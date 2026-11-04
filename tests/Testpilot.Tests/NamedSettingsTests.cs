using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Editor.Services;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class NamedSettingsTests
{
    [Fact]
    public void SettingsRoundTripWindowsAndUncPaths()
    {
        NamedSetting[] settings =
        [
            Setting("COMPort_Debug", "COM9"),
            Setting("JFlashExecutablePath", @"C:\Program Files (x86)\SEGGER\JLink_V612f\JFlash.exe"),
            Setting("PROGFILE_PATH_1", @"\\heka.de\prod\pruefmittel\Firmware\FW.bin")
        ];
        Assert.Equal(settings, NamedSettings.Deserialize(NamedSettings.Serialize(settings)));
    }

    [Theory]
    [InlineData(SettingDataType.Boolean, "No")]
    [InlineData(SettingDataType.Integer, "1.5")]
    [InlineData(SettingDataType.Integer, "9223372036854775808")]
    [InlineData(SettingDataType.Number, "1,5")]
    [InlineData(SettingDataType.Number, "NaN")]
    [InlineData(SettingDataType.Number, "Infinity")]
    [InlineData((SettingDataType)99, "x")]
    public void InvalidTypedValuesAreRejected(SettingDataType type, string value) =>
        Assert.Throws<ArgumentException>(() => NamedSettings.Serialize([Setting("Value", value, type)]));

    [Theory]
    [InlineData("")]
    [InlineData("COM-Port DUT")]
    [InlineData("1Port")]
    [InlineData("Port.Name")]
    public void InvalidAliasesAreRejected(string alias) =>
        Assert.Throws<ArgumentException>(() => NamedSettings.Serialize([Setting(alias, "COM9")]));

    [Fact]
    public void AliasesAreUniqueIgnoringCaseAndCannotReferenceOtherSettings()
    {
        Assert.Throws<ArgumentException>(() => NamedSettings.Serialize([Setting("Port", "COM9"), Setting("PORT", "COM3")]));
        Assert.Throws<ArgumentException>(() => NamedSettings.Serialize([Setting("Port", "{{station.OtherPort}}") ]));
        Assert.Throws<JsonException>(() => NamedSettings.Deserialize("[{\"alias\":\"A\"}]"));
    }

    [Fact]
    public void ResolverPreservesTypesEscapingScopesAndOtherTokens()
    {
        var resolver = new NamedSettingsResolver(
            [Setting("PORT", "plan-value"), Setting("Enabled", "false", SettingDataType.Boolean),
             Setting("Count", "12", SettingDataType.Integer), Setting("Threshold", "1.5", SettingDataType.Number)],
            [Setting("Port", "COM9"), Setting("Path", "C:\\folder with spaces\\\"quoted\"\\file.bin")]);
        using var culture = new CultureScope("de-DE");
        using var document = JsonDocument.Parse(resolver.ResolveJson("""
            {"{{station.Port}}":"{{station.port}}", "plan":"{{plan.port}}", "enabled":"{{plan.Enabled}}",
             "nested":["{{plan.Count}}", {"limit":"{{plan.Threshold}}"}],
             "argument":"x={{plan.Threshold}};{{station.Port}}", "path":"{{station.Path}}", "image":"{{imagePath}}"}
            """));
        var root = document.RootElement;
        Assert.Equal("COM9", root.GetProperty("{{station.Port}}").GetString());
        Assert.Equal("plan-value", root.GetProperty("plan").GetString());
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, root.GetProperty("nested")[0].GetInt32());
        Assert.Equal(1.5, root.GetProperty("nested")[1].GetProperty("limit").GetDouble());
        Assert.Equal("x=1.5;COM9", root.GetProperty("argument").GetString());
        Assert.Equal("C:\\folder with spaces\\\"quoted\"\\file.bin", root.GetProperty("path").GetString());
        Assert.Equal("{{imagePath}}", root.GetProperty("image").GetString());
    }

    [Theory]
    [InlineData("{{station.Missing}}")]
    [InlineData("{{plan.Missing}}")]
    [InlineData("{{station.COM-Port}}")]
    [InlineData("{{station.Port}")]
    [InlineData("{{Plan.Port}}")]
    public void MissingAndMalformedReferencesFailExplicitly(string reference)
    {
        var resolver = new NamedSettingsResolver([], []);
        var exception = Assert.Throws<ArgumentException>(() => resolver.ResolveJson(JsonSerializer.Serialize(new { value = reference })));
        Assert.Contains(reference, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredStationReferencesStaySymbolicButPlanReferencesAreChecked()
    {
        var resolver = new NamedSettingsResolver([Setting("File", "firmware.bin")], []);
        using var document = JsonDocument.Parse(resolver.ResolveJson(
            """{"path":"{{plan.File}}","port":"{{station.Port}}"}""", deferStation: true));
        Assert.Equal("firmware.bin", document.RootElement.GetProperty("path").GetString());
        Assert.Equal("{{station.Port}}", document.RootElement.GetProperty("port").GetString());
        Assert.Throws<ArgumentException>(() => resolver.ResolveJson("""{"path":"{{plan.Unknown}}"}""", deferStation: true));
    }

    [Fact]
    public void OlderExecutionSnapshotsWithoutSettingsRemainReadable()
    {
        var execution = TestDatabase.Optimize(TestDatabase.Draft());
        var json = JsonNode.Parse(ExecutionPlanSerializer.Serialize(execution))!.AsObject();
        Assert.True(json.Remove("settings"));
        Assert.Empty(ExecutionPlanSerializer.Deserialize(json.ToJsonString()).Settings);
    }

    [Fact]
    public async Task UpgradePreservesExistingStationsAndReleasedPlans()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new SqliteTestpilotDbContext(new DbContextOptionsBuilder<SqliteTestpilotDbContext>().UseSqlite(connection).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261104075608_DebugUsers");
        var plan = TestDatabase.Draft();
        var execution = ExecutionPlanSerializer.Serialize(TestDatabase.Optimize(plan));
        var stationId = Guid.NewGuid();
        var now = DatabaseClock.UtcNow();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Stations (Id, Name, Description) VALUES ({stationId}, {"PS05-1"}, {"Existing station"})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TestPlans (Id, ArtNr, Fz, Version, Status, ExecutionPlanJson, CreatedAt, UpdatedAt, ReleasedAt) VALUES ({plan.Id}, {plan.ArtNr}, {plan.Fz}, {plan.Version}, {1}, {execution}, {now}, {now}, {now})");

        await migrator.MigrateAsync();

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("[]", (await context.Stations.SingleAsync()).SettingsJson);
        var stored = await context.TestPlans.SingleAsync();
        Assert.Equal("[]", stored.SettingsJson);
        Assert.Equal(PlanStatus.Released, stored.Status);
        Assert.Equal(execution, stored.ExecutionPlanJson);
    }

    [Fact]
    public async Task SettingsAreFrozenProtectedAndCopiedIntoANewVersion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var draft = TestDatabase.Draft();
        draft.SettingsJson = NamedSettings.Serialize([Setting("PROGFILE_PATH_1", "firmware.bin")]);
        database.Context.TestPlans.Add(draft);
        await database.Context.SaveChangesAsync();
        await database.ReleaseAsync(draft);

        var frozen = ExecutionPlanSerializer.Deserialize(draft.ExecutionPlanJson!);
        Assert.Equal(NamedSettings.Deserialize(draft.SettingsJson), frozen.Settings);
        var next = await database.Plans.CreateNewVersionAsync(draft.Id);
        Assert.Equal(draft.SettingsJson, next.SettingsJson);
        Assert.Equal(PlanStatus.Draft, next.Status);
        Assert.Equal(2, next.Version);

        draft.SettingsJson = "[]";
        await Assert.ThrowsAsync<PlanConflictException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task ChangingSettingsInvalidatesTheReleasePreview()
    {
        await using var database = await TestDatabase.CreateAsync();
        var draft = TestDatabase.Draft();
        database.Context.TestPlans.Add(draft);
        await database.Context.SaveChangesAsync();
        var preview = TestDatabase.Optimize(draft);
        draft.SettingsJson = NamedSettings.Serialize([Setting("Firmware", "new.bin")]);
        await database.Context.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<PlanConflictException>(() => database.Plans.ReleaseAsync(draft.Id, preview));
        Assert.Contains("Einstellungen", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizerRejectsMissingPlanReferencesAndInvalidResolvedTypes()
    {
        var plan = new TestPlan
        {
            ArtNr = "4711", Fz = "A",
            Steps = [new TestStep { Name = "Prompt", StepType = StepType.OperatorPrompt,
                ParametersJson = StepParameters.Serialize(new OperatorPromptParameters("{{plan.Message}}")) }]
        };
        Assert.Throws<ArgumentException>(() => new ExecutionPlanOptimizer().Optimize(plan, new Dictionary<string, DeviceDefinition>()));
        plan.SettingsJson = NamedSettings.Serialize([Setting("Message", "false", SettingDataType.Boolean)]);
        Assert.Throws<JsonException>(() => new ExecutionPlanOptimizer().Optimize(plan, new Dictionary<string, DeviceDefinition>()));
    }

    [Fact]
    public void StationCopyAndYamlRoundTripKeepSettingsAndSymbolicConnections()
    {
        var station = new Station
        {
            Name = "PS05-1",
            SettingsJson = NamedSettings.Serialize([Setting("COMPort_Datalogic", "COM4")]),
            Devices = [new StationDevice { DeviceKey = "Matrix200", Driver = "DatalogicMatrixSerial",
                ConnectionParametersJson = """{"model":"Matrix200","portName":"{{station.COMPort_Datalogic}}"}""" }]
        };
        var copy = StationCopy.Copy(station, "PS05-2", null);
        Assert.Equal(station.SettingsJson, copy.SettingsJson);
        var yaml = new StationYamlService();
        var imported = yaml.Import(yaml.Export(copy));
        Assert.Equal(copy.SettingsJson, imported.SettingsJson);
        Assert.Equal(station.Devices[0].ConnectionParametersJson, imported.Devices[0].ConnectionParametersJson);
        Assert.Equal("PS05-2", imported.Name);
    }

    [Fact]
    public async Task StationSettingsAreValidatedOnDatabaseSave()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Stations.Add(new Station { Name = "PS05-1", SettingsJson = "null" });
        await Assert.ThrowsAsync<ArgumentException>(() => database.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneReleasedPlanUsesEachStationsPortsAndFrozenFirmware(bool simulation)
    {
        await using var database = await TestDatabase.CreateAsync();
        var (plan, type) = await CreateProgramPlanAsync(database);
        AddStation(database, type, "PS05-1", "COM4");
        AddStation(database, type, "PS05-2", "COM7");
        await database.Context.SaveChangesAsync();
        var preparation = Prepare(database, plan, simulation);

        var first = await preparation.PrepareForOrderAsync("PS05-1", "FAUF-1");
        var second = await preparation.PrepareForOrderAsync("PS05-2", "FAUF-1");

        Assert.Equal(@"\\heka.de\firmware\FW.bin", StepParameters.Deserialize<ProgramParameters>(first.Plan.Steps[0].ParametersJson).FilePath);
        Assert.Equal(first.Plan.Steps, second.Plan.Steps);
        Assert.Equal(NamedSettings.Deserialize(plan.SettingsJson), first.Plan.Settings);
        Assert.Contains("{{plan.PROGFILE_PATH_1}}", plan.ExecutionPlanJson!, StringComparison.Ordinal);
        if (simulation)
        {
            Assert.All(first.Plan.Devices, device => Assert.StartsWith("Simulated", device.Driver));
            return;
        }
        using var scanner1 = JsonDocument.Parse(first.Plan.Devices.Single(device => device.DeviceKey == "Matrix200").ConnectionParametersJson!);
        using var scanner2 = JsonDocument.Parse(second.Plan.Devices.Single(device => device.DeviceKey == "Matrix200").ConnectionParametersJson!);
        Assert.Equal("COM4", scanner1.RootElement.GetProperty("portName").GetString());
        Assert.Equal("COM7", scanner2.RootElement.GetProperty("portName").GetString());
        using var programmer = JsonDocument.Parse(first.Plan.Devices.Single(device => device.DeviceKey == "JLink").ConnectionParametersJson!);
        Assert.Equal(@"C:\SEGGER\JFlash.exe", programmer.RootElement.GetProperty("executablePath").GetString());
        Assert.Equal(@"\\heka.de\firmware\Board.jflash", programmer.RootElement.GetProperty("arguments")[1].GetString());
        Assert.Equal("{{imagePath}}", programmer.RootElement.GetProperty("arguments")[3].GetString());
        Assert.Equal(600, programmer.RootElement.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task MissingStationSettingsPreventPreparationEvenInSimulation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (plan, type) = await CreateProgramPlanAsync(database);
        var station = AddStation(database, type, "PS05-1", "COM4");
        station.SettingsJson = "[]";
        await database.Context.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Prepare(database, plan, true).PrepareForOrderAsync("PS05-1", "FAUF-1"));
        Assert.Contains("fehlt", exception.Message, StringComparison.Ordinal);
        Assert.Empty(database.Context.Runs);
    }

    [Fact]
    public async Task DirectSequencerCallsResolvePlanReferencesAndSaveTheRun()
    {
        await using var database = await TestDatabase.CreateAsync();
        var plan = new TestPlan
        {
            ArtNr = "4711", Fz = "A", SettingsJson = NamedSettings.Serialize([Setting("Message", "LED gruen?")]),
            Steps = [new TestStep { Name = "Prompt", StepType = StepType.OperatorPrompt,
                ParametersJson = StepParameters.Serialize(new OperatorPromptParameters("{{plan.Message}}")) }]
        };
        var execution = new ExecutionPlanOptimizer().Optimize(plan, new Dictionary<string, DeviceDefinition>());
        var interaction = new RecordingInteraction();
        var directory = Path.Combine(Path.GetTempPath(), "testpilot-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new SequenceRunner(new DriverHost(new DriverRegistry().AddSimulatedDrivers(), NullLoggerFactory.Instance),
                database.Context, new RunJsonExporter(directory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()), interaction);
            var result = await runner.RunAsync(execution, new RunContext("FAUF-1", "1234567890", "PS05-1", "", Simulated: true));
            Assert.Equal("LED gruen?", interaction.Message);
            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            Assert.Null(result.SaveError);
            Assert.True(File.Exists(result.ExportPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(TestPlan Plan, TestSystemType Type)> CreateProgramPlanAsync(TestDatabase database)
    {
        var type = new TestSystemType
        {
            Name = "PS05",
            Devices = [new SystemTypeDevice { DeviceKey = "JLink", Model = "J-Link" },
                new SystemTypeDevice { DeviceKey = "Matrix200", Model = "Matrix200" }]
        };
        database.Context.TestSystemTypes.Add(type);
        var plan = new TestPlan
        {
            ArtNr = "4711", Fz = "A", TestSystemTypeId = type.Id,
            SettingsJson = NamedSettings.Serialize([Setting("PROGFILE_PATH_1", @"\\heka.de\firmware\FW.bin"), Setting("PROJFILE_PATH_1", @"\\heka.de\firmware\Board.jflash")]),
            Steps = [new TestStep { Name = "Program", StepType = StepType.Program, DeviceKey = "JLink",
                    ParametersJson = StepParameters.Serialize(new ProgramParameters("{{plan.PROGFILE_PATH_1}}")) },
                new TestStep { Order = 1, Name = "Scan", StepType = StepType.Scan, DeviceKey = "Matrix200",
                    ParametersJson = StepParameters.Serialize(new ScanParameters()) }]
        };
        database.Context.TestPlans.Add(plan);
        await database.Context.SaveChangesAsync();
        await database.Plans.ReleaseAsync(plan.Id, new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(type)));
        return (plan, type);
    }

    private static Station AddStation(TestDatabase database, TestSystemType type, string name, string port)
    {
        var station = new Station
        {
            Name = name, TestSystemTypeId = type.Id,
            SettingsJson = NamedSettings.Serialize([Setting("COMPort_Datalogic", port),
                Setting("JFlashExecutablePath", @"C:\SEGGER\JFlash.exe"), Setting("Timeout", "600", SettingDataType.Integer)]),
            Devices = [new StationDevice { DeviceKey = "JLink", Driver = "SeggerJFlashCli", ConnectionParametersJson = """
                {"executablePath":"{{station.JFlashExecutablePath}}", "arguments":["-openprj", "{{plan.PROJFILE_PATH_1}}", "-open", "{{imagePath}}"],
                 "timeoutSeconds":"{{station.Timeout}}"}
                """ },
                new StationDevice { DeviceKey = "Matrix200", Driver = "DatalogicMatrixSerial", ConnectionParametersJson = """
                {"model":"Matrix200","portName":"{{station.COMPort_Datalogic}}"}
                """ }]
        };
        database.Context.Stations.Add(station);
        return station;
    }

    private static RunPreparation Prepare(TestDatabase database, TestPlan plan, bool simulation) =>
        new(database.Context, new MockSapClient([new("FAUF-1", new OrderInfo(plan.ArtNr, plan.Fz))]), new DriverRegistry().AddAllDrivers(), simulation);

    private static NamedSetting Setting(string alias, string value, SettingDataType type = SettingDataType.String) =>
        new("Communication", alias, alias, type, value);

    private sealed class RecordingInteraction : ISequenceInteraction
    {
        public string? Message { get; private set; }
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            Message = message;
            return Task.FromResult(true);
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
