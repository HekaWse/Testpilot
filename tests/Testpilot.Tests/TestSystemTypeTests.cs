using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

/// <summary>
/// Test system types (PS05, PS04, ...) and concrete test systems (PS05-1, ...): device catalog, data model,
/// release and run-time binding of a plan to the drivers and addresses of a test system.
/// </summary>
public sealed class TestSystemTypeTests
{
    private const string MeterParameters = "{\"channels\":[1003],\"readings\":{\"1003|VOLT:DC\":3.3}}";

    [Fact]
    public void MainframeProfileIsComposedFromSlotModulesWithPrefixedTodos()
    {
        var profile = DeviceCatalog.CreateProfile("34980A", "{\"slots\":{\"3\":\"34937A\",\"1\":\"34921A\"}}");

        Assert.Contains(DeviceCapability.SwitchMultimeter, profile.Capabilities);
        Assert.Contains("slot * 100 + channel", profile.ChannelNumbering, StringComparison.Ordinal);
        Assert.Contains(profile.Todos, todo => todo.StartsWith("Slot 1 (34921A): ", StringComparison.Ordinal));
        Assert.Contains(profile.Todos, todo => todo.StartsWith("Slot 3 (34937A): ", StringComparison.Ordinal));
        Assert.Equal("34980A: Slot 1 34921A, Slot 3 34937A",
            DeviceCatalog.Describe("34980A", "{\"slots\":{\"3\":\"34937A\",\"1\":\"34921A\"}}"));
        Assert.Equal("{\"slots\":{\"1\":\"34921A\",\"3\":\"34937A\"}}",
            DeviceCatalog.ParseOptions("34980a", "{\"slots\":{\"3\":\"34937a\",\"1\":\"34921A\"}}").ToJson());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"slots\":{}}")]
    [InlineData("{\"slots\":{\"9\":\"34921A\"}}")]
    [InlineData("{\"slots\":{\"0\":\"34921A\"}}")]
    [InlineData("{\"slots\":{\"1\":\"34999A\"}}")]
    [InlineData("{\"slots\":{\"1\":\"34921A\",\"1\":\"34937A\"}}")]
    [InlineData("{\"channels\":[1]}")]
    public void MainframeRejectsInvalidSlotOptions(string optionsJson)
    {
        Assert.Throws<ArgumentException>(() => DeviceCatalog.ParseOptions("34980A", optionsJson));
    }

    [Fact]
    public void FixedModelsUseTheirProfileAndRejectOptions()
    {
        var profile = DeviceCatalog.CreateProfile("HMP4040", "{}");

        Assert.Contains(DeviceCapability.PowerSupply, profile.Capabilities);
        Assert.Equal(4, profile.Channels.Count);
        Assert.Throws<ArgumentException>(() => DeviceCatalog.ParseOptions("HMP4040", "{\"channels\":[1]}"));
        Assert.Throws<ArgumentException>(() => DeviceCatalog.Get("HMP9999"));
    }

    [Fact]
    public void BindingChecksDriverAndConfiguredModel()
    {
        DeviceCatalog.ValidateBinding("psu1", "HMP4040", "HmpScpi", "{\"model\":\"HMP4040\",\"resource\":\"TCPIP::1.2.3.4::INSTR\"}");
        DeviceCatalog.ValidateBinding("psu1", "HMP4040", "SimulatedPowerSupply", "{}");
        DeviceCatalog.ValidateBinding("dmm1", "34980A", "Ag34980Dll", "{}");

        var wrongDriver = Assert.Throws<ArgumentException>(() =>
            DeviceCatalog.ValidateBinding("psu1", "HMP4040", "Ag34980Scpi", "{}"));
        var wrongModel = Assert.Throws<ArgumentException>(() =>
            DeviceCatalog.ValidateBinding("psu1", "HMP4040", "HmpScpi", "{\"model\":\"HMP2030\"}"));
        var missingModel = Assert.Throws<ArgumentException>(() =>
            DeviceCatalog.ValidateBinding("scan1", "Matrix120", "DatalogicMatrixSerial", "{}"));

        Assert.Contains("Ag34980Scpi", wrongDriver.Message, StringComparison.Ordinal);
        Assert.Contains("HMP2030", wrongModel.Message, StringComparison.Ordinal);
        Assert.Contains("Matrix120", missingModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemDevicesAreValidatedAndModelNamesNormalized()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var system = new TestSystemType
        {
            Name = "PS05",
            Devices = [new SystemTypeDevice { DeviceKey = "psu1", Model = "hmp4040" }]
        };
        testDatabase.Context.TestSystemTypes.Add(system);
        await testDatabase.Context.SaveChangesAsync();

        Assert.Equal("HMP4040", system.Devices.Single().Model);

        system.Devices.Add(new SystemTypeDevice { DeviceKey = "x1", Model = "Unbekannt" });
        await Assert.ThrowsAsync<ArgumentException>(() => testDatabase.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task SystemReferencedByPlanCannotBeDeleted()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var system = SimSystem();
        testDatabase.Context.TestSystemTypes.Add(system);
        var plan = TestDatabase.Draft();
        plan.TestSystemTypeId = system.Id;
        testDatabase.Context.TestPlans.Add(plan);
        await testDatabase.Context.SaveChangesAsync();
        await using var other = testDatabase.NewContext();
        other.TestSystemTypes.Remove(await other.TestSystemTypes.SingleAsync());

        var error = await Assert.ThrowsAnyAsync<Exception>(() => other.SaveChangesAsync());
        Assert.True(error is DbUpdateException or InvalidOperationException, error.ToString());
        Assert.Equal(system.Id, (await testDatabase.NewContext().TestPlans.AsNoTracking().SingleAsync()).TestSystemTypeId);
    }

    [Fact]
    public async Task SystemPlanIsReleasedWithModelsAndNewVersionKeepsTheSystem()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var (system, plan) = await AddSystemPlanAsync(testDatabase);

        var executionPlan = new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(system));
        await testDatabase.Plans.ReleaseAsync(plan.Id, executionPlan);
        var copy = await testDatabase.Plans.CreateNewVersionAsync(plan.Id);

        var device = Assert.Single(ExecutionPlanSerializer.Deserialize(
            testDatabase.Context.TestPlans.AsNoTracking().Single(item => item.Id == plan.Id).ExecutionPlanJson!).Devices);
        Assert.Equal(new ExecutionDevice("dmm1", null, null, "SimDmm"), device);
        Assert.False(device.IsBound);
        Assert.Equal(system.Id, copy.TestSystemTypeId);
    }

    [Fact]
    public async Task ReleaseRejectsDevicesThatAreNotInTheSystem()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var (_, plan) = await AddSystemPlanAsync(testDatabase);
        var executionPlan = new ExecutionPlanOptimizer().Optimize(plan, new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm1"] = DeviceDefinition.ForSystemDevice("SimDmm", DeviceCatalog.CreateProfile("SimDmm", "{\"channels\":[1003]}"))
        });
        var mismatching = executionPlan with
        {
            Devices = [executionPlan.Devices.Single() with { Model = "SimPowerSupply" }]
        };

        await Assert.ThrowsAsync<PlanConflictException>(() => testDatabase.Plans.ReleaseAsync(plan.Id, mismatching));
    }

    [Fact]
    public void SerializerAcceptsModelDevicesAndRejectsIncompleteDevices()
    {
        var plan = new ExecutionPlanOptimizer().Optimize(Draft(Guid.NewGuid()), new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm1"] = DeviceDefinition.ForSystemDevice("SimDmm", DeviceCatalog.CreateProfile("SimDmm", "{\"channels\":[1003]}"))
        });

        Assert.Equal(ExecutionPlanSerializer.Serialize(plan),
            ExecutionPlanSerializer.Serialize(ExecutionPlanSerializer.Deserialize(ExecutionPlanSerializer.Serialize(plan))));
        Assert.Throws<System.Text.Json.JsonException>(() => ExecutionPlanSerializer.Serialize(
            plan with { Devices = [new ExecutionDevice("dmm1", null, "{}", "SimDmm")] }));
        Assert.Throws<System.Text.Json.JsonException>(() => ExecutionPlanSerializer.Serialize(
            plan with { Devices = [new ExecutionDevice("dmm1", null, null)] }));
        Assert.Throws<System.Text.Json.JsonException>(() => ExecutionPlanSerializer.Serialize(
            plan with { Devices = [new ExecutionDevice("dmm1", null, null, "Unbekannt")] }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunPreparationBindsSystemDevicesToTheStationDrivers(bool simulation)
    {
        await using var testDatabase = await CreateReleasedSystemPlanAsync("SimulatedSwitchMultimeter");
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), simulation);

        var prepared = await preparation.PrepareForOrderAsync("PS05-1", "FAUF-1");

        var device = Assert.Single(prepared.Plan.Devices);
        Assert.Equal("SimulatedSwitchMultimeter", device.Driver);
        Assert.Equal("SimDmm", device.Model);
        Assert.Contains("3.3", device.ConnectionParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPreparationRejectsDriverThatDoesNotFitTheModel()
    {
        await using var testDatabase = await CreateReleasedSystemPlanAsync("SimulatedPowerSupply");
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareForOrderAsync("PS05-1", "FAUF-1"));

        Assert.Contains("SimulatedPowerSupply", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPreparationNamesDevicesThatAreNotInstalledAtTheTestSystem()
    {
        await using var testDatabase = await CreateReleasedSystemPlanAsync("SimulatedSwitchMultimeter");
        var station = await testDatabase.Context.Stations.Include(item => item.Devices).SingleAsync();
        testDatabase.Context.StationDevices.RemoveRange(station.Devices);
        await testDatabase.Context.SaveChangesAsync();
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareForOrderAsync("PS05-1", "FAUF-1"));

        Assert.Contains("Prüfsystem 'PS05-1'", error.Message, StringComparison.Ordinal);
        Assert.Contains("es fehlt: dmm1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingStationDevicesAreSortedAndDistinct()
    {
        var station = new Station
        {
            Name = "PS04-3",
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "Ag34980Scpi" }]
        };

        Assert.Equal(["psu1", "scanner"], StationDevices.Missing(["scanner", null, "dmm1", "psu1", "scanner"], station));
        Assert.Empty(StationDevices.Missing(["dmm1"], station));
    }

    [Theory]
    [InlineData("{\"resource\":\"GPIB0::9::INSTR\",\"model\":\"HMP2030\"}", "GPIB0::9::INSTR")]
    [InlineData("{\"Resource\":\"TCPIP0::10.0.0.5::inst0::INSTR\"}", "TCPIP0::10.0.0.5::inst0::INSTR")]
    [InlineData("{\"host\":\"192.0.2.10\",\"port\":5025}", "192.0.2.10:5025")]
    [InlineData("{\"host\":\"192.0.2.10\",\"triggerPort\":2006}", "192.0.2.10")]
    [InlineData("{\"model\":\"Matrix120\",\"portName\":\"COM3\"}", "COM3")]
    [InlineData("{\"executablePath\":\"C:\\\\JLink\\\\JFlash.exe\"}", "C:\\JLink\\JFlash.exe")]
    [InlineData("{}", "")]
    [InlineData("kein JSON", "")]
    [InlineData("[1,2]", "")]
    public void ConnectionAddressDescribesTheAddress(string json, string expected)
    {
        Assert.Equal(expected, ConnectionAddress.Describe(json));
    }

    [Fact]
    public void ConnectionAddressSetsAndRemovesTheVisaResource()
    {
        var withResource = ConnectionAddress.SetResource("{\"model\":\"HMP2030\",\"RESOURCE\":\"alt\"}", " GPIB0::10::INSTR ");

        Assert.Equal("{\"model\":\"HMP2030\",\"resource\":\"GPIB0::10::INSTR\"}", withResource);
        Assert.Equal("GPIB0::10::INSTR", ConnectionAddress.GetResource(withResource));
        Assert.Equal("{\"model\":\"HMP2030\"}", ConnectionAddress.SetResource(withResource, ""));
        Assert.Equal("{\"resource\":\"ASRL3::INSTR\"}", ConnectionAddress.SetResource("", "ASRL3::INSTR"));
        Assert.Throws<System.Text.Json.JsonException>(() => ConnectionAddress.SetResource("[1]", "x"));
        Assert.Contains("HmpScpi", ConnectionAddress.VisaDrivers);
    }

    [Fact]
    public async Task RunPreparationRejectsStationOfAnotherSystem()
    {
        await using var testDatabase = await CreateReleasedSystemPlanAsync("SimulatedSwitchMultimeter");
        testDatabase.Context.Stations.Add(new Station
        {
            Name = "Schreibtisch",
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedSwitchMultimeter", ConnectionParametersJson = MeterParameters }]
        });
        await testDatabase.Context.SaveChangesAsync();
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareForOrderAsync("Schreibtisch", "FAUF-1"));

        Assert.Contains("PS05", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemYamlRoundTripsAndValidatesModels()
    {
        var service = new TestSystemTypeYamlService();
        var system = new TestSystemType
        {
            Name = "PS05", Description = "34970A und HMP2030",
            Devices =
            [
                new SystemTypeDevice { DeviceKey = "dmm1", Model = "34980A", OptionsJson = "{\"slots\":{\"1\":\"34921A\"}}" },
                new SystemTypeDevice { DeviceKey = "psu1", Model = "HMP4040", Description = "Versorgung" }
            ]
        };

        var imported = service.Import(service.Export(system));

        Assert.Equal("PS05", imported.Name);
        Assert.Equal("34970A und HMP2030", imported.Description);
        Assert.Equal(["dmm1", "psu1"], imported.Devices.Select(device => device.DeviceKey));
        Assert.Equal("{\"slots\":{\"1\":\"34921A\"}}", imported.Devices[0].OptionsJson);
        Assert.Equal("Versorgung", imported.Devices[1].Description);
        Assert.Throws<ArgumentException>(() => service.Import("name: X\ndevices:\n  - deviceKey: a\n    model: Unbekannt\n"));
        Assert.Throws<InvalidDataException>(() => service.Import(
            "name: X\ndevices:\n  - deviceKey: a\n    model: HMP4040\n  - deviceKey: a\n    model: HMP2020\n"));
    }

    [Fact]
    public void StationYamlKeepsTheSystemByName()
    {
        var service = new StationYamlService();
        var system = SimSystem();
        var station = new Station
        {
            Name = "PS05-1", TestSystemTypeId = system.Id, Description = "Halle 2",
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedSwitchMultimeter", ConnectionParametersJson = "{}" }]
        };
        var yaml = service.Export(station, system.Name);

        var imported = service.Import(yaml, [system]);

        Assert.Contains("systemType: PS05", yaml, StringComparison.Ordinal);
        Assert.Equal(system.Id, imported.TestSystemTypeId);
        Assert.Equal("Halle 2", imported.Description);
        Assert.Throws<InvalidDataException>(() => service.Import(yaml, []));
        Assert.Throws<InvalidDataException>(() => service.Import(
            "name: PS05-2\nsystemType: PS05\ndevices:\n  - deviceKey: psu9\n    driver: SimulatedPowerSupply\n", [system]));
        Assert.Throws<ArgumentException>(() => service.Import(
            "name: PS05-2\nsystemType: PS05\ndevices:\n  - deviceKey: dmm1\n    driver: HmpScpi\n", [system]));
    }

    private static TestSystemType SimSystem() => new()
    {
        Name = "PS05",
        Devices = [new SystemTypeDevice { DeviceKey = "dmm1", Model = "SimDmm", OptionsJson = "{\"channels\":[1003]}" }]
    };

    private static TestPlan Draft(Guid id)
    {
        var plan = TestDatabase.Draft();
        plan.Id = id;
        return plan;
    }

    private static async Task<(TestSystemType System, TestPlan Plan)> AddSystemPlanAsync(TestDatabase testDatabase)
    {
        var system = SimSystem();
        testDatabase.Context.TestSystemTypes.Add(system);
        var plan = TestDatabase.Draft();
        plan.TestSystemTypeId = system.Id;
        testDatabase.Context.TestPlans.Add(plan);
        await testDatabase.Context.SaveChangesAsync();
        return (system, plan);
    }

    private static async Task<TestDatabase> CreateReleasedSystemPlanAsync(string stationDriver)
    {
        var testDatabase = await TestDatabase.CreateAsync();
        var (system, plan) = await AddSystemPlanAsync(testDatabase);
        testDatabase.Context.Stations.Add(new Station
        {
            Name = "PS05-1", TestSystemTypeId = system.Id,
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = stationDriver, ConnectionParametersJson = MeterParameters }]
        });
        await testDatabase.Context.SaveChangesAsync();
        await testDatabase.Plans.ReleaseAsync(plan.Id,
            new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(system)));
        return testDatabase;
    }
}
