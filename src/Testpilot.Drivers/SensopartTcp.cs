using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// SensoPart code reader over TCP. The protocol is taken from the legacy CVI driver
/// (SensoScanSerNr.c, ReadSensoPart):
/// <list type="bullet">
/// <item>two TCP connections to the sensor: trigger port (legacy 2006) and result port (legacy 2005),</item>
/// <item>trigger: the 3 ASCII bytes "TRG" without terminator on the trigger port,</item>
/// <item>result: one line on the result port, ended by '\n'; "CANNOTREAD" means nothing was decoded,</item>
/// <item>on CANNOTREAD or a wrong code the legacy code triggers again (10 attempts, 50 ms pause).</item>
/// </list>
/// Answers on the trigger port are not evaluated (the legacy code never reads them); they are discarded.
/// </summary>
internal sealed class SensopartTcp(DriverConfiguration configuration) : IBarcodeScanner
{
    private const string TriggerCommand = "TRG";
    private const string NoReadResult = "CANNOTREAD";
    private const int RetryDelayMilliseconds = 50;
    private const int MaximumResultBytes = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly SensopartOptions options = ParseOptions(configuration);
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private TcpClient? triggerClient;
    private TcpClient? resultClient;

    public DeviceProfile Profile { get; } = EmbeddedProfiles.Load("CodeReader.yaml");

    // TODO: JobswitchSensoPart (job selection) is declared in Seriennummer.c, but its implementation is not
    // in the legacy sources; add it once the command is known.
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (triggerClient is not null) throw new InvalidOperationException("The SensoPart sensor is already connected.");
            TcpClient? trigger = null;
            try
            {
                trigger = await OpenAsync(options.TriggerPort, cancellationToken);
                resultClient = await OpenAsync(options.ResultPort, cancellationToken);
                triggerClient = trigger;
            }
            catch
            {
                trigger?.Dispose();
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
            Close();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        // The legacy protocol has no identification command.
        return Task.FromResult($"SensoPart sensor {options.Host} (trigger port {options.TriggerPort}, result port {options.ResultPort})");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        return Task.FromResult(new SelfCheckResult(true,
            "Trigger and result connections are open (the legacy protocol has no self-test command)."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> ScanAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            var lastResult = "";
            for (var attempt = 1; attempt <= options.Attempts; attempt++)
            {
                if (attempt > 1) await Task.Delay(RetryDelayMilliseconds, cancellationToken);
                DiscardPendingData(triggerClient!);
                DiscardPendingData(resultClient!);
                await SendTriggerAsync(cancellationToken);
                lastResult = await ReadResultLineAsync(cancellationToken);
                if (lastResult != NoReadResult && SerialNumberCode.IsValid(lastResult))
                    return lastResult;
            }
            throw new BarcodeReadException(
                $"SensoPart {options.Host}: no valid serial number ({SerialNumberCode.Description}) after {options.Attempts} attempt(s); last result '{lastResult}'.");
        }
        catch (Exception exception) when (exception is TimeoutException or SocketException or OperationCanceledException
            || (exception is IOException && exception is not BarcodeReadException))
        {
            // A late result would be read as the answer to the next trigger, so the connection is closed.
            Close();
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<TcpClient> OpenAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.TimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(options.Host, port, linked.Token);
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Connecting to SensoPart {options.Host}:{port} timed out.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task SendTriggerAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.TimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var stream = triggerClient!.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(TriggerCommand), linked.Token);
            await stream.FlushAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Sending the trigger to SensoPart {options.Host} timed out.");
        }
    }

    private async Task<string> ReadResultLineAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.TimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var stream = resultClient!.GetStream();
            var line = new byte[MaximumResultBytes];
            var count = 0;
            var oneByte = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(oneByte, linked.Token) == 0)
                    throw new IOException($"SensoPart {options.Host} closed the result connection.");
                if (oneByte[0] == (byte)'\n') break;
                if (oneByte[0] == (byte)'\r') continue;
                if (count == line.Length)
                    throw new IOException($"SensoPart result exceeded {MaximumResultBytes} bytes without line end.");
                line[count++] = oneByte[0];
            }
            return Encoding.ASCII.GetString(line, 0, count);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SensoPart {options.Host} sent no result within {options.TimeoutMilliseconds} ms.");
        }
    }

    private static void DiscardPendingData(TcpClient client)
    {
        var buffer = new byte[256];
        while (client.Available > 0)
            _ = client.GetStream().Read(buffer, 0, Math.Min(buffer.Length, client.Available));
    }

    private void EnsureConnected()
    {
        if (triggerClient is null || resultClient is null)
            throw new InvalidOperationException("The SensoPart sensor is not connected.");
    }

    private void Close()
    {
        triggerClient?.Dispose();
        resultClient?.Dispose();
        triggerClient = null;
        resultClient = null;
    }

    private static SensopartOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<SensopartOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("SensopartTcp connection parameters must be a JSON object.");
        if (string.IsNullOrWhiteSpace(input.Host))
            throw new JsonException("SensopartTcp host is required.");
        if (input.TriggerPort is < 1 or > 65_535 || input.ResultPort is < 1 or > 65_535)
            throw new JsonException("SensopartTcp triggerPort and resultPort must be between 1 and 65535.");
        if (input.TriggerPort == input.ResultPort)
            throw new JsonException("SensopartTcp triggerPort and resultPort must be different.");
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("SensopartTcp timeoutMilliseconds must be between 1 and 120000.");
        if (input.Attempts is < 1 or > 100)
            throw new JsonException("SensopartTcp attempts must be between 1 and 100.");
        return new SensopartOptions(input.Host, input.TriggerPort, input.ResultPort, input.TimeoutMilliseconds,
            input.Attempts);
    }

    private sealed class SensopartOptionsInput
    {
        public string Host { get; init; } = "";
        // Ports and attempts as in the legacy SensoScanSerNr.c.
        public int TriggerPort { get; init; } = 2006;
        public int ResultPort { get; init; } = 2005;
        public int TimeoutMilliseconds { get; init; } = 5000;
        public int Attempts { get; init; } = 10;
    }

    private sealed record SensopartOptions(string Host, int TriggerPort, int ResultPort, int TimeoutMilliseconds,
        int Attempts);
}

public static class SensopartTcpRegistration
{
    public static DriverRegistry AddSensopartTcp(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("SensopartTcp", configuration => new SensopartTcp(configuration));
        return registry;
    }
}
