using System.Text.Json;

namespace Testpilot.Drivers.Abstractions;

public sealed record SelfCheckResult(bool Passed, string Message);

public sealed record MeasurementConfiguration(int Channel, string Function, double? Range = null, double? Nplc = null)
{
    public void Validate(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var channel = profile.GetChannel(Channel);
        if (!channel.SupportedFunctions.Contains(Function))
            throw new ArgumentException($"Function '{Function}' is not supported on channel {Channel}.");
        if (Range.HasValue && (!double.IsFinite(Range.Value) || Range.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(Range));
        if (Nplc.HasValue && (!double.IsFinite(Nplc.Value) || Nplc.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(Nplc));
        if (Nplc.HasValue && !SupportsNplc(Function))
            throw new ArgumentException($"NPLC applies only to DC and resistance measurements, not to '{Function}'.");
        if (Range.HasValue && channel.ValueRanges.TryGetValue(Function, out var limits)
            && (Range.Value < limits.Minimum || Range.Value > limits.Maximum))
            throw new ArgumentOutOfRangeException(nameof(Range), "The requested range is outside the profile limits.");
    }

    /// <summary>AC functions use a bandwidth/filter setting instead of an integration time.</summary>
    public static bool SupportsNplc(string function) => function is "VOLT:DC" or "CURR:DC" or "RES" or "FRES";
}

public sealed class DriverConfiguration(string deviceKey, string driver, JsonElement connectionParameters)
{
    public string DeviceKey { get; } = Required(deviceKey, nameof(deviceKey));
    public string Driver { get; } = Required(driver, nameof(driver));
    public JsonElement ConnectionParameters { get; } = CloneObject(connectionParameters);

    public static DriverConfiguration Parse(string deviceKey, string driver, string connectionParametersJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionParametersJson);
        using var document = JsonDocument.Parse(connectionParametersJson);
        return new(deviceKey, driver, document.RootElement);
    }

    private static JsonElement CloneObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Connection parameters must be a JSON object.", nameof(connectionParameters));
        return value.Clone();
    }

    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

public interface IInstrument
{
    DeviceProfile Profile { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<string> IdentifyAsync(CancellationToken cancellationToken = default);
    Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default);
    Task SafeStateAsync(CancellationToken cancellationToken = default);
}

public interface ISwitchMultimeter : IInstrument
{
    Task ConfigureMeasurementAsync(MeasurementConfiguration configuration, CancellationToken cancellationToken = default);
    Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default);
    Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default);
}

/// <summary>Power supply with one or more numbered outputs (1-based, as printed on the instrument).</summary>
public interface IPowerSupply : IInstrument
{
    Task SetOutputAsync(int channel, double voltage, double currentLimit, bool enabled, CancellationToken cancellationToken = default);
    Task<(double Voltage, double CurrentLimit, bool Enabled)> GetOutputAsync(int channel, CancellationToken cancellationToken = default);
}

/// <summary>Function names used in power-output channel profiles for the programmable voltage and current limit.</summary>
public static class PowerOutputLimits
{
    public const string Voltage = "VOLT";
    public const string CurrentLimit = "CURR";

    /// <summary>Checks a requested output against the profile. Channels and ranges are only enforced when the profile declares them.</summary>
    public static void Validate(DeviceProfile profile, int channel, double voltage, double currentLimit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel), "The output channel must be positive.");
        if (!double.IsFinite(voltage)) throw new ArgumentOutOfRangeException(nameof(voltage), "The voltage must be finite.");
        if (!double.IsFinite(currentLimit) || currentLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentLimit), "The current limit must be finite and positive.");
        if (profile.Channels.Count == 0)
            return;
        var output = profile.GetChannel(channel);
        if (output.ValueRanges.TryGetValue(Voltage, out var voltageRange)
            && (voltage < voltageRange.Minimum || voltage > voltageRange.Maximum))
            throw new ArgumentOutOfRangeException(nameof(voltage),
                FormattableString.Invariant($"{voltage} V is outside {voltageRange.Minimum}..{voltageRange.Maximum} V for output {channel} of '{profile.Model}'."));
        if (output.ValueRanges.TryGetValue(CurrentLimit, out var currentRange)
            && (currentLimit < currentRange.Minimum || currentLimit > currentRange.Maximum))
            throw new ArgumentOutOfRangeException(nameof(currentLimit),
                FormattableString.Invariant($"{currentLimit} A is outside {currentRange.Minimum}..{currentRange.Maximum} A for output {channel} of '{profile.Model}'."));
    }
}

public interface IProgrammer : IInstrument
{
    Task ProgramAsync(string imagePath, CancellationToken cancellationToken = default);
}

public interface IBarcodeScanner : IInstrument
{
    Task<string> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result lamp of the test adapter, shown when the adapter is released after a run.</summary>
public enum FixtureLamp
{
    None,
    Pass,
    Fail
}

/// <summary>
/// Test adapter (fixture) with a "closed" sensor and a hold-down that keeps it closed (self-hold),
/// for example a pneumatic hold-down. Used by the operator application to start a run automatically.
/// </summary>
public interface ITestFixture : IInstrument
{
    /// <summary>True when the adapter is closed (hold-down down).</summary>
    Task<bool> IsClosedAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps the closed adapter closed (self-hold) until <see cref="ReleaseAsync"/> is called.</summary>
    Task LockAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases the self-hold so that the adapter opens, and shows the result lamp.</summary>
    Task ReleaseAsync(FixtureLamp lamp, CancellationToken cancellationToken = default);
}

public interface IDeviceLease : IAsyncDisposable
{
    string DeviceKey { get; }
    IInstrument Instrument { get; }
    T GetCapability<T>() where T : class;
}

/// <summary>Provides one exclusive, connected instrument session per device key.</summary>
public interface IDriverHost
{
    ValueTask<IDeviceLease> AcquireAsync(DriverConfiguration configuration, CancellationToken cancellationToken = default);
}
