using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

/// <summary>Samples, retry, DUT commands and calculations as used by the FB-HAT plan, on simulated devices.</summary>
public sealed class StepBuildingBlockTests
{
    private const string Meter = """
        {"channels":[101,102],"readings":{"102|VOLT:DC":3.0},"sequences":{"101|VOLT:DC":[5,1,2,9,3]}}
        """;
    private const string DutConnection = """{"firmware":"1.6.20","genericData":{"9|5":[5]},"toggleCommands":["5|5"]}""";

    [Fact]
    public async Task MeasureAveragesTheSamplesWithoutTheHighest()
    {
        var results = await RunAsync(Measure("U", 101, 0, 10, samples: 5, discardHighest: 2));

        Assert.Equal(2, results[0].Value);
        Assert.Equal(StepOutcome.Pass, results[0].Result);
        Assert.Null(results[0].TextValue);
    }

    [Theory]
    [InlineData(0, StepOutcome.Fail, 5)]
    [InlineData(1, StepOutcome.Pass, 1)]
    public async Task RetryRepeatsAFailedStepAndOnlyTheLastAttemptCounts(int retryCount, StepOutcome expected, double value)
    {
        var step = Measure("U", 101, 0, 2);
        step.RetryCount = retryCount;
        step.RetryDelayMilliseconds = 1;

        var results = await RunAsync(step);

        Assert.Equal(expected, results[0].Result);
        Assert.Equal(value, results[0].Value);
    }

    [Theory]
    [InlineData("1.6.20", StepOutcome.Pass)]
    [InlineData("1.6.21", StepOutcome.Fail)]
    public async Task DutCommandComparesTheFormattedText(string expectedText, StepOutcome expected)
    {
        var results = await RunAsync(Dut("Firmware", new DutCommandParameters("ReadFirmwareVersion",
            Text: "{major}.{minor}.{deb}", Expected: expectedText)));

        Assert.Equal(expected, results[0].Result);
        Assert.Equal("1.6.20", results[0].TextValue);
    }

    [Fact]
    public async Task DutCommandOutputsAndRunValuesAreUsedByLaterSteps()
    {
        var results = await RunAsync(
            Dut("Schreiben", new DutCommandParameters("WriteMainConfigBuffer", ["32", "{serial}"])),
            Dut("Lesen", new DutCommandParameters("ReadMainConfigBuffer")),
            Calculate("Seriennummer", "{Lesen:serial} == {serial}", 1, 1),
            Dut("XO", new DutCommandParameters("SendGenericTestCommand", ["9", "5"], "{data[0]}"), 3, 7),
            Dut("Taster gedrückt", new DutCommandParameters("SendGenericTestCommand", ["5", "5"], "{data[0]}"), 0, 0),
            Dut("Taster los", new DutCommandParameters("SendGenericTestCommand", ["5", "5"], "{data[0]}"), 1, 1),
            Measure("VCC", 102, null, null),
            Calculate("Verhältnis", "{VCC} / 3", 1, 1));

        Assert.All(results, result => Assert.Equal(StepOutcome.Pass, result.Result));
        Assert.Equal(1, results[0].Value);
        Assert.Equal(5, results[3].Value);
        Assert.Equal([0d, 1d], results.Where(result => result.Name.StartsWith("Taster", StringComparison.Ordinal))
            .Select(result => result.Value!.Value));
    }

    [Theory]
    [InlineData("1 / 0", "not finite")]
    [InlineData("{Unbekannt} + 1", "Unbekannt")]
    public async Task CalculationErrorsStopTheRun(string formula, string expectedMessage)
    {
        var results = await RunAsync(Calculate("Rechnung", formula, 0, 1), Measure("VCC", 102, null, null));

        Assert.Equal(StepOutcome.Error, results[0].Result);
        Assert.Contains(expectedMessage, results[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(StepOutcome.Skipped, results[1].Result);
    }

    [Fact]
    public void InvalidBuildingBlocksAreRejectedWhenTheStepIsValidated()
    {
        static void Invalid(TestStep step) => Assert.ThrowsAny<ArgumentException>(() => StepParameters.Validate(step));

        Invalid(Measure("U", 101, null, null, samples: 2, discardHighest: 2));
        Invalid(Measure("U", 101, null, null, samples: 101));
        Invalid(Calculate("Rechnung", "1 +", null, null));
        Invalid(Dut("Firmware", new DutCommandParameters("ReadFirmwareVersion", Expected: "1.6.20")));
        Invalid(Dut("Firmware", new DutCommandParameters("ReadFirmwareVersion", Output: "{major}", Text: "{major}",
            Expected: "1")));
        var retry = Measure("U", 101, null, null);
        retry.RetryCount = 1001;
        Invalid(retry);
    }

    private static async Task<IReadOnlyList<RunStepResult>> RunAsync(params TestStep[] steps)
    {
        for (var index = 0; index < steps.Length; index++)
            steps[index].Order = index;
        var registry = new DriverRegistry().AddSimulatedDrivers();
        var meter = DriverConfiguration.Parse("dmm", "SimulatedSwitchMultimeter", Meter);
        var dut = DriverConfiguration.Parse("dut", "SimulatedDutCommunication", DutConnection);
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm"] = new(meter, registry.GetProfile(meter)),
            ["dut"] = new(dut, registry.GetProfile(dut))
        };
        var plan = new TestPlan { ArtNr = "4711", Fz = "A", Steps = [.. steps] };
        var execution = new ExecutionPlanOptimizer().Optimize(plan, definitions);
        await using var testDatabase = await TestDatabase.CreateAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-blocks-{Guid.NewGuid():N}");
        try
        {
            var runner = new SequenceRunner(new DriverHost(registry, NullLoggerFactory.Instance), testDatabase.Context,
                new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new ContinueInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));
            var result = await runner.RunAsync(execution, new RunContext("FAUF-1", "1234567890", "Bench 1", ""));
            Assert.Null(result.SaveError);
            return result.Run.StepResults.OrderBy(step => step.Order).ToList();
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static TestStep Measure(string name, int channel, double? low, double? high, int? samples = null,
        int? discardHighest = null) => new()
    {
        Name = name, StepType = StepType.Measure, DeviceKey = "dmm", Unit = MeasurementUnit.Volt, LimitLow = low,
        LimitHigh = high,
        ParametersJson = StepParameters.Serialize(new MeasureParameters(channel, "VOLT:DC", null, 1, samples, discardHighest))
    };

    private static TestStep Dut(string name, DutCommandParameters parameters, double? low = null, double? high = null) => new()
    {
        Name = name, StepType = StepType.DutCommand, DeviceKey = "dut", LimitLow = low, LimitHigh = high,
        Unit = low.HasValue ? MeasurementUnit.Number : null, ParametersJson = StepParameters.Serialize(parameters)
    };

    private static TestStep Calculate(string name, string formula, double? low, double? high) => new()
    {
        Name = name, StepType = StepType.Calculate, LimitLow = low, LimitHigh = high, Unit = MeasurementUnit.Number,
        ParametersJson = StepParameters.Serialize(new CalculateParameters(formula))
    };

    private sealed class ContinueInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
