using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Keysight/Agilent 34980A over raw TCP or VISA. The instrument keeps the measurement configuration per
/// channel, so several channels can be configured up front (Init) and measured later with ROUT:SCAN + READ?.
/// </summary>
internal sealed class Ag34980Scpi : ISwitchMultimeter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private static readonly Regex FunctionPattern = new("^(VOLT:DC|VOLT:AC|CURR:DC|CURR:AC|RES|FRES)$", RegexOptions.CultureInvariant);
    private readonly Ag34980Options options;
    private readonly VisaSessionFactory visaSessionFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private IScpiTransport? connection;
    private readonly Dictionary<int, MeasurementConfiguration> configuredChannels = [];
    private bool connected;

    public Ag34980Scpi(DriverConfiguration configuration, VisaSessionFactory visaSessionFactory)
    {
        this.visaSessionFactory = visaSessionFactory;
        options = ParseOptions(configuration);
        var profilePath = Path.GetFullPath(options.ProfilePath);
        if (!File.Exists(profilePath)) throw new FileNotFoundException("The configured 34980A module profile was not found.", profilePath);
        Profile = DeviceProfileLoader.Load(File.ReadAllText(profilePath));
        if (!Profile.Capabilities.Contains(DeviceCapability.SwitchMultimeter))
            throw new InvalidDataException("The configured 34980A profile does not declare the SwitchMultimeter capability.");
        if (!Profile.Channels.Any()) throw new InvalidDataException("The configured 34980A module profile must define verified channels.");
        if (Profile.SpecialCases.Concat(Profile.Todos).Any(note => note.Contains(DeviceProfile.DataSheetTodo, StringComparison.Ordinal)))
            throw new InvalidDataException("The configured 34980A module profile still contains unresolved data-sheet TODOs.");
    }

    public DeviceProfile Profile { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connected) throw new InvalidOperationException("The 34980A is already connected.");
            var candidate = options.Endpoint.CreateTransport(options.TimeoutMilliseconds, visaSessionFactory);
            try
            {
                var identity = await ConnectAndIdentifyAsync(candidate, cancellationToken);
                if (string.IsNullOrWhiteSpace(identity))
                    throw new ScpiProtocolException("The 34980A returned an empty identity response.");
                // Start every session from a defined state: no leftover scan list, reading format or errors.
                if (options.ResetOnConnect)
                    await candidate.WriteAsync("*RST", cancellationToken);
                await candidate.WriteAsync("*CLS", cancellationToken);
                connection = candidate;
                connected = true;
                configuredChannels.Clear();
            }
            catch
            {
                await candidate.DisposeAsync();
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connection is not null) await connection.DisposeAsync();
            connection = null;
            connected = false;
            configuredChannels.Clear();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try { return await QueryAsync("*IDN?", cancellationToken); }
        finally { operationLock.Release(); }
    }

    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var response = await QueryAsync("*TST?", cancellationToken);
            if (!int.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorCode))
                throw new ScpiProtocolException($"The 34980A self-test returned a non-integer response '{response}'.");
            return new SelfCheckResult(errorCode == 0, errorCode == 0
                ? "34980A self-test passed."
                : $"34980A self-test returned error code {errorCode}.");
        }
        finally { operationLock.Release(); }
    }

    public async Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!connected || connection is null)
                throw new InvalidOperationException("Connect the 34980A first.");
            await connection.ReopenIfClosedAsync(cancellationToken);
            configuredChannels.Clear();
            await WriteAsync("ROUT:OPEN:ALL", cancellationToken);
            await connection.ThrowIfInstrumentErrorsAsync("34980A", "ROUT:OPEN:ALL", cancellationToken);
        }
        finally { operationLock.Release(); }
    }

    public async Task ConfigureMeasurementAsync(MeasurementConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate(Profile);
        if (!FunctionPattern.IsMatch(configuration.Function))
            throw new ArgumentException($"Unsupported 34980A measurement function '{configuration.Function}'.", nameof(configuration));
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            configuredChannels.Remove(configuration.Channel);
            var range = configuration.Range?.ToString("G", CultureInfo.InvariantCulture) ?? "AUTO";
            var commands = new List<string>
            {
                FormattableString.Invariant($"CONF:{configuration.Function} {range},(@{configuration.Channel})")
            };
            if (configuration.Nplc.HasValue)
                commands.Add(FormattableString.Invariant(
                    $"SENS:{configuration.Function}:NPLC {configuration.Nplc.Value:G},(@{configuration.Channel})"));
            foreach (var command in commands)
                await WriteAsync(command, cancellationToken);
            await connection!.ThrowIfInstrumentErrorsAsync("34980A", string.Join("; ", commands), cancellationToken);
            configuredChannels[configuration.Channel] = configuration;
        }
        finally { operationLock.Release(); }
    }

    public async Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!configuredChannels.ContainsKey(channel))
                throw new InvalidOperationException($"Configure channel {channel} before measuring it.");
            // CONF replaces the scan list, so the channel to read is selected explicitly.
            await WriteAsync(FormattableString.Invariant($"ROUT:SCAN (@{channel})"), cancellationToken);
            var response = await QueryAsync("READ?", cancellationToken);
            if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
                throw new ScpiProtocolException($"The 34980A returned an invalid measurement '{response}'.");
            return value;
        }
        finally { operationLock.Release(); }
    }

    public async Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default)
    {
        _ = Profile.GetChannel(channel);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var command = FormattableString.Invariant($"ROUT:{(closed ? "CLOS" : "OPEN")} (@{channel})");
            await WriteAsync(command, cancellationToken);
            await connection!.ThrowIfInstrumentErrorsAsync("34980A", command, cancellationToken);
        }
        finally { operationLock.Release(); }
    }

    private async Task<string> ConnectAndIdentifyAsync(IScpiTransport candidate, CancellationToken cancellationToken)
    {
        await candidate.ConnectAsync(cancellationToken);
        return await candidate.QueryAsync("*IDN?", cancellationToken);
    }

    private async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        if (!connected || connection is null)
            throw new InvalidOperationException("Connect the 34980A first.");
        await connection.WriteAsync(command, cancellationToken);
    }

    private async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        if (!connected || connection is null)
            throw new InvalidOperationException("Connect the 34980A first.");
        return await connection.QueryAsync(command, cancellationToken);
    }

    private static Ag34980Options ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<Ag34980OptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("34980A connection parameters must be a JSON object.");
        if (string.IsNullOrWhiteSpace(input.ProfilePath))
            throw new JsonException("34980A profilePath is required.");
        var endpoint = ScpiEndpoint.Parse("34980A", input.Host, input.Port, input.Resource);
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("34980A timeoutMilliseconds must be between 1 and 120000.");
        return new Ag34980Options(endpoint, input.ProfilePath, input.TimeoutMilliseconds, input.ResetOnConnect);
    }

    private sealed class Ag34980OptionsInput
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5025;
        public string? Resource { get; init; }
        public string ProfilePath { get; init; } = "";
        public int TimeoutMilliseconds { get; init; } = 5000;
        public bool ResetOnConnect { get; init; } = true;
    }

    private sealed record Ag34980Options(ScpiEndpoint Endpoint, string ProfilePath, int TimeoutMilliseconds, bool ResetOnConnect);
}

public static class Ag34980ScpiRegistration
{
    public static DriverRegistry AddAg34980Scpi(this DriverRegistry registry) =>
        registry.AddAg34980Scpi(IviVisaMessageSession.Open);

    internal static DriverRegistry AddAg34980Scpi(this DriverRegistry registry, VisaSessionFactory visaSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(visaSessionFactory);
        registry.Register("Ag34980Scpi", configuration => new Ag34980Scpi(configuration, visaSessionFactory));
        return registry;
    }
}
