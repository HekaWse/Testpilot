using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;

namespace Testpilot.Sequencer;

public sealed class DeviceSession : IAsyncDisposable
{
    private readonly IDriverHost driverHost;
    private readonly Dictionary<string, IDeviceLease> leases = new(StringComparer.Ordinal);
    private int disposed;

    public DeviceSession(IDriverHost driverHost) =>
        this.driverHost = driverHost ?? throw new ArgumentNullException(nameof(driverHost));

    public async Task OpenAsync(IReadOnlyList<ExecutionDevice> devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var unbound = devices.Where(device => device.Driver is null || device.ConnectionParametersJson is null).ToArray();
        if (unbound.Length != 0)
            throw new InvalidOperationException(
                $"Geräte ohne Treiberzuordnung zum Prüfsystem: {string.Join(", ", unbound.Select(device => device.DeviceKey))}. Der Plan muss über RunPreparation an ein Prüfsystem gebunden werden.");
        try
        {
            foreach (var device in devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configuration = DriverConfiguration.Parse(device.DeviceKey, device.Driver!, device.ConnectionParametersJson!);
                var lease = await driverHost.AcquireAsync(configuration, cancellationToken);
                leases.Add(device.DeviceKey, lease);
            }
        }
        catch (Exception openFailure)
        {
            var cleanupFailures = new List<Exception>();
            foreach (var lease in leases.Values)
            {
                try
                {
                    await lease.Instrument.SafeStateAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(new InvalidOperationException($"Safe state failed for device '{lease.DeviceKey}'.", exception));
                }
            }
            cleanupFailures.AddRange(await CleanupAsync());
            if (cleanupFailures.Count != 0)
                throw new AggregateException("Device acquisition failed and session cleanup also failed.", [openFailure, .. cleanupFailures]);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(openFailure).Throw();
            throw;
        }
    }

    public bool Contains(string deviceKey) => leases.ContainsKey(deviceKey);

    public T GetCapability<T>(string deviceKey) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return leases.TryGetValue(deviceKey, out var lease)
            ? lease.GetCapability<T>()
            : throw new KeyNotFoundException($"Device '{deviceKey}' is not part of this session.");
    }

    public IInstrument GetInstrument(string deviceKey)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return leases.TryGetValue(deviceKey, out var lease)
            ? lease.Instrument
            : throw new KeyNotFoundException($"Device '{deviceKey}' is not part of this session.");
    }

    public async Task<IReadOnlyList<RunDevice>> GetDeviceSnapshotsAsync(CancellationToken cancellationToken)
    {
        var result = new List<RunDevice>(leases.Count);
        foreach (var lease in leases.Values.OrderBy(item => item.DeviceKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new RunDevice
            {
                DeviceKey = lease.DeviceKey,
                Identification = await lease.Instrument.IdentifyAsync(cancellationToken)
            });
        }
        return result;
    }

    public async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var lease in leases.Values.OrderBy(item => item.DeviceKey, StringComparer.Ordinal))
        {
            try
            {
                await lease.Instrument.SafeStateAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException($"Safe state failed for device '{lease.DeviceKey}'.", exception));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("One or more devices could not be placed in a safe state.", failures);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        var failures = await CleanupAsync();
        if (failures.Count != 0)
            throw new AggregateException("One or more device leases could not be released.", failures);
    }

    private async Task<List<Exception>> CleanupAsync()
    {
        var failures = new List<Exception>();
        foreach (var lease in leases.Values.Reverse())
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException($"Disconnect failed for device '{lease.DeviceKey}'.", exception));
            }
        }
        leases.Clear();
        return failures;
    }
}
