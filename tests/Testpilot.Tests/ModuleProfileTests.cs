using System.Text.Json;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

/// <summary>34921A/34937A module profiles (Keysight user guides 9018-02147 and 9018-02150) and their use.</summary>
public sealed class ModuleProfileTests
{
    private static DeviceProfile Mainframe(string slotsJson) =>
        DeviceCatalog.CreateProfile("34980A", "{\"slots\":" + slotsJson + "}");

    [Fact]
    public void Module34921AFollowsTheUserGuide()
    {
        var profile = Mainframe("{\"2\":\"34921A\"}");

        for (var channel = 1; channel <= 20; channel++)
        {
            Assert.Contains("FRES", profile.GetChannel(2000 + channel).SupportedFunctions);
            Assert.Equal(2020 + channel, profile.GetChannel(2000 + channel).FourWireSenseChannel);
        }
        for (var channel = 21; channel <= 40; channel++)
        {
            Assert.Equal(["RES", "VOLT:AC", "VOLT:DC"], profile.GetChannel(2000 + channel).SupportedFunctions.Order());
            Assert.Null(profile.GetChannel(2000 + channel).FourWireSenseChannel);
        }
        for (var channel = 41; channel <= 44; channel++)
        {
            Assert.Equal(ChannelType.Current, profile.GetChannel(2000 + channel).Type);
            Assert.Equal(["CURR:AC", "CURR:DC"], profile.GetChannel(2000 + channel).SupportedFunctions.Order());
        }
        Assert.DoesNotContain(profile.Channels, channel => channel.Number < 2041 && channel.SupportedFunctions.Contains("CURR:DC"));
        Assert.False(profile.GetChannel(2001).State.IsInvalidatedBy("configure", "range"));
        Assert.False(profile.GetChannel(2001).State.IsInvalidatedBy("measure", "function"));
        Assert.True(profile.GetChannel(2001).State.IsInvalidatedBy("relay", "range"));
        Assert.True(profile.GetChannel(2001).State.IsInvalidatedBy("setPower", "range"));
    }

    [Fact]
    public void Module34937AIsARelayModuleWithoutMeasurements()
    {
        var profile = Mainframe("{\"5\":\"34937A\"}");

        Assert.Equal(32, profile.Channels.Count);
        Assert.All(profile.Channels, channel =>
        {
            Assert.Equal(ChannelType.Relay, channel.Type);
            Assert.Empty(channel.SupportedFunctions);
        });
        Assert.Contains("Form C", profile.GetChannel(5028).Name, StringComparison.Ordinal);
        Assert.Contains("Form A", profile.GetChannel(5029).Name, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new MeasurementConfiguration(5001, "VOLT:DC").Validate(profile));
    }

    [Theory]
    [InlineData("FRES", "VOLT:DC", true)]
    [InlineData("RES", "VOLT:DC", false)]
    [InlineData("VOLT:DC", "VOLT:DC", false)]
    public void FourWireSenseChannelConflictsOnlyWithFres(string sourceFunction, string senseFunction, bool conflict)
    {
        var plan = new TestPlan
        {
            ArtNr = "A1", Fz = "A",
            Steps =
            [
                Measure(0, 1001, sourceFunction),
                Measure(1, 1021, senseFunction)
            ]
        };

        var allocations = ChannelAllocationAnalyzer.Analyze(plan,
            new Dictionary<string, DeviceProfile>(StringComparer.Ordinal) { ["Ag34980A"] = Mainframe("{\"1\":\"34921A\"}") });

        Assert.Equal(conflict, allocations.Single(item => item.Channel == 1001).HasConflict);
        Assert.Equal(conflict, allocations.Single(item => item.Channel == 1021).HasConflict);
    }

    [Fact]
    public void Ag34980DriversComposeTheProfileFromSlots()
    {
        var registry = new DriverRegistry().AddAg34980Scpi().AddAg34980Dll();
        var scpi = registry.GetProfile(DriverConfiguration.Parse("Ag34980A", "Ag34980Scpi",
            "{\"resource\":\"TCPIP0::192.0.2.1::inst0::INSTR\",\"slots\":{\"1\":\"34921A\",\"3\":\"34937A\"}}"));
        var dll = registry.GetProfile(DriverConfiguration.Parse("Ag34980A", "Ag34980Dll",
            "{\"libraryPath\":\"ag34980a.dll\",\"resource\":\"GPIB0::9::INSTR\",\"slots\":{\"1\":\"34921A\"}}"));

        Assert.Equal(76, scpi.Channels.Count);
        Assert.Equal(44, dll.Channels.Count);
        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("Ag34980A", "Ag34980Scpi",
            "{\"host\":\"192.0.2.1\",\"slots\":{\"1\":\"34921A\"},\"profilePath\":\"x.yaml\"}")));
        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("Ag34980A", "Ag34980Scpi",
            "{\"host\":\"192.0.2.1\",\"slots\":{\"9\":\"34921A\"}}")));
    }

    [Fact]
    public async Task RunPreparationPassesTheSlotsOfTheSystemTypeToTheDriver()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var system = new TestSystemType
        {
            Name = "PS04",
            Devices = [new SystemTypeDevice { DeviceKey = "dmm1", Model = "34980A", OptionsJson = "{\"slots\":{\"1\":\"34921A\"}}" }]
        };
        testDatabase.Context.TestSystemTypes.Add(system);
        var plan = TestDatabase.Draft();
        plan.TestSystemTypeId = system.Id;
        testDatabase.Context.TestPlans.Add(plan);
        testDatabase.Context.Stations.Add(new Station
        {
            Name = "PS04-1", TestSystemTypeId = system.Id,
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "Ag34980Scpi",
                ConnectionParametersJson = "{\"resource\":\"TCPIP0::192.0.2.1::inst0::INSTR\"}" }]
        });
        await testDatabase.Context.SaveChangesAsync();
        await testDatabase.Plans.ReleaseAsync(plan.Id,
            new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(system)));
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo(plan.ArtNr, plan.Fz))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddAg34980Scpi(), false);

        var prepared = await preparation.PrepareForOrderAsync("PS04-1", "FAUF-1");

        var device = Assert.Single(prepared.Plan.Devices);
        Assert.Equal("Ag34980Scpi", device.Driver);
        using var connection = JsonDocument.Parse(device.ConnectionParametersJson!);
        Assert.Equal("TCPIP0::192.0.2.1::inst0::INSTR", connection.RootElement.GetProperty("resource").GetString());
        Assert.Equal("34921A", connection.RootElement.GetProperty("slots").GetProperty("1").GetString());
    }

    private static TestStep Measure(int order, int channel, string function) => new()
    {
        Order = order, Name = $"M{order}", StepType = StepType.Measure, DeviceKey = "Ag34980A",
        Unit = function is "RES" or "FRES" ? MeasurementUnit.Ohm : MeasurementUnit.Volt,
        ParametersJson = StepParameters.Serialize(new MeasureParameters(channel, function))
    };
}
