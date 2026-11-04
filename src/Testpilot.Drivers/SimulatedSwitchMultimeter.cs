using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Simulated switch/multimeter. Connection options: <c>channels</c>, <c>readings</c> (fixed value per
/// "channel|function") and <c>sequences</c> (values returned one after another per "channel|function"; the last value
/// repeats, e.g. a discharging capacitor <c>["101|VOLT:DC": [0.5, 0.2, 0.05]]</c>).
/// </summary>
internal sealed class SimulatedSwitchMultimeter : ISwitchMultimeter
{
    private readonly IReadOnlyDictionary<string, double> readings;
    private readonly IReadOnlyDictionary<string, double[]> sequences;
    private readonly Dictionary<string, int> sequencePositions = new(StringComparer.Ordinal);
    private readonly HashSet<int> closedRelays = [];
    private readonly Dictionary<int, MeasurementConfiguration> configurations = [];
    private bool connected;

    public SimulatedSwitchMultimeter(DriverConfiguration driverConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driverConfiguration);
        var options = JsonSerializer.Deserialize<SimulationOptions>(driverConfiguration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("Simulation options must be a JSON object.");
        if (options.Readings is null)
            throw new JsonException("Simulated readings must be a JSON object.");
        var channels = new HashSet<int>(options.Channels ?? [1]);
        if (options.Channels is not null && channels.Count != options.Channels.Length)
            throw new JsonException("Simulated channel numbers must be unique.");
        var configuredReadings = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in options.Readings)
        {
            ValidateKey(key);
            if (!double.IsFinite(value))
                throw new JsonException($"Simulated reading '{key}' must be finite.");
            configuredReadings.Add(key, value);
        }
        var configuredSequences = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (key, values) in options.Sequences ?? [])
        {
            ValidateKey(key);
            if (values is null || values.Length == 0 || values.Any(value => !double.IsFinite(value)))
                throw new JsonException($"Simulated sequence '{key}' must contain at least one finite value.");
            configuredSequences.Add(key, values);
        }
        Profile = SimulatedProfiles.Create("simulated-dmm", "Simulated switch multimeter",
            DeviceCapability.SwitchMultimeter, channels);
        foreach (var key in configuredReadings.Keys.Concat(configuredSequences.Keys))
        {
            var separator = key.IndexOf('|');
            var channelNumber = int.Parse(key.AsSpan(0, separator), System.Globalization.CultureInfo.InvariantCulture);
            if (!Profile.Channels.Any(channel => channel.Number == channelNumber
                && channel.SupportedFunctions.Contains(key[(separator + 1)..])))
                throw new JsonException($"Simulated reading key '{key}' must name a configured channel and supported function.");
        }
        readings = configuredReadings;
        sequences = configuredSequences;
    }

    private static void ValidateKey(string key)
    {
        var separator = key.IndexOf('|');
        if (separator <= 0 || separator == key.Length - 1
            || !int.TryParse(key.AsSpan(0, separator), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var channelNumber) || channelNumber <= 0)
            throw new JsonException($"Simulated reading key '{key}' must contain a positive channel and function.");
    }

    public DeviceProfile Profile { get; }

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
        configurations.Clear();
        closedRelays.Clear();
        sequencePositions.Clear();
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult("TESTPILOT,SIMULATED-DMM,1.0");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, "Simulated self-check passed."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        closedRelays.Clear();
        configurations.Clear();
        return Task.CompletedTask;
    }

    public Task ConfigureMeasurementAsync(MeasurementConfiguration newConfiguration, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        ArgumentNullException.ThrowIfNull(newConfiguration);
        newConfiguration.Validate(Profile);
        configurations[newConfiguration.Channel] = newConfiguration;
        return Task.CompletedTask;
    }

    public Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        if (!configurations.TryGetValue(channel, out var configuration))
            throw new InvalidOperationException($"Configure channel {channel} before measuring it.");
        var key = $"{channel}|{configuration.Function}";
        if (sequences.TryGetValue(key, out var sequence))
        {
            var position = sequencePositions.GetValueOrDefault(key);
            sequencePositions[key] = position + 1;
            return Task.FromResult(sequence[Math.Min(position, sequence.Length - 1)]);
        }
        return Task.FromResult(readings.GetValueOrDefault(key));
    }

    public Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        _ = Profile.GetChannel(channel);
        if (closed) closedRelays.Add(channel);
        else closedRelays.Remove(channel);
        return Task.CompletedTask;
    }

    public bool IsRelayClosed(int channel) => closedRelays.Contains(channel);

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }

    private sealed class SimulationOptions
    {
        public int[]? Channels { get; init; }
        public Dictionary<string, double>? Readings { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, double[]>? Sequences { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };
}
