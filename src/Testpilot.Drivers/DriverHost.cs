using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

public sealed class DriverHost : IDriverHost
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceLocks = new(StringComparer.Ordinal);
    private readonly DriverRegistry registry;
    private readonly ILoggerFactory loggerFactory;

    public DriverHost(DriverRegistry registry, ILoggerFactory loggerFactory)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async ValueTask<IDeviceLease> AcquireAsync(DriverConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var deviceLock = DeviceLocks.GetOrAdd(configuration.DeviceKey, static _ => new SemaphoreSlim(1, 1));
        await deviceLock.WaitAsync(cancellationToken);
        IInstrument? instrument = null;
        DeviceTraceInstrument? traced = null;
        try
        {
            instrument = registry.Create(configuration);
            traced = new DeviceTraceInstrument(instrument, loggerFactory.CreateLogger<DeviceTraceInstrument>());
            await traced.ConnectAsync(cancellationToken);
            return new DeviceLease(configuration.DeviceKey, instrument, traced, deviceLock, loggerFactory);
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            var cleanupTarget = (IInstrument?)traced ?? instrument;
            if (cleanupTarget is not null)
            {
                try
                {
                    await cleanupTarget.DisconnectAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
            deviceLock.Release();
            if (cleanupFailure is not null)
                throw new AggregateException("Driver acquisition failed and the partially created instrument could not be disconnected.", failure, cleanupFailure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private sealed class DeviceLease : IDeviceLease
    {
        private readonly IInstrument inner;
        private readonly SemaphoreSlim deviceLock;
        private readonly ILoggerFactory loggerFactory;
        private int disposed;

        internal DeviceLease(string deviceKey, IInstrument inner, IInstrument instrument,
            SemaphoreSlim deviceLock, ILoggerFactory loggerFactory)
        {
            DeviceKey = deviceKey;
            this.inner = inner;
            Instrument = instrument;
            this.deviceLock = deviceLock;
            this.loggerFactory = loggerFactory;
        }

        public string DeviceKey { get; }
        public IInstrument Instrument { get; }

        public T GetCapability<T>() where T : class
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var logger = loggerFactory.CreateLogger<DeviceTraceInstrument>();
            object? wrapper = typeof(T) == typeof(ISwitchMultimeter) && inner is ISwitchMultimeter switchMultimeter
                ? new TracedSwitchMultimeter(Instrument, switchMultimeter, logger)
                : typeof(T) == typeof(IPowerSupply) && inner is IPowerSupply powerSupply
                    ? new TracedPowerSupply(Instrument, powerSupply, logger)
                    : typeof(T) == typeof(IProgrammer) && inner is IProgrammer programmer
                        ? new TracedProgrammer(Instrument, programmer, logger)
                        : typeof(T) == typeof(IBarcodeScanner) && inner is IBarcodeScanner scanner
                            ? new TracedBarcodeScanner(Instrument, scanner, logger)
                            : typeof(T) == typeof(ITestFixture) && inner is ITestFixture fixture
                                ? new TracedTestFixture(Instrument, fixture, logger)
                                : null;
            return wrapper as T ?? throw new NotSupportedException(
                $"Device '{DeviceKey}' does not provide capability '{typeof(T).Name}'.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try
            {
                await Instrument.DisconnectAsync(CancellationToken.None);
            }
            finally
            {
                deviceLock.Release();
            }
        }
    }
}
