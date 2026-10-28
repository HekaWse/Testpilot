using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class Phase5Ag34980ScpiTests
{
    private const string VerifiedTestProfile = """
        id: test-34980a-module
        model: Synthetic test module
        channelCount: 2
        channelNumbering: channel
        capabilities: [SwitchMultimeter]
        state:
          settings: [function, range, nplc]
          invalidatedByCommand:
            relay: [function, range, nplc]
          unknownCommandInvalidatesAll: true
        channels:
          - number: 1003
            name: Test channel
            type: Voltage
            supportedFunctions: [VOLT:DC]
            valueRanges: {}
          - number: 1004
            name: Second test channel
            type: Voltage
            supportedFunctions: [VOLT:DC]
            valueRanges: {}
        specialCases: []
        todos: []
        """;

    [Fact]
    public async Task SwitchMultimeterConfiguresMeasuresRoutesAndSafesState()
    {
        var profilePath = WriteProfile(VerifiedTestProfile);
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var commands = new List<string>();
            var server = ServeAsync(listener, commands, Respond, cancellation.Token);
            var configuration = CreateConfiguration(port, profilePath);
            var host = new DriverHost(new DriverRegistry().AddAg34980Scpi(), NullLoggerFactory.Instance);
            await using (var lease = await host.AcquireAsync(configuration, cancellation.Token))
            {
                Assert.Equal("Agilent Technologies,34980A,123,1.0", await lease.Instrument.IdentifyAsync(cancellation.Token));
                Assert.True((await lease.Instrument.SelfCheckAsync(cancellation.Token)).Passed);
                var meter = lease.GetCapability<ISwitchMultimeter>();
                await Assert.ThrowsAsync<InvalidOperationException>(() => meter.MeasureAsync(1003, cancellation.Token));
                // Like the optimizer's Init phase: both channels are configured before the first measurement.
                await meter.ConfigureMeasurementAsync(new MeasurementConfiguration(1003, "VOLT:DC", 10, 1), cancellation.Token);
                await meter.ConfigureMeasurementAsync(new MeasurementConfiguration(1004, "VOLT:DC"), cancellation.Token);
                Assert.Equal(3.298, await meter.MeasureAsync(1003, cancellation.Token));
                await meter.SetRelayAsync(1003, true, cancellation.Token);
                Assert.Equal(3.298, await meter.MeasureAsync(1004, cancellation.Token));
                await lease.Instrument.SafeStateAsync(cancellation.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => meter.MeasureAsync(1003, cancellation.Token));
            }
            listener.Stop();
            await server;

            Assert.Equal(
            [
                "*IDN?", "*RST", "*CLS", "*IDN?", "*TST?",
                "CONF:VOLT:DC 10,(@1003)", "SENS:VOLT:DC:NPLC 1,(@1003)", "SYST:ERR?",
                "CONF:VOLT:DC AUTO,(@1004)", "SYST:ERR?",
                "ROUT:SCAN (@1003)", "READ?",
                "ROUT:CLOS (@1003)", "SYST:ERR?",
                "ROUT:SCAN (@1004)", "READ?",
                "ROUT:OPEN:ALL", "SYST:ERR?"
            ], commands);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public async Task InstrumentErrorsAfterConfigurationAreReported()
    {
        var profilePath = WriteProfile(VerifiedTestProfile);
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var errors = new Queue<string>(["-222,\"Data out of range\"", "+0,\"No error\""]);
            var commands = new List<string>();
            var server = ServeAsync(listener, commands,
                command => command == "SYST:ERR?" ? errors.Dequeue() : Respond(command), cancellation.Token);
            var host = new DriverHost(new DriverRegistry().AddAg34980Scpi(), NullLoggerFactory.Instance);
            await using (var lease = await host.AcquireAsync(CreateConfiguration(port, profilePath), cancellation.Token))
            {
                var meter = lease.GetCapability<ISwitchMultimeter>();
                var error = await Assert.ThrowsAsync<ScpiProtocolException>(() =>
                    meter.ConfigureMeasurementAsync(new MeasurementConfiguration(1003, "VOLT:DC", 10), cancellation.Token));
                Assert.Contains("Data out of range", error.Message, StringComparison.Ordinal);
                Assert.Contains("CONF:VOLT:DC 10,(@1003)", error.Message, StringComparison.Ordinal);
                // A rejected configuration does not count as configured.
                await Assert.ThrowsAsync<InvalidOperationException>(() => meter.MeasureAsync(1003, cancellation.Token));
            }
            listener.Stop();
            await server;
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public void NplcIsRejectedForAcFunctions()
    {
        var profile = SimulatedDeviceProfiles.Create("dmm", "Simulated", DeviceCapability.SwitchMultimeter, [1]);

        Assert.Throws<ArgumentException>(() => new MeasurementConfiguration(1, "VOLT:AC", null, 1).Validate(profile));
        Assert.Throws<ArgumentException>(() => new MeasurementConfiguration(1, "CURR:AC", null, 1).Validate(profile));
        new MeasurementConfiguration(1, "VOLT:AC").Validate(profile);
        new MeasurementConfiguration(1, "FRES", null, 10).Validate(profile);
    }

    [Fact]
    public void ModuleProfileWithUnverifiedFactsCannotBeConnected()
    {
        var path = WriteProfile(VerifiedTestProfile.Replace("todos: []", "todos: [\"TODO: aus Datenblatt prüfen\"]", StringComparison.Ordinal));
        try
        {
            var configuration = CreateConfiguration(5025, path);
            Assert.Throws<InvalidDataException>(() => new DriverRegistry().AddAg34980Scpi().GetProfile(configuration));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProfileMustBeExplicitlyConfigured()
    {
        var configuration = DriverConfiguration.Parse("mux", "Ag34980Scpi", "{\"host\":\"127.0.0.1\"}");

        Assert.Throws<JsonException>(() => new DriverRegistry().AddAg34980Scpi().GetProfile(configuration));
    }

    private static DriverConfiguration CreateConfiguration(int port, string profilePath) =>
        DriverConfiguration.Parse("ag34980-test", "Ag34980Scpi", JsonSerializer.Serialize(new
        {
            host = "127.0.0.1", port, profilePath, timeoutMilliseconds = 2000
        }));

    private static string WriteProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"testpilot-ag34980-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string? Respond(string command) => command switch
    {
        "*IDN?" => "Agilent Technologies,34980A,123,1.0",
        "*TST?" => "0",
        "SYST:ERR?" => "+0,\"No error\"",
        "READ?" => 3.298.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private static async Task ServeAsync(TcpListener listener, List<string> commands, Func<string, string?> respond,
        CancellationToken cancellationToken)
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
            if (response is not null) await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }
    }
}
