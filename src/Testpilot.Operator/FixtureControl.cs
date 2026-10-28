using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Operator;

/// <summary>
/// Test adapter and fixed code reader of the station for the automatic start.
/// The adapter stays leased for the whole program lifetime (it is polled several times per second);
/// the code reader is only leased while reading, so that Scan steps of a plan can use the same device.
/// Only called from the window thread, never concurrently.
/// </summary>
public sealed class FixtureControl : IAsyncDisposable
{
    private readonly IDriverHost host;
    private readonly DriverConfiguration fixtureConfiguration;
    private readonly DriverConfiguration? readerConfiguration;
    private readonly TimeSpan? timeout;
    private IDeviceLease? lease;
    private ITestFixture? fixture;

    /// <param name="timeout">Limit for every adapter or reader call; null disables it (Debug:DisableTimeouts).</param>
    public FixtureControl(IDriverHost host, DriverConfiguration fixtureConfiguration,
        DriverConfiguration? readerConfiguration, TimeSpan? timeout)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.fixtureConfiguration = fixtureConfiguration ?? throw new ArgumentNullException(nameof(fixtureConfiguration));
        this.readerConfiguration = readerConfiguration;
        this.timeout = timeout;
    }

    public string FixtureDeviceKey => fixtureConfiguration.DeviceKey;
    public bool HasReader => readerConfiguration is not null;

    /// <summary>Simulation only: reading the serial number fails as if the reader found no code.</summary>
    public bool SimulateReadFailure { get; set; }

    /// <summary>Simulation only: closes the simulated adapter (driver SimulatedFixture) as the operator would.</summary>
    public void SimulateClose() => SimulatedFixtures.Close(FixtureDeviceKey);

    public Task<bool> IsClosedAsync(CancellationToken cancellationToken = default) =>
        CallAsync((device, token) => device.IsClosedAsync(token), cancellationToken);

    public Task LockAsync(CancellationToken cancellationToken = default) =>
        CallAsync(async (device, token) =>
        {
            await device.LockAsync(token);
            return true;
        }, cancellationToken);

    public Task ReleaseAsync(FixtureLamp lamp, CancellationToken cancellationToken = default) =>
        CallAsync(async (device, token) =>
        {
            await device.ReleaseAsync(lamp, token);
            return true;
        }, cancellationToken);

    /// <summary>Reads the serial number with the fixed code reader. Throws when no code was read.</summary>
    public async Task<string> ReadSerialAsync(CancellationToken cancellationToken = default)
    {
        if (readerConfiguration is null)
            throw new InvalidOperationException("Kein Code-Leser konfiguriert (Operator:SerialReaderDevice).");
        if (SimulateReadFailure)
            throw new BarcodeReadException("Simulierter Lesefehler: kein Code gefunden.");
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        try
        {
            await using var readerLease = await host.AcquireAsync(readerConfiguration, timeoutSource.Token);
            var code = await readerLease.GetCapability<IBarcodeScanner>().ScanAsync(timeoutSource.Token);
            return code.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Code-Leser '{readerConfiguration.DeviceKey}' hat nicht innerhalb von {timeout?.TotalSeconds:0} s geantwortet.");
        }
    }

    /// <summary>Closes the adapter connection after an error; the next call connects again.</summary>
    public async Task ResetConnectionAsync()
    {
        var current = lease;
        lease = null;
        fixture = null;
        if (current is not null)
            await current.DisposeAsync();
    }

    /// <summary>Brings the adapter into its safe state (self-hold off, no lamp) and releases the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (fixture is not null)
            {
                using var timeoutSource = CreateTimeoutSource(CancellationToken.None);
                await fixture.SafeStateAsync(timeoutSource.Token);
            }
        }
        finally
        {
            await ResetConnectionAsync();
        }
    }

    private async Task<T> CallAsync<T>(Func<ITestFixture, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        try
        {
            if (fixture is null)
            {
                var acquired = await host.AcquireAsync(fixtureConfiguration, timeoutSource.Token);
                try
                {
                    fixture = acquired.GetCapability<ITestFixture>();
                }
                catch
                {
                    await acquired.DisposeAsync();
                    throw;
                }
                lease = acquired;
            }
            return await action(fixture, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Adapter-Steuerung '{FixtureDeviceKey}' hat nicht innerhalb von {timeout?.TotalSeconds:0} s geantwortet.");
        }
    }

    private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
            source.CancelAfter(limit);
        return source;
    }
}
