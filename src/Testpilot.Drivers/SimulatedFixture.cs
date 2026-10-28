using System.Collections.Concurrent;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>State of a simulated test adapter.</summary>
public sealed record SimulatedFixtureState(bool Closed, bool Locked, FixtureLamp Lamp);

/// <summary>
/// The "hands" of the simulation: a simulated adapter has no hold-down switch, so it is closed from outside
/// (operator window in simulation mode, tests). The state is kept per device key for the whole process.
/// </summary>
public static class SimulatedFixtures
{
    private static readonly ConcurrentDictionary<string, SimulatedFixtureState> States = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> FailingReleases = new(StringComparer.Ordinal);

    /// <summary>Closes the adapter, as the operator would do by pressing the hold-down.</summary>
    public static void Close(string deviceKey) =>
        States.AddOrUpdate(deviceKey, _ => new(true, false, FixtureLamp.None), (_, state) => state with { Closed = true });

    public static SimulatedFixtureState GetState(string deviceKey) =>
        States.GetValueOrDefault(deviceKey, new SimulatedFixtureState(false, false, FixtureLamp.None));

    /// <summary>Lets the next release of the adapter fail once (simulates a lost connection, for tests).</summary>
    public static void FailNextRelease(string deviceKey) => FailingReleases[deviceKey] = true;

    internal static bool TakeReleaseFailure(string deviceKey) => FailingReleases.TryRemove(deviceKey, out _);

    internal static void Set(string deviceKey, SimulatedFixtureState state) => States[deviceKey] = state;
}

/// <summary>Simulated test adapter: locking needs a closed adapter, releasing opens it (like the pneumatic hold-down).</summary>
internal sealed class SimulatedFixture : ITestFixture
{
    private readonly string deviceKey;
    private bool connected;

    public SimulatedFixture(DriverConfiguration configuration)
    {
        SimulatedProfiles.ValidateNoOptions(configuration);
        deviceKey = configuration.DeviceKey;
    }

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "simulated-fixture", "Simulated test adapter", DeviceCapability.TestFixture);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connected) throw new InvalidOperationException("The simulated instrument is already connected.");
        connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connected = false;
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult("TESTPILOT,SIMULATED-FIXTURE,1.0");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, "Simulated self-check passed."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default) => ReleaseAsync(FixtureLamp.None, cancellationToken);

    public Task<bool> IsClosedAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(SimulatedFixtures.GetState(deviceKey).Closed);
    }

    public Task LockAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        var state = SimulatedFixtures.GetState(deviceKey);
        if (!state.Closed)
            throw new InvalidOperationException("The simulated adapter is not closed and cannot be locked.");
        SimulatedFixtures.Set(deviceKey, state with { Locked = true, Lamp = FixtureLamp.None });
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(FixtureLamp lamp, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        if (SimulatedFixtures.TakeReleaseFailure(deviceKey))
            throw new IOException("Simulated release failure.");
        SimulatedFixtures.Set(deviceKey, new SimulatedFixtureState(false, false, lamp));
        return Task.CompletedTask;
    }

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }
}
