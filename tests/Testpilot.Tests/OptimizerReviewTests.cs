using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class OptimizerReviewTests
{
    private static DeviceProfile WithState(DeviceProfile profile, ChannelStateProfile state) => profile with
    {
        DefaultChannelState = state,
        Channels = profile.Channels.Select(channel => channel with { State = state }).ToArray()
    };

    [Fact]
    public void ChangingFromFixedRangeToAutoRangeReconfiguresAndSerializes()
    {
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            Measure(1, "dmm1", new MeasureParameters(1, "VOLT:DC", null, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Meter());

        Assert.Empty(execution.Init.Configurations);
        Assert.Equal(10, execution.Steps[0].ConfigureMeasurement!.Range);
        Assert.NotNull(execution.Steps[1].ConfigureMeasurement);
        Assert.Null(execution.Steps[1].ConfigureMeasurement!.Range);
        var json = ExecutionPlanSerializer.Serialize(execution);
        Assert.Equal(json, ExecutionPlanSerializer.Serialize(ExecutionPlanSerializer.Deserialize(json)));
    }

    [Fact]
    public void SameConfigurationIsRepeatedAfterInvalidatingRelayCommand()
    {
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            Relay(1, "dmm1", 2),
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Meter());

        Assert.Single(execution.Init.Configurations);
        Assert.Null(execution.Steps[0].ConfigureMeasurement);
        Assert.NotNull(execution.Steps[2].ConfigureMeasurement);
    }

    [Fact]
    public void CommandThatInvalidatesNothingKeepsTheConfiguration()
    {
        var state = new ChannelStateProfile(
            new HashSet<string>(["function", "range", "nplc"], StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["relay"] = new HashSet<string>(StringComparer.Ordinal)
            }, UnknownCommandInvalidatesAll: true);
        var definitions = Meter(profile => WithState(profile, state));
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            Relay(1, "dmm1", 2),
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);

        Assert.Single(execution.Init.Configurations);
        Assert.All(execution.Steps, step => Assert.Null(step.ConfigureMeasurement));
    }

    [Fact]
    public void AlternatingConfigurationsOnOneChannelAreConfiguredEveryTime()
    {
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            Measure(1, "dmm1", new MeasureParameters(1, "RES", 1000, 1)),
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Meter());

        Assert.Empty(execution.Init.Configurations);
        Assert.All(execution.Steps, step => Assert.NotNull(step.ConfigureMeasurement));
    }

    [Fact]
    public void ExternalProgramInvalidatesEveryDevice()
    {
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            new TestStep
            {
                Order = 1, Name = "Legacy tool", StepType = StepType.External,
                ParametersJson = StepParameters.Serialize(new ExternalParameters("tool.exe", []))
            },
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Meter());

        Assert.NotNull(execution.Steps[2].ConfigureMeasurement);
    }

    [Fact]
    public void CustomCodeInvalidatesEveryDeviceEvenIfTheProfileListsNothing()
    {
        var state = new ChannelStateProfile(
            new HashSet<string>(["function", "range", "nplc"], StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), UnknownCommandInvalidatesAll: false);
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            new TestStep
            {
                Order = 1, Name = "Special", StepType = StepType.CustomCode,
                ParametersJson = StepParameters.Serialize(new CustomCodeParameters("special",
                    System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()))
            },
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, Meter(profile => WithState(profile, state)));

        Assert.NotNull(execution.Steps[2].ConfigureMeasurement);
    }

    [Fact]
    public void ProfileCanDeclareThatMeasuringAnotherChannelInvalidatesState()
    {
        var state = new ChannelStateProfile(
            new HashSet<string>(["function", "range", "nplc"], StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["configure"] = new HashSet<string>(StringComparer.Ordinal),
                ["measure"] = new HashSet<string>(["nplc"], StringComparer.Ordinal)
            }, UnknownCommandInvalidatesAll: true);
        var definitions = Meter(profile => WithState(profile, state));
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
            Measure(1, "dmm1", new MeasureParameters(2, "VOLT:DC", 10, 1)),
            Measure(2, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);

        Assert.Equal(2, execution.Init.Configurations.Count);
        Assert.Null(execution.Steps[0].ConfigureMeasurement);
        Assert.NotNull(execution.Steps[1].ConfigureMeasurement);
        Assert.NotNull(execution.Steps[2].ConfigureMeasurement);
    }

    [Fact]
    public void OptimizerIsDeterministic()
    {
        var plan = Plan(
            Measure(0, "dmm1", new MeasureParameters(2, "VOLT:DC", 10, 1)),
            Measure(1, "dmm1", new MeasureParameters(1, "RES", 1000, 1)),
            Relay(2, "dmm1", 1),
            Measure(3, "dmm1", new MeasureParameters(2, "VOLT:DC", null, null)));

        var first = ExecutionPlanSerializer.Serialize(new ExecutionPlanOptimizer().Optimize(plan, Meter()));
        var second = ExecutionPlanSerializer.Serialize(new ExecutionPlanOptimizer().Optimize(plan, Meter()));

        Assert.Equal(first, second);
    }

    [Fact]
    public void CleanupSwitchesPowerOffBeforeOpeningRelays()
    {
        var registry = new DriverRegistry().AddSimulatedDrivers();
        var meter = DriverConfiguration.Parse("a-dmm", "SimulatedSwitchMultimeter", "{}");
        var power = DriverConfiguration.Parse("z-psu", "SimulatedPowerSupply", "{}");
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["a-dmm"] = new(meter, registry.GetProfile(meter)),
            ["z-psu"] = new(power, registry.GetProfile(power))
        };
        var plan = Plan(
            new TestStep
            {
                Order = 0, Name = "Power on", DeviceKey = "z-psu", StepType = StepType.SetPower,
                ParametersJson = StepParameters.Serialize(new SetPowerParameters(5, 0.1, true))
            },
            Measure(1, "a-dmm", new MeasureParameters(1, "VOLT:DC", 10, 1)));

        var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);

        Assert.Equal(["z-psu", "a-dmm"], execution.Cleanup.SafeStateDeviceKeys);
    }

    [Fact]
    public async Task RunnerMeasuresTwoChannelsConfiguredDuringInit()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-init-{Guid.NewGuid():N}");
        try
        {
            var registry = new DriverRegistry().AddSimulatedDrivers();
            var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter",
                "{\"channels\":[1,2],\"readings\":{\"1|VOLT:DC\":3.3,\"2|VOLT:DC\":1.2}}");
            var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
            {
                ["dmm1"] = new(configuration, registry.GetProfile(configuration))
            };
            var plan = Plan(
                Measure(0, "dmm1", new MeasureParameters(1, "VOLT:DC", 10, 1)),
                Measure(1, "dmm1", new MeasureParameters(2, "VOLT:DC", 10, 1)));
            var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);
            Assert.Equal(2, execution.Init.Configurations.Count);
            var runner = new SequenceRunner(new DriverHost(registry, NullLoggerFactory.Instance), testDatabase.Context,
                new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new ContinueInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));

            var result = await runner.RunAsync(execution, new RunContext("FAUF-9", "1234567890", "Bench 1", "Operator"));

            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            Assert.Equal(3.3, result.Run.StepResults[0].Value);
            Assert.Equal(1.2, result.Run.StepResults[1].Value);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static Dictionary<string, DeviceDefinition> Meter(Func<DeviceProfile, DeviceProfile>? change = null)
    {
        var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", "{\"channels\":[1,2]}");
        var profile = new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration);
        return new(StringComparer.Ordinal) { ["dmm1"] = new(configuration, change?.Invoke(profile) ?? profile) };
    }

    private static TestPlan Plan(params TestStep[] steps) => new() { ArtNr = "4711", Fz = "A", Version = 1, Steps = steps.ToList() };

    private static TestStep Measure(int order, string device, MeasureParameters parameters) => new()
    {
        Order = order, Name = $"Measure {order}", DeviceKey = device, StepType = StepType.Measure,
        ParametersJson = StepParameters.Serialize(parameters), LimitLow = 0, LimitHigh = 5,
        Unit = parameters.Function is "RES" or "FRES" ? MeasurementUnit.Ohm : MeasurementUnit.Volt
    };

    private static TestStep Relay(int order, string device, int channel) => new()
    {
        Order = order, Name = $"Relay {order}", DeviceKey = device, StepType = StepType.Relay,
        ParametersJson = StepParameters.Serialize(new RelayParameters(channel, true))
    };

    private sealed class ContinueInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
