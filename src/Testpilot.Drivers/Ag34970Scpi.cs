using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Keysight/Agilent 34970A data acquisition unit over VISA (GPIB/RS-232, or LAN through a gateway) or raw TCP.
/// Channel numbers are slot * 100 + module channel (for example 103). Like the 34980A, the instrument keeps the
/// measurement configuration per channel; a channel is read with ROUT:SCAN + READ?.
/// <para>
/// Differences to <see cref="Ag34980Scpi"/>: no *RST by default and no *TST?, because both reset the 34907A digital
/// outputs (in PS05 port 302 holds the adapter closed, see <see cref="Ps05Fixture"/>). The safe state opens only the
/// relay channels of the profile; the digital I/O, totalizer and DAC channels are never touched.
/// </para>
/// </summary>
internal sealed class Ag34970Scpi : ISwitchMultimeter
{
    private const string Instrument = "34970A";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private static readonly Regex FunctionPattern = new("^(VOLT:DC|VOLT:AC|CURR:DC|CURR:AC|RES|FRES)$", RegexOptions.CultureInvariant);
    private readonly Ag34970Options options;
    private readonly VisaSessionFactory visaSessionFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly Dictionary<int, MeasurementConfiguration> configuredChannels = [];
    private IScpiTransport? connection;
    private bool connected;

    public Ag34970Scpi(DriverConfiguration configuration, VisaSessionFactory visaSessionFactory)
    {
        this.visaSessionFactory = visaSessionFactory;
        options = ParseOptions(configuration);
        Profile = MainframeProfileSource.Load("Ag34970Scpi", DeviceCatalog.Ag34970A, options.ProfilePath, options.Slots);
    }

    public DeviceProfile Profile { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connected) throw new InvalidOperationException("The 34970A is already connected.");
            var candidate = options.Endpoint.CreateTransport(options.TimeoutMilliseconds, visaSessionFactory);
            try
            {
                await candidate.ConnectAsync(cancellationToken);
                var identity = await candidate.QueryAsync("*IDN?", cancellationToken);
                if (string.IsNullOrWhiteSpace(identity))
                    throw new ScpiProtocolException("The 34970A returned an empty identity response.");
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

    /// <summary>Identity and error queue only; *TST? would reset the digital outputs of the 34907A.</summary>
    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var identity = await QueryAsync("*IDN?", cancellationToken);
            if (string.IsNullOrWhiteSpace(identity))
                return new SelfCheckResult(false, "34970A returned an empty identity response.");
            try
            {
                await connection!.ThrowIfInstrumentErrorsAsync(Instrument, "*IDN?", cancellationToken);
            }
            catch (ScpiProtocolException exception)
            {
                return new SelfCheckResult(false, exception.Message);
            }
            return new SelfCheckResult(true, $"34970A responds ({identity}); error queue empty.");
        }
        finally { operationLock.Release(); }
    }

    public async Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!connected || connection is null)
                throw new InvalidOperationException("Connect the 34970A first.");
            await connection.ReopenIfClosedAsync(cancellationToken);
            configuredChannels.Clear();
            var relayChannels = Profile.Channels.Where(channel => channel.CanSwitch).Select(channel => channel.Number).ToList();
            if (relayChannels.Count == 0)
                return;
            var command = $"ROUT:OPEN (@{string.Join(",", relayChannels.Select(number => number.ToString(CultureInfo.InvariantCulture)))})";
            await connection.WriteAsync(command, cancellationToken);
            await connection.ThrowIfInstrumentErrorsAsync(Instrument, command, cancellationToken);
        }
        finally { operationLock.Release(); }
    }

    public async Task ConfigureMeasurementAsync(MeasurementConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate(Profile);
        if (!FunctionPattern.IsMatch(configuration.Function))
            throw new ArgumentException($"Unsupported 34970A measurement function '{configuration.Function}'.", nameof(configuration));
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
            await connection!.ThrowIfInstrumentErrorsAsync(Instrument, string.Join("; ", commands), cancellationToken);
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
                throw new ScpiProtocolException($"The 34970A returned an invalid measurement '{response}'.");
            return value;
        }
        finally { operationLock.Release(); }
    }

    public async Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default)
    {
        var profileChannel = Profile.GetChannel(channel);
        if (!profileChannel.CanSwitch)
            throw new ArgumentException($"34970A channel {channel} is not a relay channel ({profileChannel.Type}).", nameof(channel));
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var command = FormattableString.Invariant($"ROUT:{(closed ? "CLOS" : "OPEN")} (@{channel})");
            await WriteAsync(command, cancellationToken);
            await connection!.ThrowIfInstrumentErrorsAsync(Instrument, command, cancellationToken);
        }
        finally { operationLock.Release(); }
    }

    private async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        if (!connected || connection is null)
            throw new InvalidOperationException("Connect the 34970A first.");
        await connection.WriteAsync(command, cancellationToken);
    }

    private async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        if (!connected || connection is null)
            throw new InvalidOperationException("Connect the 34970A first.");
        return await connection.QueryAsync(command, cancellationToken);
    }

    private static Ag34970Options ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<Ag34970OptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("34970A connection parameters must be a JSON object.");
        var endpoint = ScpiEndpoint.Parse(Instrument, input.Host, input.Port, input.Resource);
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("34970A timeoutMilliseconds must be between 1 and 120000.");
        return new Ag34970Options(endpoint, input.ProfilePath, input.Slots, input.TimeoutMilliseconds, input.ResetOnConnect);
    }

    private sealed class Ag34970OptionsInput
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5025;
        public string? Resource { get; init; }
        public string? ProfilePath { get; init; }
        public Dictionary<string, string>? Slots { get; init; }
        public int TimeoutMilliseconds { get; init; } = 5000;
        public bool ResetOnConnect { get; init; }
    }

    private sealed record Ag34970Options(ScpiEndpoint Endpoint, string? ProfilePath, IReadOnlyDictionary<string, string>? Slots,
        int TimeoutMilliseconds, bool ResetOnConnect);
}

public static class Ag34970ScpiRegistration
{
    public static DriverRegistry AddAg34970Scpi(this DriverRegistry registry) =>
        registry.AddAg34970Scpi(IviVisaMessageSession.Open);

    internal static DriverRegistry AddAg34970Scpi(this DriverRegistry registry, VisaSessionFactory visaSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(visaSessionFactory);
        registry.Register("Ag34970Scpi", configuration => new Ag34970Scpi(configuration, visaSessionFactory));
        return registry;
    }
}
