using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class Phase3Tests
{
    [Fact]
    public void OptimizerMovesSingleChannelConfigurationToInit()
    {
        var configuration = SimulatedMeterConfiguration();
        var plan = Plan(
            MeasurementStep(0, new MeasureParameters(1, "VOLT:DC", 10, 1)),
            MeasurementStep(1, new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Definitions(configuration));

        Assert.Single(execution.Init.Configurations);
        Assert.All(execution.Steps, step => Assert.Null(step.ConfigureMeasurement));
        var serialized = ExecutionPlanSerializer.Serialize(execution);
        Assert.Equal(serialized, ExecutionPlanSerializer.Serialize(ExecutionPlanSerializer.Deserialize(serialized)));
    }

    [Fact]
    public void SimulatedProfileCanRepresentPlanSpecificChannels()
    {
        var configuration = DriverConfiguration.Parse("dmm", "SimulatedSwitchMultimeter",
            "{\"channels\":[1003],\"readings\":{\"1003|VOLT:DC\":3.3}}");
        var profile = new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration);
        Assert.Equal(1003, profile.GetChannel(1003).Number);
    }

    [Fact]
    public void OptimizerReconfiguresWhenRequiredStateIsNotTrackable()
    {
        var configuration = SimulatedMeterConfiguration();
        var profile = new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration);
        var partialState = new ChannelStateProfile(new HashSet<string>(["range"], StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), UnknownCommandInvalidatesAll: true);
        var partialProfile = profile with
        {
            DefaultChannelState = partialState,
            Channels = [profile.GetChannel(1) with { State = partialState }]
        };
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm1"] = new(configuration, partialProfile)
        };
        var plan = Plan(
            MeasurementStep(0, new MeasureParameters(1, "VOLT:DC", 10, 1)),
            MeasurementStep(1, new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);

        Assert.Single(execution.Init.Configurations);
        Assert.All(execution.Steps, step => Assert.NotNull(step.ConfigureMeasurement));
    }

    [Fact]
    public async Task TypedReleaseRejectsStaleSnapshotsAndFreezesOptimizerOutput()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var plan = Plan(MeasurementStep(0, new MeasureParameters(1, "VOLT:DC", 10, 1)));
        testDatabase.Context.TestPlans.Add(plan);
        await testDatabase.Context.SaveChangesAsync();
        var execution = new ExecutionPlanOptimizer().Optimize(plan, Definitions(SimulatedMeterConfiguration()));

        await Assert.ThrowsAsync<PlanConflictException>(() => testDatabase.Plans.ReleaseAsync(plan.Id,
            execution with { ArtNr = "stale" }));
        await testDatabase.Plans.ReleaseAsync(plan.Id, execution);

        Assert.Equal(PlanStatus.Released, plan.Status);
        Assert.Equal(ExecutionPlanSerializer.Serialize(execution), plan.ExecutionPlanJson);
    }

    [Fact]
    public void OptimizerConfiguresBeforeEveryChangeAndAfterInvalidatingRelayCommand()
    {
        var configuration = SimulatedMeterConfiguration();
        var plan = Plan(
            MeasurementStep(0, new MeasureParameters(1, "VOLT:DC", 10, 1)),
            new TestStep { Order = 1, Name = "Close relay", DeviceKey = "dmm1", StepType = StepType.Relay,
                ParametersJson = StepParameters.Serialize(new RelayParameters(1, true)) },
            MeasurementStep(2, new MeasureParameters(1, "VOLT:DC", 100, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Definitions(configuration));

        Assert.Empty(execution.Init.Configurations);
        Assert.NotNull(execution.Steps[0].ConfigureMeasurement);
        Assert.Null(execution.Steps[1].ConfigureMeasurement);
        Assert.NotNull(execution.Steps[2].ConfigureMeasurement);
        Assert.Equal("dmm1", execution.Cleanup.SafeStateDeviceKeys.Single());
        Assert.Equal("dmm1", execution.Cleanup.DisconnectDeviceKeys.Single());
    }

    [Fact]
    public async Task RunnerPersistsTextStepResultsAndExportsCompletedRun()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-run-{Guid.NewGuid():N}");
        try
        {
            var registry = new DriverRegistry().AddSimulatedDrivers();
            var scannerConfiguration = DriverConfiguration.Parse("scanner", "SimulatedBarcodeScanner", "{}");
            var meterConfiguration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter",
                "{\"readings\":{\"1|VOLT:DC\":3.298}}");
            var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
            {
                ["scanner"] = new(scannerConfiguration, registry.GetProfile(scannerConfiguration)),
                ["dmm1"] = new(meterConfiguration, registry.GetProfile(meterConfiguration))
            };
            var plan = Plan(MeasurementStep(0, new MeasureParameters(1, "VOLT:DC", 10, 1)), new TestStep
            {
                Order = 1, Name = "Scan serial", DeviceKey = "scanner", StepType = StepType.Scan,
                ParametersJson = StepParameters.Serialize(new ScanParameters())
            });
            var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);
            var runner = new SequenceRunner(new DriverHost(registry, NullLoggerFactory.Instance), testDatabase.Context,
                new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new NoOpInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));

            var result = await runner.RunAsync(execution, new RunContext("FAUF-1", "SER-1", "Bench 1", "Operator"));

            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            Assert.Equal(3.298, result.Run.StepResults[0].Value);
            Assert.Equal(StepOutcome.Pass, result.Run.StepResults[0].Result);
            Assert.Equal("1234567890", result.Run.StepResults[1].TextValue);
            Assert.True(File.Exists(result.ExportPath));
            var persisted = await testDatabase.Context.Runs.FindAsync(result.Run.Id);
            Assert.NotNull(persisted);
            Assert.Equal("1234567890", testDatabase.Context.RunStepResults.Single(step => step.StepType == StepType.Scan).TextValue);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.ExportPath));
            Assert.Equal("1234567890", json.RootElement.GetProperty("characteristics")[1].GetProperty("textValue").GetString());
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunnerPersistsAbortAndAlwaysCompletesStepSnapshot()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-abort-{Guid.NewGuid():N}");
        try
        {
            var plan = Plan(new TestStep
            {
                Order = 0, Name = "Long wait", StepType = StepType.Wait,
                ParametersJson = StepParameters.Serialize(new WaitParameters(10_000))
            });
            var execution = new ExecutionPlanOptimizer().Optimize(plan,
                new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal));
            var runner = new SequenceRunner(new DriverHost(new DriverRegistry().AddSimulatedDrivers(), NullLoggerFactory.Instance),
                testDatabase.Context, new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new NoOpInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var result = await runner.RunAsync(execution,
                new RunContext("FAUF-2", "SER-2", "Bench 1", "Operator"), cancellation.Token);

            Assert.Equal(RunOutcome.Aborted, result.Run.Result);
            Assert.Equal(StepOutcome.Skipped, result.Run.StepResults.Single().Result);
            Assert.NotNull(result.Run.FinishedAt);
            Assert.True(File.Exists(result.ExportPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MockSapClientReturnsConfiguredReadOnlyOrder()
    {
        var sap = new MockSapClient([new KeyValuePair<string, OrderInfo>("FAUF-3", new OrderInfo("4711", "A"))]);
        var order = await sap.GetOrderAsync("FAUF-3");
        Assert.Equal(new OrderInfo("4711", "A"), order);
        await Assert.ThrowsAsync<SapOrderNotFoundException>(() => sap.GetOrderAsync("unknown"));
    }

    private static Dictionary<string, DeviceDefinition> Definitions(DriverConfiguration configuration)
    {
        var profile = new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration);
        return new(StringComparer.Ordinal) { [configuration.DeviceKey] = new(configuration, profile) };
    }

    private static DriverConfiguration SimulatedMeterConfiguration() =>
        DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", "{}");

    private static TestPlan Plan(params TestStep[] steps) => new()
    {
        ArtNr = "4711", Fz = "A", Version = 1, Steps = steps.ToList()
    };

    private static TestStep MeasurementStep(int order, MeasureParameters parameters) => new()
    {
        Order = order,
        Name = $"Measure {order}",
        DeviceKey = "dmm1",
        StepType = StepType.Measure,
        ParametersJson = StepParameters.Serialize(parameters),
        Unit = MeasurementUnit.Volt,
        LimitLow = 0,
        LimitHigh = 5
    };

    private sealed class NoOpInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
