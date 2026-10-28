using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

internal abstract class DeviceTraceDecoratorBase(IInstrument inner, ILogger logger) : IInstrument
{
    protected IInstrument Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));
    public DeviceProfile Profile => Inner.Profile;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("CONNECT", () => Inner.ConnectAsync(cancellationToken));

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("DISCONNECT", () => Inner.DisconnectAsync(cancellationToken));

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("IDENTIFY", () => Inner.IdentifyAsync(cancellationToken));

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("SELF-CHECK", () => Inner.SelfCheckAsync(cancellationToken), result =>
            $"{result.Passed}: {result.Message}");

    public Task SafeStateAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("SAFE-STATE", () => Inner.SafeStateAsync(cancellationToken));

    protected Task TraceAsync(string command, Func<Task> action) =>
        TraceAsync(command, async () =>
        {
            await action();
            return "OK";
        });

    protected async Task<T> TraceAsync<T>(string command, Func<Task<T>> action, Func<T, string>? formatResponse = null,
        LogLevel level = LogLevel.Information)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await action();
            Logger.Log(level, "Device command {Command} returned {Response} in {DurationMs} ms",
                command, formatResponse?.Invoke(response) ?? Convert.ToString(response, CultureInfo.InvariantCulture), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Device command {Command} failed after {DurationMs} ms",
                command, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}

internal sealed class DeviceTraceInstrument(IInstrument inner, ILogger<DeviceTraceInstrument> logger)
    : DeviceTraceDecoratorBase(inner, logger);

internal sealed class TracedSwitchMultimeter(IInstrument inner, ISwitchMultimeter capability, ILogger logger)
    : DeviceTraceDecoratorBase(inner, logger), ISwitchMultimeter
{
    public Task ConfigureMeasurementAsync(MeasurementConfiguration configuration, CancellationToken cancellationToken = default) =>
        TraceAsync(FormattableString.Invariant($"MEASURE:CONFIGURE channel={configuration.Channel} function={configuration.Function} range={configuration.Range} nplc={configuration.Nplc}"),
            () => capability.ConfigureMeasurementAsync(configuration, cancellationToken));

    public Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default) =>
        TraceAsync($"MEASURE? channel={channel}", () => capability.MeasureAsync(channel, cancellationToken));

    public Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default) =>
        TraceAsync($"RELAY channel={channel} closed={closed}", () => capability.SetRelayAsync(channel, closed, cancellationToken));
}

internal sealed class TracedPowerSupply(IInstrument inner, IPowerSupply capability, ILogger logger)
    : DeviceTraceDecoratorBase(inner, logger), IPowerSupply
{
    public Task SetOutputAsync(int channel, double voltage, double currentLimit, bool enabled, CancellationToken cancellationToken = default) =>
        TraceAsync(FormattableString.Invariant($"OUTPUT channel={channel} voltage={voltage} currentLimit={currentLimit} enabled={enabled}"),
            () => capability.SetOutputAsync(channel, voltage, currentLimit, enabled, cancellationToken));

    public Task<(double Voltage, double CurrentLimit, bool Enabled)> GetOutputAsync(int channel, CancellationToken cancellationToken = default) =>
        TraceAsync(FormattableString.Invariant($"OUTPUT? channel={channel}"), () => capability.GetOutputAsync(channel, cancellationToken),
            response => FormattableString.Invariant($"voltage={response.Voltage} currentLimit={response.CurrentLimit} enabled={response.Enabled}"));
}

internal sealed class TracedProgrammer(IInstrument inner, IProgrammer capability, ILogger logger)
    : DeviceTraceDecoratorBase(inner, logger), IProgrammer
{
    public Task ProgramAsync(string imagePath, CancellationToken cancellationToken = default) =>
        TraceAsync($"PROGRAM file={imagePath}", () => capability.ProgramAsync(imagePath, cancellationToken));
}

internal sealed class TracedBarcodeScanner(IInstrument inner, IBarcodeScanner capability, ILogger logger)
    : DeviceTraceDecoratorBase(inner, logger), IBarcodeScanner
{
    public Task<string> ScanAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("SCAN?", () => capability.ScanAsync(cancellationToken));
}

internal sealed class TracedTestFixture(IInstrument inner, ITestFixture capability, ILogger logger)
    : DeviceTraceDecoratorBase(inner, logger), ITestFixture
{
    // The operator application polls the adapter several times per second; these queries are only logged at Debug level.
    public Task<bool> IsClosedAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("FIXTURE-CLOSED?", () => capability.IsClosedAsync(cancellationToken), level: LogLevel.Debug);

    public Task LockAsync(CancellationToken cancellationToken = default) =>
        TraceAsync("FIXTURE-LOCK", () => capability.LockAsync(cancellationToken));

    public Task ReleaseAsync(FixtureLamp lamp, CancellationToken cancellationToken = default) =>
        TraceAsync($"FIXTURE-RELEASE lamp={lamp}", () => capability.ReleaseAsync(lamp, cancellationToken));
}
