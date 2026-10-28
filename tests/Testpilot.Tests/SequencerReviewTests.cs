using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class SequencerReviewTests
{
    [Fact]
    public async Task ExpiredLongOperationTimeoutIsReportedAsTimeoutError()
    {
        var customSteps = new CustomStepRegistry();
        customSteps.Register("hang", new DelegateStep(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new StepExecutionData();
        }));
        var options = new SequenceOptions(TimeSpan.FromSeconds(5), false) { LongOperationTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await RunAsync(Plan(Custom(0, "hang"), WaitStep(1)), customSteps, options: options);

        Assert.Equal(RunOutcome.Error, result.Run.Result);
        Assert.Equal(StepOutcome.Error, result.Run.StepResults[0].Result);
        Assert.Equal("Timeout after 0.1 s.", result.Run.StepResults[0].ErrorMessage);
        Assert.Equal(StepOutcome.Skipped, result.Run.StepResults[1].Result);
    }

    [Fact]
    public async Task ErrorResultStopsTheSequence()
    {
        var customSteps = new CustomStepRegistry();
        customSteps.Register("error", new DelegateStep((_, _) =>
            Task.FromResult(new StepExecutionData(Result: StepOutcome.Error, ErrorMessage: "broken"))));

        var result = await RunAsync(Plan(Custom(0, "error"), WaitStep(1)), customSteps);

        Assert.Equal(RunOutcome.Error, result.Run.Result);
        Assert.Equal("broken", result.Run.StepResults[0].ErrorMessage);
        Assert.Equal(StepOutcome.Skipped, result.Run.StepResults[1].Result);
    }

    [Fact]
    public async Task NonZeroExitCodeOfExternalProgramIsAFail()
    {
        var result = await RunAsync(Plan(External(0, "cmd.exe", "/c", "exit 3"), WaitStep(1)));

        Assert.Equal(RunOutcome.Fail, result.Run.Result);
        Assert.Equal(StepOutcome.Fail, result.Run.StepResults[0].Result);
        Assert.StartsWith("Exit code 3", result.Run.StepResults[0].ErrorMessage);
        Assert.Equal(StepOutcome.Pass, result.Run.StepResults[1].Result);
    }

    [Fact]
    public async Task ExternalBatchFileIsRejected()
    {
        var result = await RunAsync(Plan(External(0, "legacy.bat")));

        Assert.Equal(RunOutcome.Error, result.Run.Result);
        Assert.Contains("batch file", result.Run.StepResults[0].ErrorMessage);
    }

    [Fact]
    public async Task MissingCustomStepHandlerFailsBeforeDevicesAreOpened()
    {
        var registry = new DriverRegistry().AddSimulatedDrivers();
        var meter = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", "{}");
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm1"] = new(meter, registry.GetProfile(meter))
        };
        var plan = Plan(new TestStep
        {
            Order = 0, Name = "Measure", DeviceKey = "dmm1", StepType = StepType.Measure, Unit = MeasurementUnit.Volt,
            ParametersJson = StepParameters.Serialize(new MeasureParameters(1, "VOLT:DC", 10, 1)), LimitLow = 0, LimitHigh = 5
        }, Custom(1, "unknown"));

        var result = await RunAsync(plan, definitions: definitions);

        Assert.Equal(RunOutcome.Error, result.Run.Result);
        Assert.Empty(result.Run.Devices);
        Assert.All(result.Run.StepResults, step => Assert.Equal(StepOutcome.Skipped, step.Result));
    }

    [Fact]
    public async Task FailedExportIsReportedAsSaveErrorAndRunIsStillStored()
    {
        var blockingFile = Path.GetTempFileName();
        try
        {
            await using var testDatabase = await TestDatabase.CreateAsync();
            var result = await RunAsync(Plan(WaitStep(0)), database: testDatabase, outputDirectory: blockingFile);

            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            Assert.Null(result.ExportPath);
            Assert.StartsWith("JSON-Export:", result.SaveError);
            Assert.NotNull(await testDatabase.Context.Runs.FindAsync(result.Run.Id));
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task SimulatedRunIsMarkedInDatabaseAndExport()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-sim-{Guid.NewGuid():N}");
        try
        {
            var result = await RunAsync(Plan(WaitStep(0)), outputDirectory: outputDirectory, simulated: true);

            Assert.True(result.Run.Simulated);
            Assert.Null(result.SaveError);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.ExportPath!));
            Assert.True(json.RootElement.GetProperty("run").GetProperty("simulated").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void StepResultToStringIsReadable()
    {
        var result = new RunStepResult
        {
            Order = 2, Name = "VCC_3V3", Value = 3.298, Unit = MeasurementUnit.Volt, LimitLow = 3.2, LimitHigh = 3.4,
            Result = StepOutcome.Pass
        };

        Assert.Equal("#2 VCC_3V3: 3.298 V [3.2 .. 3.4] -> Pass", result.ToString());
    }

    [Theory]
    [InlineData("12 34")]
    [InlineData("FAUF;del")]
    [InlineData("\"quoted\"")]
    public async Task ExternalSapClientRejectsInvalidFauf(string fauf)
    {
        var client = new ExternalProgramSapClient("sap.exe", ["{fauf}"]);

        await Assert.ThrowsAsync<SapException>(() => client.GetOrderAsync(fauf));
    }

    [Fact]
    public async Task ExternalSapClientRejectsBatchFiles()
    {
        var client = new ExternalProgramSapClient("sap.cmd", ["{fauf}"]);

        var exception = await Assert.ThrowsAsync<SapException>(() => client.GetOrderAsync("1000123"));
        Assert.Contains("batch file", exception.Message);
    }

    [Fact]
    public async Task ExternalSapClientReadsOrderFromStdout()
    {
        var client = new ExternalProgramSapClient("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "@{artNr='4711';fz='{fauf}'} | ConvertTo-Json -Compress"]);

        var order = await client.GetOrderAsync("B-7");

        Assert.Equal(new OrderInfo("4711", "B-7"), order);
    }

    [Fact]
    public async Task ExternalSapClientStopsHangingProgramAfterTimeout()
    {
        var client = new ExternalProgramSapClient("ping.exe", ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(300));

        var exception = await Assert.ThrowsAsync<SapException>(() => client.GetOrderAsync("1000123"));
        Assert.Contains("did not answer", exception.Message);
    }

    private static async Task<SequenceRunResult> RunAsync(TestPlan plan, CustomStepRegistry? customSteps = null,
        Dictionary<string, DeviceDefinition>? definitions = null, SequenceOptions? options = null,
        TestDatabase? database = null, string? outputDirectory = null, bool simulated = false)
    {
        var ownDatabase = database is null ? await TestDatabase.CreateAsync() : null;
        var ownOutput = outputDirectory is null;
        outputDirectory ??= Path.Combine(Path.GetTempPath(), $"testpilot-review-{Guid.NewGuid():N}");
        try
        {
            var execution = new ExecutionPlanOptimizer().Optimize(plan,
                definitions ?? new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal));
            var runner = new SequenceRunner(
                new DriverHost(new DriverRegistry().AddSimulatedDrivers(), NullLoggerFactory.Instance),
                (database ?? ownDatabase)!.Context, new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(customSteps ?? new CustomStepRegistry()),
                new ConfirmingInteraction(), options ?? new SequenceOptions(TimeSpan.FromSeconds(5), false));
            return await runner.RunAsync(execution, new RunContext("1000123", "1234567890", "Bench 1", "Operator",
                Simulated: simulated));
        }
        finally
        {
            if (ownDatabase is not null)
                await ownDatabase.DisposeAsync();
            if (ownOutput && Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static TestPlan Plan(params TestStep[] steps) => new() { ArtNr = "4711", Fz = "A", Version = 1, Steps = steps.ToList() };

    private static TestStep WaitStep(int order) => new()
    {
        Order = order, Name = $"Wait {order}", StepType = StepType.Wait,
        ParametersJson = StepParameters.Serialize(new WaitParameters(1))
    };

    private static TestStep Custom(int order, string handlerKey) => new()
    {
        Order = order, Name = $"Custom {order}", StepType = StepType.CustomCode,
        ParametersJson = StepParameters.Serialize(new CustomCodeParameters(handlerKey, JsonDocument.Parse("{}").RootElement.Clone()))
    };

    private static TestStep External(int order, string command, params string[] arguments) => new()
    {
        Order = order, Name = $"External {order}", StepType = StepType.External,
        ParametersJson = StepParameters.Serialize(new ExternalParameters(command, arguments))
    };

    private sealed class DelegateStep(Func<JsonElement, CancellationToken, Task<StepExecutionData>> execute) : ICustomStep
    {
        public Task<StepExecutionData> ExecuteAsync(JsonElement parameters, DeviceSession devices, RunContext runContext,
            CancellationToken cancellationToken) => execute(parameters, cancellationToken);
    }

    private sealed class ConfirmingInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
