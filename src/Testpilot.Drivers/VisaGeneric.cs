using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Any message-based VISA instrument without a dedicated driver. It only identifies the instrument
/// (*IDN?) and sends the configured safe-state commands; it declares no capabilities and no channels.
/// </summary>
internal sealed class VisaGeneric : IInstrument
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly VisaGenericOptions options;
    private readonly VisaSessionFactory visaSessionFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private IScpiTransport? connection;

    public VisaGeneric(DriverConfiguration configuration, VisaSessionFactory visaSessionFactory)
    {
        this.visaSessionFactory = visaSessionFactory;
        options = ParseOptions(configuration);
    }

    public DeviceProfile Profile { get; } = new(
        "visa-generic", "Generic VISA instrument", null, "not applicable",
        new HashSet<DeviceCapability>(),
        new ChannelStateProfile(new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), true),
        [], [], [DeviceProfile.DataSheetTodo + " - generic instrument: channels and capabilities are unknown"]);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connection is not null) throw new InvalidOperationException("The VISA instrument is already connected.");
            var candidate = new VisaScpiTransport(options.Resource, options.TimeoutMilliseconds, visaSessionFactory);
            try
            {
                await candidate.ConnectAsync(cancellationToken);
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
            return await ActiveConnection().QueryAsync("*IDN?", cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Deliberately does not send *TST?: on many instruments it resets outputs or relays and can take minutes.
    /// </summary>
    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        var identity = await IdentifyAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(identity)
            ? new SelfCheckResult(false, $"{options.Resource} returned an empty *IDN? response.")
            : new SelfCheckResult(true, $"{options.Resource} answered *IDN?; no instrument-specific self-test is configured.");
    }

    public async Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var connection = ActiveConnection();
            await connection.ReopenIfClosedAsync(cancellationToken);
            // Every safe-state command is attempted, even if an earlier one failed.
            var errors = new List<Exception>();
            foreach (var command in options.SafeStateCommands)
            {
                try
                {
                    await connection.ReopenIfClosedAsync(cancellationToken);
                    await connection.WriteAsync(command, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add(exception);
                }
            }
            if (errors.Count != 0)
                throw new AggregateException($"{options.Resource}: {errors.Count} safe-state command(s) failed.", errors);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private IScpiTransport ActiveConnection() =>
        connection ?? throw new InvalidOperationException("Connect the VISA instrument first.");

    private static VisaGenericOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<VisaGenericOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("VisaGeneric connection parameters must be a JSON object.");
        var endpoint = ScpiEndpoint.Parse("VisaGeneric", null, 0, input.Resource);
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("VisaGeneric timeoutMilliseconds must be between 1 and 120000.");
        var commands = input.SafeStateCommands ?? [];
        foreach (var command in commands)
            ScpiCommandText.Validate(command, "VisaGeneric");
        return new VisaGenericOptions(endpoint.Resource!, input.TimeoutMilliseconds, commands);
    }

    private sealed class VisaGenericOptionsInput
    {
        public string? Resource { get; init; }
        public int TimeoutMilliseconds { get; init; } = 5000;
        public string[]? SafeStateCommands { get; init; }
    }

    private sealed record VisaGenericOptions(string Resource, int TimeoutMilliseconds, IReadOnlyList<string> SafeStateCommands);
}

public static class VisaGenericRegistration
{
    public static DriverRegistry AddVisaGeneric(this DriverRegistry registry) =>
        registry.AddVisaGeneric(IviVisaMessageSession.Open);

    internal static DriverRegistry AddVisaGeneric(this DriverRegistry registry, VisaSessionFactory visaSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(visaSessionFactory);
        registry.Register("VisaGeneric", configuration => new VisaGeneric(configuration, visaSessionFactory));
        return registry;
    }
}
