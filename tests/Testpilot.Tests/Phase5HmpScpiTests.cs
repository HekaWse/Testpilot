using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;

namespace Testpilot.Tests;

public sealed class Phase5HmpScpiTests
{
    // Values from the R&S HMP data sheet 5215.4981.32 v02.01.
    [Theory]
    [InlineData("HMP2020", new[] { 10.0, 5.0 })]
    [InlineData("HMP2030", new[] { 5.0, 5.0, 5.0 })]
    [InlineData("HMP4030", new[] { 10.0, 10.0, 10.0 })]
    [InlineData("HMP4040", new[] { 10.0, 10.0, 10.0, 10.0 })]
    public void EmbeddedModelProfilesMatchTheDataSheet(string model, double[] maximumCurrents)
    {
        var profile = new DriverRegistry().AddHmpScpi().GetProfile(TcpConfiguration(5025, model));

        Assert.Equal(maximumCurrents.Length, profile.ChannelCount);
        Assert.Equal(DeviceCapability.PowerSupply, Assert.Single(profile.Capabilities));
        Assert.Empty(profile.Todos);
        for (var index = 0; index < maximumCurrents.Length; index++)
        {
            var output = profile.GetChannel(index + 1);
            Assert.Equal(ChannelType.PowerOutput, output.Type);
            Assert.Equal(new NumericRange(0, 32), output.ValueRanges[PowerOutputLimits.Voltage]);
            Assert.Equal(new NumericRange(0, maximumCurrents[index]), output.ValueRanges[PowerOutputLimits.CurrentLimit]);
        }
    }

    [Fact]
    public async Task PowerSupplyUsesDocumentedCommandsOverTcp()
    {
        // Worst case for safe state: OUTP:GEN OFF has no effect, so the driver must switch output 2 off itself.
        var hmp = new FakeHmp("HMP4040", 4) { GeneralOffSwitchesOutputsOff = false };

        var commands = await RunAgainstTcpFakeAsync(hmp, async (lease, cancellationToken) =>
        {
            Assert.StartsWith("Rohde&Schwarz,HMP4040", await lease.Instrument.IdentifyAsync(cancellationToken));
            Assert.True((await lease.Instrument.SelfCheckAsync(cancellationToken)).Passed);
            var supply = lease.GetCapability<IPowerSupply>();
            await supply.SetOutputAsync(2, 3.3, 0.5, true, cancellationToken);
            Assert.Equal((3.3, 0.5, true), await supply.GetOutputAsync(2, cancellationToken));
            await lease.Instrument.SafeStateAsync(cancellationToken);
        });

        Assert.Equal(
        [
            "*IDN?", "*CLS", "*IDN?", "*TST?",
            "INST OUT2", "VOLT 3.300", "CURR 0.5000", "OUTP ON", "SYST:ERR?",
            "INST OUT2", "VOLT?", "CURR?", "OUTP?",
            "OUTP:GEN OFF",
            "INST OUT1", "OUTP?",
            "INST OUT2", "OUTP?", "OUTP OFF", "OUTP?",
            "INST OUT3", "OUTP?",
            "INST OUT4", "OUTP?"
        ], commands);
        Assert.False(hmp.IsOn(2));
    }

    [Fact]
    public async Task SafeStateOnlyQueriesOutputsThatGeneralOffAlreadySwitchedOff()
    {
        var hmp = new FakeHmp("HMP2030", 3) { GeneralOffSwitchesOutputsOff = true };

        var commands = await RunAgainstTcpFakeAsync(hmp, async (lease, cancellationToken) =>
        {
            var supply = lease.GetCapability<IPowerSupply>();
            await supply.SetOutputAsync(1, 5, 1, true, cancellationToken);
            await supply.SetOutputAsync(3, 12, 1, true, cancellationToken);
            await lease.Instrument.SafeStateAsync(cancellationToken);
        });

        Assert.Equal(
        [
            "OUTP:GEN OFF", "INST OUT1", "OUTP?", "INST OUT2", "OUTP?", "INST OUT3", "OUTP?"
        ], commands.SkipWhile(command => command != "OUTP:GEN OFF"));
        Assert.DoesNotContain("OUTP OFF", commands);
    }

    [Fact]
    public async Task SafeStateFailsWhenAnOutputStaysOn()
    {
        var hmp = new FakeHmp("HMP4030", 3) { StuckOnChannel = 2 };
        ScpiProtocolException? error = null;

        var commands = await RunAgainstTcpFakeAsync(hmp, async (lease, cancellationToken) =>
        {
            await lease.GetCapability<IPowerSupply>().SetOutputAsync(2, 5, 1, true, cancellationToken);
            error = await Assert.ThrowsAsync<ScpiProtocolException>(() => lease.Instrument.SafeStateAsync(cancellationToken));
        });

        Assert.NotNull(error);
        Assert.Contains("output(s) 2 not confirmed OFF", error.Message);
        // The remaining outputs are still checked after the failing one.
        Assert.Equal(["INST OUT3", "OUTP?"], commands.TakeLast(2));
    }

    [Fact]
    public async Task SafeStateSwitchesOffOutputsWithUnknownStateAndReportsThem()
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "Rohde&Schwarz,HMP2020,1,1",
            "OUTP?" => "MAYBE",
            _ => null
        });
        var host = new DriverHost(new DriverRegistry().AddHmpScpi((_, _) => session), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(VisaConfiguration("HMP2020"));
        session.Writes.Clear();

        var error = await Assert.ThrowsAsync<ScpiProtocolException>(() => lease.Instrument.SafeStateAsync());
        Assert.Contains("output(s) 1, 2 not confirmed OFF", error.Message);
        Assert.Equal(
        [
            "OUTP:GEN OFF\n",
            "INST OUT1\n", "OUTP?\n", "OUTP OFF\n", "OUTP?\n",
            "INST OUT2\n", "OUTP?\n", "OUTP OFF\n", "OUTP?\n"
        ], session.Writes);
    }

    [Fact]
    public async Task PowerSupplyWorksOverVisaResource()
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "Rohde&Schwarz,HMP2020,1,1",
            _ => null
        });
        (string Resource, int Timeout)? opened = null;
        var registry = new DriverRegistry().AddHmpScpi((resource, timeout) =>
        {
            opened = (resource, timeout);
            return session;
        });
        var configuration = DriverConfiguration.Parse("psu1", "HmpScpi", JsonSerializer.Serialize(new
        {
            resource = "TCPIP0::192.168.0.20::5025::SOCKET", model = "HMP2020", timeoutMilliseconds = 3000
        }));
        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        await using (var lease = await host.AcquireAsync(configuration))
        {
            await lease.GetCapability<IPowerSupply>().SetOutputAsync(1, 5, 8, false);
        }

        Assert.Equal(("TCPIP0::192.168.0.20::5025::SOCKET", 3000), opened);
        // Disabling switches the output off before the new values are set.
        Assert.Equal(["*IDN?\n", "*CLS\n", "INST OUT1\n", "OUTP OFF\n", "VOLT 5.000\n", "CURR 8.0000\n", "SYST:ERR?\n"], session.Writes);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task SafeStateReopensTheConnectionAfterATimeout()
    {
        var sessions = new List<FakeVisaSession>();
        var registry = new DriverRegistry().AddHmpScpi((_, _) =>
        {
            var session = new FakeVisaSession(command => command switch
            {
                "*IDN?" => "Rohde&Schwarz,HMP2020,1,1",
                "OUTP?" => "0",
                _ => null
            });
            sessions.Add(session);
            return session;
        });
        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(VisaConfiguration("HMP2020"));
        sessions[0].TimeoutOnRead = true;
        await Assert.ThrowsAsync<TimeoutException>(() => lease.Instrument.IdentifyAsync());
        Assert.True(sessions[0].Disposed);

        await lease.Instrument.SafeStateAsync();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(["OUTP:GEN OFF\n", "INST OUT1\n", "OUTP?\n", "INST OUT2\n", "OUTP?\n"], sessions[1].Writes);
    }

    [Fact]
    public async Task RejectedOutputSettingsAreReported()
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "Rohde&Schwarz,HMP2020,1,1",
            "SYST:ERR?" => "-222,\"Data out of range\"",
            _ => null
        });
        var host = new DriverHost(new DriverRegistry().AddHmpScpi((_, _) => session), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(VisaConfiguration("HMP2020"));

        var error = await Assert.ThrowsAsync<ScpiProtocolException>(() =>
            lease.GetCapability<IPowerSupply>().SetOutputAsync(1, 5, 1, true));
        Assert.Contains("Data out of range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectFailsWhenTheInstrumentIsADifferentModel()
    {
        var session = new FakeVisaSession(_ => "Rohde&Schwarz,HMP4040,1,1");
        var registry = new DriverRegistry().AddHmpScpi((_, _) => session);
        var host = new DriverHost(registry, NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<ScpiProtocolException>(async () => await host.AcquireAsync(VisaConfiguration("HMP2030")));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task OutputsOutsideTheModelLimitsAreRejectedBeforeAnyCommand()
    {
        var session = new FakeVisaSession(_ => "Rohde&Schwarz,HMP2030,1,1");
        var host = new DriverHost(new DriverRegistry().AddHmpScpi((_, _) => session), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(VisaConfiguration("HMP2030"));
        var supply = lease.GetCapability<IPowerSupply>();
        session.Writes.Clear();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.SetOutputAsync(1, 5, 6, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.SetOutputAsync(1, 33, 1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.SetOutputAsync(4, 5, 1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.GetOutputAsync(4));
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task InvalidOutputStateReadbackIsAProtocolError()
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "Rohde&Schwarz,HMP4030,1,1",
            "VOLT?" => "1.000",
            "CURR?" => "0.1000",
            _ => "MAYBE"
        });
        var host = new DriverHost(new DriverRegistry().AddHmpScpi((_, _) => session), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(VisaConfiguration("HMP4030"));

        await Assert.ThrowsAsync<ScpiProtocolException>(() => lease.GetCapability<IPowerSupply>().GetOutputAsync(1));
    }

    [Theory]
    [InlineData("{\"host\":\"127.0.0.1\",\"model\":\"HMP9999\"}")]
    [InlineData("{\"host\":\"127.0.0.1\"}")]
    [InlineData("{\"model\":\"HMP4040\"}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"resource\":\"TCPIP0::1::SOCKET\",\"model\":\"HMP4040\"}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"port\":0,\"model\":\"HMP4040\"}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"model\":\"HMP4040\",\"timeoutMilliseconds\":0}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"model\":\"HMP4040\",\"output\":\"OUT1\"}")]
    [InlineData("{\"host\":\"127.0.0.1\",\"model\":\"HMP4040\",\"allowUnverifiedScpiCommands\":true}")]
    [InlineData("{\"resource\":\"TCPIP0::1::SOCKET\\n*RST\",\"model\":\"HMP4040\"}")]
    public void InvalidConnectionOptionsAreRejected(string json)
    {
        var registry = new DriverRegistry().AddHmpScpi();

        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("psu1", "HmpScpi", json)));
    }

    [Theory]
    [InlineData(1, 5.0, 1.0, true)]
    [InlineData(1, 5.0, 6.0, false)]
    [InlineData(4, 5.0, 1.0, false)]
    public void OptimizerChecksPowerStepsAgainstTheModelProfile(int channel, double voltage, double current, bool valid)
    {
        var configuration = VisaConfiguration("HMP2030");
        var profile = new DriverRegistry().AddHmpScpi().GetProfile(configuration);
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["psu1"] = new(configuration, profile)
        };
        var plan = new TestPlan
        {
            ArtNr = "4711", Fz = "A", Version = 1,
            Steps =
            [
                new TestStep
                {
                    Order = 0, Name = "Versorgung", DeviceKey = "psu1", StepType = StepType.SetPower,
                    ParametersJson = StepParameters.Serialize(new SetPowerParameters(voltage, current, true, channel))
                }
            ]
        };

        if (valid)
            Assert.Single(new ExecutionPlanOptimizer().Optimize(plan, definitions).Steps);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionPlanOptimizer().Optimize(plan, definitions));
    }

    [Fact]
    public async Task ScpiQueryTimeoutIsReportedAndConnectionCanBeDisposed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = listener.AcceptTcpClientAsync(cancellation.Token);
        var host = new DriverHost(new DriverRegistry().AddHmpScpi(), NullLoggerFactory.Instance);
        var configuration = DriverConfiguration.Parse("psu-timeout", "HmpScpi", JsonSerializer.Serialize(new
        {
            host = "127.0.0.1", port, model = "HMP4040", timeoutMilliseconds = 1_000
        }));
        var acquisition = host.AcquireAsync(configuration, cancellation.Token).AsTask();
        using var peer = await accepted;
        var commandReader = new StreamReader(peer.GetStream(), Encoding.ASCII, leaveOpen: true);
        await using var commandWriter = new StreamWriter(peer.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        Assert.Equal("*IDN?", await commandReader.ReadLineAsync(cancellation.Token));
        await commandWriter.WriteLineAsync("Rohde&Schwarz,HMP4040,1,1".AsMemory(), cancellation.Token);
        Assert.Equal("*CLS", await commandReader.ReadLineAsync(cancellation.Token));
        await using var lease = await acquisition;

        var identify = lease.Instrument.IdentifyAsync(cancellation.Token);
        Assert.Equal("*IDN?", await commandReader.ReadLineAsync(cancellation.Token));
        await Assert.ThrowsAsync<TimeoutException>(() => identify);
        listener.Stop();
    }

    private static DriverConfiguration TcpConfiguration(int port, string model) =>
        DriverConfiguration.Parse("psu1", "HmpScpi", JsonSerializer.Serialize(new
        {
            host = "127.0.0.1", port, model, timeoutMilliseconds = 2_000
        }));

    private static DriverConfiguration VisaConfiguration(string model) =>
        DriverConfiguration.Parse("psu1", "HmpScpi", JsonSerializer.Serialize(new
        {
            resource = "TCPIP0::192.168.0.20::5025::SOCKET", model
        }));

    private static async Task<List<string>> RunAgainstTcpFakeAsync(FakeHmp hmp, Func<IDeviceLease, CancellationToken, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commands = new List<string>();
        var server = ServeAsync(listener, commands, hmp.Handle, cancellation.Token);
        var host = new DriverHost(new DriverRegistry().AddHmpScpi(), NullLoggerFactory.Instance);
        await using (var lease = await host.AcquireAsync(TcpConfiguration(port, hmp.Model.ToLowerInvariant()), cancellation.Token))
        {
            await action(lease, cancellation.Token);
        }
        listener.Stop();
        await server;
        return commands;
    }

    private static async Task ServeAsync(TcpListener listener, List<string> commands,
        Func<string, string?> respond, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        while (!cancellationToken.IsCancellationRequested)
        {
            var command = await reader.ReadLineAsync(cancellationToken);
            if (command is null) return;
            commands.Add(command);
            var response = respond(command);
            if (response is not null)
                await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }
    }

    /// <summary>
    /// Minimal stateful HMP for tests. Whether OUTP:GEN OFF also switches outputs that were
    /// switched on with OUTP ON is not clear from the manual, so tests can choose both behaviours.
    /// </summary>
    private sealed class FakeHmp(string model, int channelCount)
    {
        private readonly bool[] outputOn = new bool[channelCount + 1];
        private readonly string[] voltage = Enumerable.Repeat("0.000", channelCount + 1).ToArray();
        private readonly string[] current = Enumerable.Repeat("0.0000", channelCount + 1).ToArray();
        private int selected = 1;

        public string Model => model;
        public bool GeneralOffSwitchesOutputsOff { get; init; } = true;
        public int StuckOnChannel { get; init; }

        public bool IsOn(int channel) => outputOn[channel];

        public string? Handle(string command)
        {
            if (command.StartsWith("INST OUT", StringComparison.Ordinal))
            {
                selected = int.Parse(command["INST OUT".Length..], System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
            if (command.StartsWith("VOLT ", StringComparison.Ordinal)) { voltage[selected] = command[5..]; return null; }
            if (command.StartsWith("CURR ", StringComparison.Ordinal)) { current[selected] = command[5..]; return null; }
            switch (command)
            {
                case "*IDN?": return $"Rohde&Schwarz,{model},123456,HW50020001/SW2.51";
                case "*TST?": return "0";
                case "*CLS": return null;
                case "SYST:ERR?": return "0,\"No error\"";
                case "VOLT?": return voltage[selected];
                case "CURR?": return current[selected];
                case "OUTP?": return outputOn[selected] ? "1" : "0";
                case "OUTP ON": outputOn[selected] = true; return null;
                case "OUTP OFF": SwitchOff(selected); return null;
                case "OUTP:GEN OFF":
                    if (GeneralOffSwitchesOutputsOff)
                        for (var channel = 1; channel <= channelCount; channel++) SwitchOff(channel);
                    return null;
                default: throw new InvalidOperationException($"Unexpected command '{command}'.");
            }
        }

        private void SwitchOff(int channel)
        {
            if (channel != StuckOnChannel) outputOn[channel] = false;
        }
    }
}
