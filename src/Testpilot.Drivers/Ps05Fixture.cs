using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// PS05 test adapter control: the adapter switch and the hold-down/lamp outputs are wired to the
/// digital channels of an Agilent 34970A (usually GPIB through VISA, e.g. "GPIB0::9::INSTR").
/// Commands and byte values are taken unchanged from the legacy program
/// (Externes\Geräte\Ps05InternWrapper.cs and Ag34970AWrapperVISA.cs).
/// </summary>
internal sealed class Ps05Fixture : ITestFixture
{
    // Legacy Ag34970AWrapperVISA.ByteRead reads the input byte with this query; the instrument answers e.g. "+2.54000000E+02".
    internal const string ReadAdapterQuery = "MEAS:VOLT:DC? (@301)";
    internal const int LockByte = 250;
    internal const int OpenPassByte = 253;
    internal const int OpenFailByte = 254;
    internal const int InitialByte = 255;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly Ps05Options options;
    private readonly VisaSessionFactory visaSessionFactory;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private IScpiTransport? connection;

    public Ps05Fixture(DriverConfiguration configuration, VisaSessionFactory visaSessionFactory)
    {
        this.visaSessionFactory = visaSessionFactory;
        options = ParseOptions(configuration);
    }

    public DeviceProfile Profile { get; } = EmbeddedProfiles.Load("Ps05Fixture.yaml");

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connection is not null) throw new InvalidOperationException("The PS05 adapter control is already connected.");
            var candidate = options.Endpoint.CreateTransport(options.TimeoutMilliseconds, visaSessionFactory);
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

    /// <summary>Only checks that the 34970A answers; *TST? is not sent because it resets the outputs.</summary>
    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        var identity = await IdentifyAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(identity)
            ? new SelfCheckResult(false, $"{options.Endpoint} returned an empty *IDN? response.")
            : new SelfCheckResult(true, $"{options.Endpoint} answered *IDN?.");
    }

    /// <summary>Writes the initial byte of the legacy Initialize (255): self-hold off, no lamp.</summary>
    public Task SafeStateAsync(CancellationToken cancellationToken = default) => WriteByteAsync(InitialByte, cancellationToken);

    public async Task<bool> IsClosedAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var response = await ActiveConnection().QueryAsync(ReadAdapterQuery, cancellationToken);
            if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new ScpiProtocolException($"The 34970A returned an invalid adapter state '{response}' to {ReadAdapterQuery}.");
            // Legacy Ps05InternWrapper.AdapterState: "+2.54000000E+02" or "+2.55000000E+02" means closed, anything else open.
            return value is 254 or 255;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public Task LockAsync(CancellationToken cancellationToken = default) => WriteByteAsync(LockByte, cancellationToken);

    public Task ReleaseAsync(FixtureLamp lamp, CancellationToken cancellationToken = default) => WriteByteAsync(lamp switch
    {
        FixtureLamp.Pass => OpenPassByte,
        FixtureLamp.Fail => OpenFailByte,
        _ => InitialByte
    }, cancellationToken);

    private async Task WriteByteAsync(int value, CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var connection = ActiveConnection();
            await connection.ReopenIfClosedAsync(cancellationToken);
            await connection.WriteAsync(FormattableString.Invariant($"SOUR:DIG:DATA:BYTE {value},(@302)"), cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private IScpiTransport ActiveConnection() =>
        connection ?? throw new InvalidOperationException("Connect the PS05 adapter control first.");

    private static Ps05Options ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<Ps05OptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("Ps05Fixture connection parameters must be a JSON object.");
        var endpoint = ScpiEndpoint.Parse("Ps05Fixture", input.Host, input.Port, input.Resource);
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("Ps05Fixture timeoutMilliseconds must be between 1 and 120000.");
        return new Ps05Options(endpoint, input.TimeoutMilliseconds);
    }

    private sealed class Ps05OptionsInput
    {
        public string? Host { get; init; }
        public int Port { get; init; } = 5025;
        public string? Resource { get; init; }
        // Legacy Ps05InternWrapper.Initialize default timeout.
        public int TimeoutMilliseconds { get; init; } = 4000;
    }

    private sealed record Ps05Options(ScpiEndpoint Endpoint, int TimeoutMilliseconds);
}

public static class Ps05FixtureRegistration
{
    public static DriverRegistry AddPs05Fixture(this DriverRegistry registry) =>
        registry.AddPs05Fixture(IviVisaMessageSession.Open);

    internal static DriverRegistry AddPs05Fixture(this DriverRegistry registry, VisaSessionFactory visaSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(visaSessionFactory);
        registry.Register("Ps05Fixture", configuration => new Ps05Fixture(configuration, visaSessionFactory));
        return registry;
    }
}
