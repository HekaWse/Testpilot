using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;

namespace Testpilot.Tests;

public sealed class Phase4EditorTests
{
    [Fact]
    public void StationYamlRoundTripsStationAndDeviceConfiguration()
    {
        var station = new Station
        {
            Name = "Prüfplatz 1",
            Devices =
            [
                new StationDevice
                {
                    DeviceKey = "dmm1",
                    Driver = "SimulatedSwitchMultimeter",
                    ConnectionParametersJson = "{\"channels\":[1003],\"readings\":{\"1003|VOLT:DC\":3.3}}"
                }
            ]
        };
        var service = new StationYamlService();

        var imported = service.Import(service.Export(station));

        Assert.Equal(station.Name, imported.Name);
        var device = Assert.Single(imported.Devices);
        Assert.Equal("dmm1", device.DeviceKey);
        Assert.Equal(station.Devices[0].Driver, device.Driver);
        Assert.Equal(station.Devices[0].ConnectionParametersJson, device.ConnectionParametersJson);
    }

    [Fact]
    public void StationYamlRejectsDuplicateDeviceKeysAndNonObjectParameters()
    {
        var service = new StationYamlService();
        const string duplicateKeys = """
            name: Bench
            devices:
              - deviceKey: dmm1
                driver: SimulatedSwitchMultimeter
                connectionParametersJson: '{}'
              - deviceKey: dmm1
                driver: SimulatedPowerSupply
                connectionParametersJson: '{}'
            """;
        const string invalidParameters = """
            name: Bench
            devices:
              - deviceKey: dmm1
                driver: SimulatedSwitchMultimeter
                connectionParametersJson: '[]'
            """;

        Assert.Throws<InvalidDataException>(() => service.Import(duplicateKeys));
        Assert.Throws<InvalidDataException>(() => service.Import(invalidParameters));
    }

    [Fact]
    public void StationYamlRejectsOversizedDocuments()
    {
        var service = new StationYamlService();

        Assert.Throws<InvalidDataException>(() => service.Import(new string('a', 1_048_577)));
    }

    [Theory]
    [InlineData("{\"typo\":true}")]
    [InlineData("{\"channels\":[1003,1003]}")]
    [InlineData("{\"channels\":[0]}")]
    [InlineData("{\"channels\":null}")]
    [InlineData("{\"channels\":[]}")]
    [InlineData("{\"slots\":{\"1\":\"34921A\"}}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void SimulatedSystemDeviceRejectsInvalidOptions(string optionsJson)
    {
        Assert.Throws<ArgumentException>(() => DeviceCatalog.CreateProfile("SimDmm", optionsJson));
    }

    [Fact]
    public void DeviceProfileYamlLoadsAndValidatesChannelPairRules()
    {
        const string yaml = """
            id: pair-profile
            model: Test meter
            channelCount: 2
            channelNumbering: channel
            capabilities: [SwitchMultimeter]
            state:
              settings: [function, range, nplc]
              invalidatedByCommand: {}
              unknownCommandInvalidatesAll: true
            channels:
              - number: 1
                name: Channel 1
                type: Voltage
                supportedFunctions: [VOLT:DC]
                valueRanges: {}
                conflictsWithChannels: [2]
              - number: 2
                name: Channel 2
                type: Voltage
                supportedFunctions: [VOLT:DC]
                valueRanges: {}
            specialCases: []
            todos: []
            """;

        var profile = Testpilot.Drivers.Abstractions.DeviceProfileLoader.Load(yaml);

        Assert.Contains(2, profile.GetChannel(1).ConflictsWithChannels!);
        Assert.Throws<ArgumentException>(() => Testpilot.Drivers.Abstractions.DeviceProfileLoader.Load(
            yaml.Replace("conflictsWithChannels: [2]", "conflictsWithChannels: [3]", StringComparison.Ordinal)));
    }

    [Fact]
    public void ChannelAllocationAnalyzerReportsProfileDeclaredChannelPairs()
    {
        var state = new Testpilot.Drivers.Abstractions.ChannelStateProfile(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), true);
        var profile = new Testpilot.Drivers.Abstractions.DeviceProfile("test", "Test", 2, "channel",
            new HashSet<Testpilot.Drivers.Abstractions.DeviceCapability>
            {
                Testpilot.Drivers.Abstractions.DeviceCapability.SwitchMultimeter
            }, state,
            [
                new Testpilot.Drivers.Abstractions.ChannelProfile(1, "A", Testpilot.Drivers.Abstractions.ChannelType.Voltage,
                    new HashSet<string>(["VOLT:DC"], StringComparer.Ordinal),
                    new Dictionary<string, Testpilot.Drivers.Abstractions.NumericRange>(StringComparer.Ordinal), state,
                    new HashSet<int> { 2 }),
                new Testpilot.Drivers.Abstractions.ChannelProfile(2, "B", Testpilot.Drivers.Abstractions.ChannelType.Voltage,
                    new HashSet<string>(["VOLT:DC"], StringComparer.Ordinal),
                    new Dictionary<string, Testpilot.Drivers.Abstractions.NumericRange>(StringComparer.Ordinal), state)
            ], [], []);
        profile.Validate();
        var plan = new TestPlan
        {
            ArtNr = "A1",
            Fz = "A",
            Steps =
            [
                new TestStep { Order = 0, Name = "First", StepType = StepType.Measure, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new MeasureParameters(1, "VOLT:DC")), Unit = MeasurementUnit.Volt },
                new TestStep { Order = 1, Name = "Second", StepType = StepType.Measure, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new MeasureParameters(2, "VOLT:DC")), Unit = MeasurementUnit.Volt }
            ]
        };

        var allocations = Testpilot.Editor.Services.ChannelAllocationAnalyzer.Analyze(plan,
            new Dictionary<string, Testpilot.Drivers.Abstractions.DeviceProfile>(StringComparer.Ordinal) { ["dmm1"] = profile });

        Assert.Equal([2], Assert.Single(allocations, item => item.Channel == 1).ConflictingChannels);
        Assert.Equal([1], Assert.Single(allocations, item => item.Channel == 2).ConflictingChannels);
        Assert.False(Testpilot.Editor.Services.PlanReleasePolicy.IsAllowed(Array.Empty<string>(), allocations));
        Assert.False(Testpilot.Editor.Services.PlanReleasePolicy.IsAllowed(["TODO: aus Datenblatt prüfen"], Array.Empty<Testpilot.Editor.Services.ChannelAllocationAnalyzer.ChannelAllocation>()));
        Assert.True(Testpilot.Editor.Services.PlanReleasePolicy.IsAllowed(Array.Empty<string>(),
            allocations.Select(item => item with { ConflictingChannels = Array.Empty<int>() }).ToArray()));
    }

    [Fact]
    public void SimulatedSystemDeviceUsesConfiguredChannels()
    {
        var profile = DeviceCatalog.CreateProfile("SimDmm", "{\"channels\":[1003]}");

        Assert.Equal(1003, Assert.Single(profile.Channels).Number);
        Assert.Contains("VOLT:DC", profile.GetChannel(1003).SupportedFunctions);
    }
}
