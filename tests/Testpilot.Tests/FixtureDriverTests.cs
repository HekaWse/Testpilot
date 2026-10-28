using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class FixtureDriverTests
{
    [Fact]
    public async Task SimulatedFixtureLocksOnlyWhenClosedAndReleaseOpensWithLamp()
    {
        var key = $"fixture-{Guid.NewGuid():N}";
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(DriverConfiguration.Parse(key, "SimulatedFixture", "{}"));
        var fixture = lease.GetCapability<ITestFixture>();

        Assert.False(await fixture.IsClosedAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.LockAsync());

        SimulatedFixtures.Close(key);
        Assert.True(await fixture.IsClosedAsync());
        await fixture.LockAsync();
        Assert.Equal(new SimulatedFixtureState(true, true, FixtureLamp.None), SimulatedFixtures.GetState(key));

        await fixture.ReleaseAsync(FixtureLamp.Fail);
        Assert.Equal(new SimulatedFixtureState(false, false, FixtureLamp.Fail), SimulatedFixtures.GetState(key));
        Assert.Equal(DeviceCapability.TestFixture, Assert.Single(lease.Instrument.Profile.Capabilities));
    }

    [Theory]
    [InlineData("+2.54000000E+02", true)]
    [InlineData("+2.55000000E+02", true)]
    [InlineData("+2.53000000E+02", false)]
    [InlineData("+0.00000000E+00", false)]
    public async Task Ps05ReadsTheAdapterStateLikeTheLegacyProgram(string response, bool closed)
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "HEWLETT-PACKARD,34970A,0,13-2-2",
            "MEAS:VOLT:DC? (@301)" => response,
            _ => null
        });
        await using var lease = await AcquirePs05Async(session);
        session.Writes.Clear();

        Assert.Equal(closed, await lease.GetCapability<ITestFixture>().IsClosedAsync());
        Assert.Equal(["MEAS:VOLT:DC? (@301)\n"], session.Writes);
    }

    [Fact]
    public async Task Ps05WritesTheLegacyOutputBytes()
    {
        var session = new FakeVisaSession(command => command == "*IDN?" ? "HEWLETT-PACKARD,34970A,0,13-2-2" : null);
        await using var lease = await AcquirePs05Async(session);
        var fixture = lease.GetCapability<ITestFixture>();
        session.Writes.Clear();

        await fixture.LockAsync();
        await fixture.ReleaseAsync(FixtureLamp.Pass);
        await fixture.ReleaseAsync(FixtureLamp.Fail);
        await fixture.ReleaseAsync(FixtureLamp.None);
        await lease.Instrument.SafeStateAsync();

        Assert.Equal(
        [
            "SOUR:DIG:DATA:BYTE 250,(@302)\n",
            "SOUR:DIG:DATA:BYTE 253,(@302)\n",
            "SOUR:DIG:DATA:BYTE 254,(@302)\n",
            "SOUR:DIG:DATA:BYTE 255,(@302)\n",
            "SOUR:DIG:DATA:BYTE 255,(@302)\n"
        ], session.Writes);
    }

    [Fact]
    public async Task Ps05RejectsAnInvalidAdapterStateAndHasNoSideEffectsOnConnect()
    {
        var session = new FakeVisaSession(command => command switch
        {
            "*IDN?" => "HEWLETT-PACKARD,34970A,0,13-2-2",
            "MEAS:VOLT:DC? (@301)" => "garbage",
            _ => null
        });
        await using var lease = await AcquirePs05Async(session);

        Assert.DoesNotContain(session.Writes, command => command.StartsWith("SOUR:", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ScpiProtocolException>(() => lease.GetCapability<ITestFixture>().IsClosedAsync());
    }

    [Fact]
    public void Ps05ProfileAndOptionsAreValidated()
    {
        var registry = new DriverRegistry().AddPs05Fixture();
        var profile = registry.GetProfile(DriverConfiguration.Parse("adapter", "Ps05Fixture", "{\"resource\":\"GPIB0::9::INSTR\"}"));

        Assert.Equal(DeviceCapability.TestFixture, Assert.Single(profile.Capabilities));
        Assert.Equal([301, 302], profile.Channels.Select(channel => channel.Number));
        Assert.ThrowsAny<Exception>(() => registry.Create(DriverConfiguration.Parse("adapter", "Ps05Fixture", "{\"resource\":\"GPIB0::9::INSTR\",\"unknown\":1}")));
        Assert.ThrowsAny<Exception>(() => registry.Create(DriverConfiguration.Parse("adapter", "Ps05Fixture", "{\"resource\":\"GPIB0::9::INSTR\",\"timeoutMilliseconds\":0}")));
        Assert.Contains("Ps05Fixture", new DriverRegistry().AddAllDrivers().RegisteredDrivers);
    }

    private static async Task<IDeviceLease> AcquirePs05Async(FakeVisaSession session)
    {
        var host = new DriverHost(new DriverRegistry().AddPs05Fixture((_, _) => session), NullLoggerFactory.Instance);
        return await host.AcquireAsync(DriverConfiguration.Parse($"ps05-{Guid.NewGuid():N}", "Ps05Fixture",
            "{\"resource\":\"GPIB0::9::INSTR\"}"));
    }
}
