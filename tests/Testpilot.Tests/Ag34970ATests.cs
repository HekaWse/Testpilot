using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

/// <summary>34970A with the modules 34901A, 34903A and 34907A, the Ag34970Scpi driver and the PS05-1 seed.</summary>
public sealed class Ag34970ATests
{
    private const string Ps05Slots = "{\"1\":\"34901A\",\"2\":\"34903A\",\"3\":\"34907A\"}";

    private static DeviceProfile Mainframe(string slotsJson) =>
        DeviceCatalog.CreateProfile(DeviceCatalog.Ag34970A, "{\"slots\":" + slotsJson + "}");

    [Fact]
    public void ChannelsAreNumberedSlotTimesHundredPlusChannel()
    {
        var profile = Mainframe(Ps05Slots);

        Assert.Equal(22 + 20 + 5, profile.Channels.Count);
        Assert.Equal(Enumerable.Range(101, 22).Concat(Enumerable.Range(201, 20)).Concat(Enumerable.Range(301, 5)),
            profile.Channels.Select(channel => channel.Number).Order());
        Assert.Contains("slot * 100", profile.ChannelNumbering, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Mainframe("{\"4\":\"34901A\"}"));
        Assert.Throws<ArgumentException>(() => Mainframe("{\"1\":\"34921A\"}"));
    }

    [Fact]
    public void Module34901AHasTwentyMultiplexerAndTwoCurrentChannels()
    {
        var profile = Mainframe("{\"1\":\"34901A\"}");

        for (var channel = 101; channel <= 110; channel++)
        {
            Assert.Contains("FRES", profile.GetChannel(channel).SupportedFunctions);
            Assert.Equal(channel + 10, profile.GetChannel(channel).FourWireSenseChannel);
        }
        for (var channel = 111; channel <= 120; channel++)
        {
            Assert.Equal(["FREQ", "RES", "VOLT:AC", "VOLT:DC"], profile.GetChannel(channel).SupportedFunctions.Order());
            Assert.Null(profile.GetChannel(channel).FourWireSenseChannel);
        }
        foreach (var channel in new[] { 121, 122 })
        {
            Assert.Equal(ChannelType.Current, profile.GetChannel(channel).Type);
            Assert.Equal(["CURR:AC", "CURR:DC"], profile.GetChannel(channel).SupportedFunctions.Order());
        }
        Assert.DoesNotContain(profile.Channels, channel => channel.Number < 121 && channel.SupportedFunctions.Contains("CURR:DC"));
        new MeasurementConfiguration(121, "CURR:DC").Validate(profile);
        Assert.Throws<ArgumentException>(() => new MeasurementConfiguration(101, "CURR:DC").Validate(profile));
    }

    [Fact]
    public void Module34903AIsARelayModuleWithoutMeasurements()
    {
        var profile = Mainframe("{\"2\":\"34903A\"}");

        Assert.Equal(20, profile.Channels.Count);
        Assert.All(profile.Channels, channel =>
        {
            Assert.Equal(ChannelType.Relay, channel.Type);
            Assert.Empty(channel.SupportedFunctions);
            Assert.True(channel.CanSwitch);
        });
        Assert.Throws<ArgumentException>(() => new MeasurementConfiguration(201, "VOLT:DC").Validate(profile));
    }

    [Fact]
    public void Module34907AChannelsCannotBeMeasuredOrSwitched()
    {
        var profile = Mainframe("{\"3\":\"34907A\"}");

        Assert.Equal([ChannelType.DigitalIo, ChannelType.DigitalIo, ChannelType.Totalizer, ChannelType.AnalogOutput, ChannelType.AnalogOutput],
            profile.Channels.OrderBy(channel => channel.Number).Select(channel => channel.Type));
        Assert.All(profile.Channels, channel =>
        {
            Assert.False(channel.CanSwitch);
            Assert.Empty(channel.SupportedFunctions);
        });
    }

    [Fact]
    public void OptimizerRejectsRelayStepsOnDigitalChannels()
    {
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["Ag34970A"] = new(null, Mainframe(Ps05Slots), DeviceCatalog.Ag34970A)
        };
        var optimizer = new ExecutionPlanOptimizer();

        var error = Assert.Throws<ArgumentException>(() => optimizer.Optimize(Plan(Relay(0, 302)), definitions));
        Assert.Contains("302", error.Message, StringComparison.Ordinal);
        var plan = optimizer.Optimize(Plan(Relay(0, 201), Measure(1, 101, "VOLT:DC"), Measure(2, 121, "CURR:DC")), definitions);
        Assert.Equal(3, plan.Steps.Count);
    }

    [Fact]
    public async Task DriverDoesNotResetAndOpensOnlyRelayChannelsInSafeState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = ServeAsync(listener, commands, cancellation.Token);
        var configuration = DriverConfiguration.Parse("Ag34970A", "Ag34970Scpi", JsonSerializer.Serialize(new
        {
            host = "127.0.0.1", port, timeoutMilliseconds = 2000,
            slots = new Dictionary<string, string> { ["1"] = "34901A", ["2"] = "34903A", ["3"] = "34907A" }
        }));
        var host = new DriverHost(new DriverRegistry().AddAg34970Scpi(), NullLoggerFactory.Instance);
        await using (var lease = await host.AcquireAsync(configuration, cancellation.Token))
        {
            Assert.True((await lease.Instrument.SelfCheckAsync(cancellation.Token)).Passed);
            var meter = lease.GetCapability<ISwitchMultimeter>();
            await meter.ConfigureMeasurementAsync(new MeasurementConfiguration(101, "VOLT:DC", 10, 1), cancellation.Token);
            Assert.Equal(3.298, await meter.MeasureAsync(101, cancellation.Token));
            await meter.SetRelayAsync(201, true, cancellation.Token);
            await Assert.ThrowsAsync<ArgumentException>(() => meter.SetRelayAsync(302, true, cancellation.Token));
            await lease.Instrument.SafeStateAsync(cancellation.Token);
        }
        listener.Stop();
        await server;

        var relayList = string.Join(",", Enumerable.Range(101, 22).Concat(Enumerable.Range(201, 20)));
        Assert.Equal(
        [
            "*IDN?", "*CLS", "*IDN?", "SYST:ERR?",
            "CONF:VOLT:DC 10,(@101)", "SENS:VOLT:DC:NPLC 1,(@101)", "SYST:ERR?",
            "ROUT:SCAN (@101)", "READ?",
            "ROUT:CLOS (@201)", "SYST:ERR?",
            $"ROUT:OPEN (@{relayList})", "SYST:ERR?"
        ], commands);
    }

    [Fact]
    public async Task SeedCreatesPs05DevicesAndTheTestSystemPs05Dash1Once()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            await TestSystemTypeSeed.SeedAsync(db);
            Assert.Equal(["PS05: 3 Geräte", "PS05-1"], await StationSeed.SeedAsync(db));
        }
        await using (var db = database.NewContext())
            Assert.Empty(await StationSeed.SeedAsync(db));

        await using var verify = database.NewContext();
        var type = await verify.TestSystemTypes.Include(item => item.Devices).SingleAsync(item => item.Name == "PS05");
        Assert.Equal(["Ag34970A", "Hmp2030", "Ps05Adapter"], type.Devices.Select(device => device.DeviceKey).Order());
        var station = await verify.Stations.Include(item => item.Devices).SingleAsync(item => item.Name == "PS05-1");
        Assert.Equal(type.Id, station.TestSystemTypeId);
        Assert.Equal("Ag34970Scpi", station.Devices.Single(device => device.DeviceKey == "Ag34970A").Driver);
        Assert.Equal("HmpScpi", station.Devices.Single(device => device.DeviceKey == "Hmp2030").Driver);
        Assert.Equal("Ps05Fixture", station.Devices.Single(device => device.DeviceKey == "Ps05Adapter").Driver);
        // Every seeded station device must be accepted by its real driver.
        var registry = new DriverRegistry().AddAllDrivers();
        var definitions = SystemTypeDevices.CreateDefinitions(type);
        foreach (var device in station.Devices.Where(device => device.DeviceKey != "Ag34970A"))
            registry.GetProfile(DriverConfiguration.Parse(device.DeviceKey, device.Driver, device.ConnectionParametersJson));
        Assert.Equal(47, definitions["Ag34970A"].Profile.Channels.Count);
    }

    [Fact]
    public async Task SeedKeepsExistingDevicesAndOnlyAddsMissingKeys()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        await TestSystemTypeSeed.SeedAsync(testDatabase.Context);
        var type = await testDatabase.Context.TestSystemTypes.SingleAsync(item => item.Name == "PS05");
        testDatabase.Context.SystemTypeDevices.Add(new SystemTypeDevice
        {
            TestSystemTypeId = type.Id, DeviceKey = "PSU1", Model = "HMP2030", Description = "eigener Eintrag", OptionsJson = "{}"
        });
        testDatabase.Context.SystemTypeDevices.Add(new SystemTypeDevice
        {
            TestSystemTypeId = type.Id, DeviceKey = "hmp2030", Model = "HMP4040", Description = "eigener Eintrag", OptionsJson = "{}"
        });
        await testDatabase.Context.SaveChangesAsync();

        Assert.Equal(["PS05: 2 Geräte", "PS05-1"], await StationSeed.SeedAsync(testDatabase.Context));

        await using var verify = testDatabase.NewContext();
        var devices = await verify.SystemTypeDevices.Where(device => device.TestSystemTypeId == type.Id).ToListAsync();
        Assert.Equal(["Ag34970A", "PSU1", "Ps05Adapter", "hmp2030"], devices.Select(device => device.DeviceKey).Order(StringComparer.Ordinal));
        Assert.Equal("HMP4040", devices.Single(device => device.DeviceKey == "hmp2030").Model);
    }

    [Fact]
    public async Task Ps05Dash1RunGetsTheSlotsAndAddressesOfTheSeed()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        await TestSystemTypeSeed.SeedAsync(testDatabase.Context);
        await StationSeed.SeedAsync(testDatabase.Context);
        var type = await testDatabase.Context.TestSystemTypes.Include(item => item.Devices).SingleAsync(item => item.Name == "PS05");
        var plan = Plan(
            new TestStep
            {
                Order = 0, Name = "PSU2", StepType = StepType.SetPower, DeviceKey = "Hmp2030",
                ParametersJson = StepParameters.Serialize(new SetPowerParameters(5, 0.5, true, 2))
            },
            Measure(1, 101, "VOLT:DC"));
        plan.TestSystemTypeId = type.Id;
        testDatabase.Context.TestPlans.Add(plan);
        await testDatabase.Context.SaveChangesAsync();
        await testDatabase.Plans.ReleaseAsync(plan.Id,
            new ExecutionPlanOptimizer().Optimize(plan, SystemTypeDevices.CreateDefinitions(type)));
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo(plan.ArtNr, plan.Fz))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddAllDrivers(), false);

        var prepared = await preparation.PrepareForOrderAsync("PS05-1", "FAUF-1");

        var meter = prepared.Plan.Devices.Single(device => device.DeviceKey == "Ag34970A");
        Assert.Equal("Ag34970Scpi", meter.Driver);
        using var connection = JsonDocument.Parse(meter.ConnectionParametersJson!);
        Assert.Equal(StationSeed.Ps05Ag34970AResource, connection.RootElement.GetProperty("resource").GetString());
        Assert.Equal("34907A", connection.RootElement.GetProperty("slots").GetProperty("3").GetString());
        var supply = prepared.Plan.Devices.Single(device => device.DeviceKey == "Hmp2030");
        Assert.Equal("HmpScpi", supply.Driver);
    }

    private static TestPlan Plan(params TestStep[] steps) => new() { ArtNr = "4711", Fz = "A", Steps = [.. steps] };

    private static TestStep Relay(int order, int channel) => new()
    {
        Order = order, Name = $"R{order}", StepType = StepType.Relay, DeviceKey = "Ag34970A",
        ParametersJson = StepParameters.Serialize(new RelayParameters(channel, true))
    };

    private static TestStep Measure(int order, int channel, string function) => new()
    {
        Order = order, Name = $"M{order}", StepType = StepType.Measure, DeviceKey = "Ag34970A",
        Unit = function.StartsWith("CURR", StringComparison.Ordinal) ? MeasurementUnit.Ampere : MeasurementUnit.Volt,
        ParametersJson = StepParameters.Serialize(new MeasureParameters(channel, function))
    };

    private static string? Respond(string command) => command switch
    {
        "*IDN?" => "HEWLETT-PACKARD,34970A,0,13-2-2",
        "SYST:ERR?" => "+0,\"No error\"",
        "READ?" => 3.298.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private static async Task ServeAsync(TcpListener listener, List<string> commands, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        while (!cancellationToken.IsCancellationRequested)
        {
            var command = await reader.ReadLineAsync(cancellationToken);
            if (command is null) return;
            commands.Add(command);
            var response = Respond(command);
            if (response is not null) await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }
    }
}
