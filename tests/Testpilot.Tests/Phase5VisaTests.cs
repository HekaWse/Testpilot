using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class Phase5VisaTests
{
    [Fact]
    public async Task VisaTransportAppendsNewlineAndTrimsResponses()
    {
        var session = new FakeVisaSession(command => command == "*IDN?" ? "  Vendor,Model,1,1 " : null);
        await using var transport = new VisaScpiTransport("GPIB0::5::INSTR", 1000, (_, _) => session);
        await transport.ConnectAsync(CancellationToken.None);

        await transport.WriteAsync("*CLS", CancellationToken.None);
        Assert.Equal("Vendor,Model,1,1", await transport.QueryAsync("*IDN?", CancellationToken.None));
        Assert.Equal(["*CLS\n", "*IDN?\n"], session.Writes);
    }

    [Fact]
    public async Task VisaTimeoutClosesTheSessionSoLateResponsesCannotDesynchronize()
    {
        var session = new FakeVisaSession(_ => "1") { TimeoutOnRead = true };
        await using var transport = new VisaScpiTransport("GPIB0::5::INSTR", 1000, (_, _) => session);
        await transport.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => transport.QueryAsync("*OPC?", CancellationToken.None));
        Assert.True(session.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.WriteAsync("*CLS", CancellationToken.None));
    }

    [Fact]
    public async Task OverlongVisaResponseIsRejected()
    {
        var session = new FakeVisaSession(_ => new string('x', 8193));
        await using var transport = new VisaScpiTransport("GPIB0::5::INSTR", 1000, (_, _) => session);
        await transport.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ScpiProtocolException>(() => transport.QueryAsync("*IDN?", CancellationToken.None));
    }

    [Fact]
    public async Task VisaGenericIdentifiesAndSendsOnlyConfiguredSafeStateCommands()
    {
        var session = new FakeVisaSession(command => command == "*IDN?" ? "Keysight,34465A,1,1" : null);
        var registry = new DriverRegistry().AddVisaGeneric((_, _) => session);
        var configuration = DriverConfiguration.Parse("generic1", "VisaGeneric", JsonSerializer.Serialize(new
        {
            resource = "USB0::0x2A8D::0x0101::MY1::INSTR", safeStateCommands = new[] { "OUTP OFF", "ROUT:OPEN:ALL" }
        }));
        var profile = registry.GetProfile(configuration);
        Assert.Empty(profile.Capabilities);
        Assert.Null(profile.ChannelCount);
        Assert.Contains(profile.Todos, todo => todo.Contains(DeviceProfile.DataSheetTodo, StringComparison.Ordinal));

        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        await using (var lease = await host.AcquireAsync(configuration))
        {
            Assert.Equal("Keysight,34465A,1,1", await lease.Instrument.IdentifyAsync());
            Assert.True((await lease.Instrument.SelfCheckAsync()).Passed);
            await lease.Instrument.SafeStateAsync();
        }

        Assert.Equal(["*IDN?\n", "*IDN?\n", "OUTP OFF\n", "ROUT:OPEN:ALL\n"], session.Writes);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"host\":\"127.0.0.1\"}")]
    [InlineData("{\"resource\":\"GPIB0::5::INSTR\",\"timeoutMilliseconds\":0}")]
    [InlineData("{\"resource\":\"GPIB0::5::INSTR\",\"safeStateCommands\":[\"OUTP OFF\\n*RST\"]}")]
    [InlineData("{\"resource\":\"GPIB0::5::INSTR\",\"safeStateCommands\":[\"\"]}")]
    public void VisaGenericRejectsInvalidOptions(string json)
    {
        var registry = new DriverRegistry().AddVisaGeneric((_, _) => throw new InvalidOperationException());

        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("generic1", "VisaGeneric", json)));
    }

    [Fact]
    public async Task Ag34980ScpiAcceptsAVisaResource()
    {
        const string profileYaml = """
            id: visa-test
            model: Synthetic test module
            channelCount: 1
            channelNumbering: channel
            capabilities: [SwitchMultimeter]
            state:
              settings: [function]
              invalidatedByCommand: {}
              unknownCommandInvalidatesAll: true
            channels:
              - number: 1003
                name: Test channel
                type: Voltage
                supportedFunctions: [VOLT:DC]
                valueRanges: {}
            specialCases: []
            todos: []
            """;
        var profilePath = Path.Combine(Path.GetTempPath(), $"testpilot-visa-profile-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(profilePath, profileYaml);
        try
        {
            var session = new FakeVisaSession(command => command == "*IDN?" ? "Agilent Technologies,34980A,1,1" : null);
            string? openedResource = null;
            var registry = new DriverRegistry().AddAg34980Scpi((resource, _) =>
            {
                openedResource = resource;
                return session;
            });
            var configuration = DriverConfiguration.Parse("dmm1", "Ag34980Scpi", JsonSerializer.Serialize(new
            {
                resource = "GPIB0::9::INSTR", profilePath
            }));
            var host = new DriverHost(registry, NullLoggerFactory.Instance);
            await using (var lease = await host.AcquireAsync(configuration))
            {
                Assert.Equal("Agilent Technologies,34980A,1,1", await lease.Instrument.IdentifyAsync());
            }

            Assert.Equal("GPIB0::9::INSTR", openedResource);
            Assert.True(session.Disposed);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }
}
