using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Rohde &amp; Schwarz HMP2020/2030/4030/4040 over raw TCP or VISA.
/// Commands follow the R&amp;S HMP Series User Manual 1178.6833.02 edition 05, chapter 8.
/// </summary>
internal sealed class HmpScpi : IPowerSupply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly HmpOptions options;
    private readonly VisaSessionFactory visaSessionFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private IScpiTransport? connection;

    public HmpScpi(DriverConfiguration configuration, VisaSessionFactory visaSessionFactory)
    {
        this.visaSessionFactory = visaSessionFactory;
        options = ParseOptions(configuration);
        Profile = EmbeddedProfiles.Load(options.Model + ".yaml");
    }

    public DeviceProfile Profile { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connection is not null) throw new InvalidOperationException("The HMP instrument is already connected.");
            var candidate = options.Endpoint.CreateTransport(options.TimeoutMilliseconds, visaSessionFactory);
            try
            {
                await candidate.ConnectAsync(cancellationToken);
                var identity = await candidate.QueryAsync("*IDN?", cancellationToken);
                EnsureConfiguredModel(identity);
                await candidate.WriteAsync("*CLS", cancellationToken);
                connection = candidate;
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
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            return await QueryAsync("*IDN?", cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var response = await QueryAsync("*TST?", cancellationToken);
            if (!int.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorCode))
                throw new ScpiProtocolException($"The HMP self-test returned a non-integer response '{response}'.");
            return new SelfCheckResult(errorCode == 0, errorCode == 0
                ? $"{options.Model} self-test passed."
                : $"{options.Model} self-test returned error code {errorCode}.");
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Switches all outputs off together (OUTP:GEN OFF), then checks every output individually:
    /// an output that does not report OFF (ON or an unreadable answer) is switched off with OUTP OFF
    /// and checked again. All outputs are handled before it throws for outputs not confirmed OFF.
    /// </summary>
    public async Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connection is null) throw new InvalidOperationException("Connect the HMP instrument first.");
            await connection.ReopenIfClosedAsync(cancellationToken);
            await WriteAsync("OUTP:GEN OFF", cancellationToken);
            var notConfirmedOff = new List<int>();
            foreach (var channel in Profile.Channels.OrderBy(item => item.Number))
            {
                await WriteAsync(FormattableString.Invariant($"INST OUT{channel.Number}"), cancellationToken);
                if (ParseOutputState(await QueryAsync("OUTP?", cancellationToken)) == false)
                    continue;
                await WriteAsync("OUTP OFF", cancellationToken);
                if (ParseOutputState(await QueryAsync("OUTP?", cancellationToken)) != false)
                    notConfirmedOff.Add(channel.Number);
            }
            if (notConfirmedOff.Count != 0)
                throw new ScpiProtocolException(
                    $"{options.Model} at {options.Endpoint}: output(s) {string.Join(", ", notConfirmedOff)} not confirmed OFF after OUTP OFF.");
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task SetOutputAsync(int channel, double voltage, double currentLimit, bool enabled,
        CancellationToken cancellationToken = default)
    {
        PowerOutputLimits.Validate(Profile, channel, voltage, currentLimit);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            // Switching off happens first, so the DUT never sees the new values on a still active output.
            var commands = new List<string> { FormattableString.Invariant($"INST OUT{channel}") };
            if (!enabled) commands.Add("OUTP OFF");
            commands.Add(FormattableString.Invariant($"VOLT {voltage:0.000}"));
            commands.Add(FormattableString.Invariant($"CURR {currentLimit:0.0000}"));
            if (enabled) commands.Add("OUTP ON");
            foreach (var command in commands)
                await WriteAsync(command, cancellationToken);
            await connection!.ThrowIfInstrumentErrorsAsync(options.Model, string.Join("; ", commands), cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<(double Voltage, double CurrentLimit, bool Enabled)> GetOutputAsync(int channel,
        CancellationToken cancellationToken = default)
    {
        _ = Profile.GetChannel(channel);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(FormattableString.Invariant($"INST OUT{channel}"), cancellationToken);
            var voltage = ParseNumber(await QueryAsync("VOLT?", cancellationToken), "VOLT?");
            var currentLimit = ParseNumber(await QueryAsync("CURR?", cancellationToken), "CURR?");
            var state = await QueryAsync("OUTP?", cancellationToken);
            var enabled = ParseOutputState(state)
                ?? throw new ScpiProtocolException($"The HMP returned an invalid output state '{state}'.");
            return (voltage, currentLimit, enabled);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>Answer to OUTP? (manual: 1 = on, 0 = off); null when the answer is not recognised.</summary>
    private static bool? ParseOutputState(string state) => state switch
    {
        "1" or "ON" => true,
        "0" or "OFF" => false,
        _ => null
    };

    private void EnsureConfiguredModel(string identity)
    {
        var fields = identity.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length < 2 || !StringComparer.OrdinalIgnoreCase.Equals(fields[1], options.Model))
            throw new ScpiProtocolException(
                $"The instrument at {options.Endpoint} identifies as '{identity}', but model {options.Model} is configured.");
    }

    private static double ParseNumber(string response, string query)
    {
        if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ScpiProtocolException($"The HMP returned an invalid response '{response}' to {query}.");
        return value;
    }

    private async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        if (connection is null) throw new InvalidOperationException("Connect the HMP instrument first.");
        await connection.WriteAsync(command, cancellationToken);
    }

    private async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        if (connection is null) throw new InvalidOperationException("Connect the HMP instrument first.");
        return await connection.QueryAsync(command, cancellationToken);
    }

    private static HmpOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<HmpOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("HMP connection parameters must be a JSON object.");
        var model = HmpModels.Parse(input.Model);
        var endpoint = ScpiEndpoint.Parse("HMP", input.Host, input.Port, input.Resource);
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("HMP timeoutMilliseconds must be between 1 and 120000.");
        return new HmpOptions(endpoint, model, input.TimeoutMilliseconds);
    }

    private sealed class HmpOptionsInput
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5025;
        public string? Resource { get; init; }
        public string Model { get; init; } = "";
        public int TimeoutMilliseconds { get; init; } = 5000;
    }

    private sealed record HmpOptions(ScpiEndpoint Endpoint, string Model, int TimeoutMilliseconds);
}

internal static class HmpModels
{
    // Models listed in the R&S HMP data sheet 5215.4981.32 v02.01. Each has an embedded profile <model>.yaml.
    public static readonly string[] Supported = ["HMP2020", "HMP2030", "HMP4030", "HMP4040"];

    public static string Parse(string? model) =>
        Supported.SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item, model))
        ?? throw new JsonException(
            $"HMP model '{model}' is not supported. Use one of {string.Join(", ", Supported)} (see the *IDN? response or the type plate).");
}

public static class HmpScpiRegistration
{
    public static DriverRegistry AddHmpScpi(this DriverRegistry registry) =>
        registry.AddHmpScpi(IviVisaMessageSession.Open);

    internal static DriverRegistry AddHmpScpi(this DriverRegistry registry, VisaSessionFactory visaSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(visaSessionFactory);
        registry.Register("HmpScpi", configuration => new HmpScpi(configuration, visaSessionFactory));
        return registry;
    }
}
