using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

/// <summary>The rebuilt legacy FB-HAT test (PS05, 4001430-01 / 332351): seed, release and runs on PS05-Sim.</summary>
public sealed class Ps05FbHatPlanTests
{
    [Fact]
    public async Task SeedAddsTheDraftOnceAndOnlyWhenTheTypeExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        Assert.Empty(await Ps05FbHatPlanSeed.SeedAsync(database.Context));

        await SeedAllAsync(database.Context);
        Assert.Empty(await Ps05FbHatPlanSeed.SeedAsync(database.Context));

        await using var verify = database.NewContext();
        var plan = await verify.TestPlans.Include(item => item.Steps).SingleAsync();
        Assert.Equal((Ps05FbHatPlanSeed.ArtNr, Ps05FbHatPlanSeed.Fz, 1, PlanStatus.Draft), (plan.ArtNr, plan.Fz, plan.Version, plan.Status));
        Assert.Equal((await verify.TestSystemTypes.SingleAsync(type => type.Name == "PS05")).Id, plan.TestSystemTypeId);
        Assert.Equal(["Firm_ver_dut", "PROJFILE_PATH_1", "PROGFILE_PATH_1", "ERRORFILE_PATH_1", "START_ADDRESS_1"],
            NamedSettings.Deserialize(plan.SettingsJson).Select(setting => setting.Alias));
    }

    [Fact]
    public void PlanContainsEveryLegacyCharacteristicWithValidStepsAndFormulas()
    {
        var plan = Ps05FbHatPlanSeed.CreatePlan(null);
        var steps = plan.Steps.OrderBy(step => step.Order).ToList();
        Assert.Equal(Enumerable.Range(0, steps.Count), steps.Select(step => step.Order));
        foreach (var step in steps)
            StepParameters.Validate(step);

        // Characteristics of the legacy test report gen_tex_pass_4001430-01_332351.
        string[] expected =
        [
            "2.1.1", "2.2.1", "2.2.2", "2.2.3", "2.2.4", "2.3.1", "2.3.2",
            .. Enumerable.Range(1, 18).Select(number => $"2.4.{number}"),
            "2.5.1", "2.6.1", "2.6.2", "2.6.3", "2.6.4", "2.6.5", "2.6.6", "2.7.1", "2.8.1", "2.8.5",
            "2.9.1", "2.9.5", "2.9.6", "2.9.7", "2.9.8", "2.10.1", "2.10.2", "2.11.1", "2.11.2", "2.12.1", "2.13.1",
            "2.15.1", "2.16.1", "2.16.2", "2.16.4", "2.16.5", "2.16.7", "2.16.8",
            "2.18.1", "2.18.2", "2.19.1", "2.19.2", "2.20.1", "2.20.2"
        ];
        var numbered = steps.Where(step => step.Name.StartsWith("2.", StringComparison.Ordinal))
            .Select(step => step.Name.Split(' ')[0]).ToList();
        Assert.Equal(expected, numbered);
        Assert.All(steps.Where(step => step.Name.StartsWith("2.", StringComparison.Ordinal) && step.StepType != StepType.Program),
            step => Assert.True(step.LimitLow.HasValue || step.LimitHigh.HasValue || step.StepType == StepType.DutCommand,
                $"{step.Name} has no limits."));

        // Every reference must name a value of an earlier step (or of the command itself), so the run cannot fail on
        // a typing error in a step name.
        var known = new HashSet<string>(["serial", "fauf"], StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var own = new HashSet<string>(StringComparer.Ordinal);
            var formulas = new List<string>();
            if (step.StepType == StepType.Calculate)
                formulas.Add(StepParameters.Deserialize<CalculateParameters>(step.ParametersJson).Formula);
            if (step.StepType == StepType.DutCommand)
            {
                var command = StepParameters.Deserialize<DutCommandParameters>(step.ParametersJson);
                var operation = FsaOperations.GetRequired(command.Operation, command.Arguments?.Length ?? 0);
                own.UnionWith(operation.Outputs);
                formulas.AddRange(command.Arguments ?? []);
                if (command.Output is not null) formulas.Add(command.Output);
                foreach (var output in operation.Outputs)
                    known.Add($"{step.Name}:{output}");
            }
            foreach (var reference in formulas.SelectMany(Formula.Validate))
                Assert.True(known.Contains(reference) || own.Contains(reference), $"{step.Name}: unknown reference {{{reference}}}.");
            if (StepParameters.HasValue(step.StepType))
                known.Add(step.Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedPlanPassesOnTheSimulationStation(bool simulationEnabled)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAllAsync(database.Context);
        await ReleaseWithoutWaitsAsync(database);
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo(Ps05FbHatPlanSeed.ArtNr, Ps05FbHatPlanSeed.Fz))]);
        var registry = new DriverRegistry().AddAllDrivers();
        var preparation = new RunPreparation(database.Context, sap, registry, simulationEnabled);
        var prepared = await preparation.PrepareForOrderAsync(StationSeed.Ps05SimulationStation, "FAUF-1");
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-fbhat-{Guid.NewGuid():N}");
        try
        {
            var runner = new SequenceRunner(new DriverHost(registry, NullLoggerFactory.Instance), database.Context,
                new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new ContinueInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));

            var result = await runner.RunAsync(prepared.Plan, prepared.CreateContext("1234567890", ""));

            var results = result.Run.StepResults.OrderBy(step => step.Order).ToList();
            Assert.Null(result.SaveError);
            Assert.Equal(prepared.Plan.Steps.Count, results.Count);
            Assert.Empty(results.Where(step => step.Result != StepOutcome.Pass).Select(step => step.ToString()));
            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            double Value(string number)
            {
                var step = results.SingleOrDefault(step => step.Name.StartsWith(number + " ", StringComparison.Ordinal));
                Assert.True(step is not null, $"{number} missing in: {string.Join(" | ", results.Select(item => item.Name))}");
                return step.Value!.Value;
            }
            Assert.Equal(0.05, Value("2.3.1"));
            Assert.Equal(4e-6, Value("2.5.1"), 12);
            Assert.Equal(0, Value("2.6.6"), 6);
            Assert.Equal(-60, Value("2.8.1"));
            Assert.Equal(-54, Value("2.9.5"));
            Assert.Equal(-105, Value("2.9.7"));
            Assert.Equal("1.6.20", results.Single(step => step.Name.StartsWith("2.2.4 ", StringComparison.Ordinal)).TextValue);
            Assert.True(File.Exists(result.ExportPath));
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Ps05Dash1BindsTheRealDriversWithStationAndPlanSettings()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAllAsync(database.Context);
        await ReleaseWithoutWaitsAsync(database);
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo(Ps05FbHatPlanSeed.ArtNr, Ps05FbHatPlanSeed.Fz))]);
        var preparation = new RunPreparation(database.Context, sap, new DriverRegistry().AddAllDrivers(), false);

        var prepared = await preparation.PrepareForOrderAsync("PS05-1", "FAUF-1");

        string Connection(string key) => prepared.Plan.Devices.Single(device => device.DeviceKey == key).ConnectionParametersJson!;
        Assert.Equal("""{"port":"COM9"}""", Connection("Dut"));
        Assert.Equal("""{"port":"COM3"}""", Connection("GoldenSample"));
        using var jflash = JsonDocument.Parse(Connection("JLink"));
        Assert.Equal(@"C:\Program Files (x86)\SEGGER\JLink_V612f\JFlash.exe", jflash.RootElement.GetProperty("executablePath").GetString());
        const string firmware = @"\\heka.de\prod\pruefmittel\ATE\PS05\cvi_prog\11010\11010_329431\Firmware\";
        Assert.Equal(
        [
            $"-openprj{firmware}FB_FSA.jflash", "-open{{imagePath}},0x8000000", $"-jflashlog{firmware}segger1_error.log",
            "-usb850102034", "-auto", "-exit"
        ], jflash.RootElement.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()));
        var program = prepared.Plan.Steps.Single(step => step.StepType == StepType.Program);
        Assert.Equal(firmware + "FW_FSA_155_F_V01.06.20.bin",
            StepParameters.Deserialize<ProgramParameters>(program.ParametersJson).FilePath);
        var firmwareCheck = prepared.Plan.Steps.Single(step => step.Name.StartsWith("2.2.4 ", StringComparison.Ordinal));
        Assert.Equal("1.6.20", StepParameters.Deserialize<DutCommandParameters>(firmwareCheck.ParametersJson).Expected);
    }

    [Fact]
    public async Task SeedAddsTheNewDevicesToAnExistingPs05Dash1()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAllAsync(database.Context);
        string[] newKeys = ["Dut", "GoldenSample", "JLink", "Scanner"];
        await database.Context.StationDevices.Where(device => newKeys.Contains(device.DeviceKey)).ExecuteDeleteAsync();
        await database.Context.SystemTypeDevices.Where(device => newKeys.Contains(device.DeviceKey)).ExecuteDeleteAsync();

        await using (var db = database.NewContext())
            Assert.Equal(["PS05: 4 Geräte", "PS05-1: 4 Geräte", "PS05-Sim: 4 Geräte"], await StationSeed.SeedAsync(db));

        await using var verify = database.NewContext();
        var station = await verify.Stations.Include(item => item.Devices).SingleAsync(item => item.Name == "PS05-1");
        Assert.Equal(["Ag34970A", "Dut", "GoldenSample", "Hmp2030", "JLink", "Ps05Adapter", "Scanner"],
            station.Devices.Select(device => device.DeviceKey).Order(StringComparer.Ordinal));
        Assert.Equal("FsaCommunication", station.Devices.Single(device => device.DeviceKey == "Dut").Driver);
    }

    private static async Task SeedAllAsync(TestpilotDbContext db)
    {
        await TestSystemTypeSeed.SeedAsync(db);
        await StationSeed.SeedAsync(db);
        Assert.Equal([$"Prüfplan {Ps05FbHatPlanSeed.ArtNr} / {Ps05FbHatPlanSeed.Fz} (Entwurf)"], await Ps05FbHatPlanSeed.SeedAsync(db));
    }

    /// <summary>Releases the seeded draft with all waits set to 0 ms, so the run takes milliseconds instead of ~25 s.</summary>
    private static async Task ReleaseWithoutWaitsAsync(TestDatabase database)
    {
        var plan = await database.Context.TestPlans.Include(item => item.Steps).SingleAsync();
        foreach (var step in plan.Steps)
        {
            if (step.StepType == StepType.Wait)
                step.ParametersJson = StepParameters.Serialize(new WaitParameters(0));
            step.RetryDelayMilliseconds = 0;
        }
        await database.Context.SaveChangesAsync();
        var type = await database.Context.TestSystemTypes.Include(item => item.Devices).SingleAsync(item => item.Name == "PS05");
        await database.Plans.ReleaseAsync(plan.Id,
            new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(type)));
    }

    private sealed class ContinueInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
