using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>Minimal serial port access; isolated so tests can run without a COM port.</summary>
internal interface ISerialConnection : IDisposable
{
    void Write(byte[] data);

    /// <summary>Returns the next byte, or -1 when none arrives within <paramref name="timeoutMilliseconds"/>.</summary>
    int ReadByte(int timeoutMilliseconds);

    void DiscardInput();
}

internal delegate ISerialConnection SerialConnectionFactory(string portName, int baudRate, int writeTimeoutMilliseconds);

internal sealed class SerialPortConnection : ISerialConnection
{
    private readonly SerialPort port;

    private SerialPortConnection(SerialPort port) => this.port = port;

    /// <summary>8 data bits, no parity, 1 stop bit, no handshake (legacy Scanner.c: OpenComConfig(..., 0, 8, 1, ...)).</summary>
    public static ISerialConnection Open(string portName, int baudRate, int writeTimeoutMilliseconds)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = writeTimeoutMilliseconds
        };
        try
        {
            port.Open();
            return new SerialPortConnection(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    public void Write(byte[] data) => port.Write(data, 0, data.Length);

    public int ReadByte(int timeoutMilliseconds)
    {
        port.ReadTimeout = timeoutMilliseconds;
        try
        {
            return port.ReadByte();
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    public void DiscardInput() => port.DiscardInBuffer();

    public void Dispose() => port.Dispose();
}

/// <summary>
/// Datalogic Matrix 120 / Matrix 200 code reader on a (virtual USB) COM port.
/// From the legacy CVI driver (Scanner.c, ConfigMatrix200/ReadMatrix200): 115200 baud, 8N1; the trigger "R"
/// starts the reading phase; the answer is the 10-digit serial number, possibly followed by CR/LF.
/// The readers are configured beforehand (Matrix 200 with VisiSet, Matrix 120 with DL.CODE); the driver
/// sends no configuration.
/// End of the answer: '\n', or no further byte for <see cref="EndOfAnswerPauseMilliseconds"/> once at least
/// 10 characters have arrived (the legacy code did not depend on a terminator either).
/// </summary>
internal sealed class DatalogicMatrixSerial(DriverConfiguration configuration, SerialConnectionFactory serialFactory)
    : IBarcodeScanner
{
    private const int EndOfAnswerPauseMilliseconds = 100;
    private const int MaximumAnswerCharacters = 256;
    private static readonly string[] SupportedModels = ["Matrix120", "Matrix200"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly MatrixOptions options = ParseOptions(configuration);
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private ISerialConnection? serial;

    public DeviceProfile Profile { get; } = EmbeddedProfiles.Load("CodeReader.yaml");

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (serial is not null) throw new InvalidOperationException($"The {options.Model} is already connected.");
            serial = await Task.Run(() => serialFactory(options.PortName, options.BaudRate, options.TimeoutMilliseconds),
                cancellationToken);
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
            serial?.Dispose();
            serial = null;
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
        // No identification command is used by the legacy code.
        return Task.FromResult($"Datalogic {options.Model} on {options.PortName} ({options.BaudRate} baud, 8N1)");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        return Task.FromResult(new SelfCheckResult(true,
            $"{options.PortName} is open (the legacy code uses no self-test command)."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> ScanAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            var activeSerial = serial ?? throw new InvalidOperationException($"The {options.Model} is not connected.");
            cancellationToken.ThrowIfCancellationRequested();
            // Serial I/O is blocking; every read is bounded by the configured timeout.
            return await Task.Run(() => TriggerAndRead(activeSerial), CancellationToken.None);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private string TriggerAndRead(ISerialConnection activeSerial)
    {
        activeSerial.DiscardInput();
        activeSerial.Write(options.Trigger);
        var elapsed = Stopwatch.StartNew();
        var answer = new StringBuilder();
        while (true)
        {
            var remaining = options.TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
            var complete = answer.Length >= SerialNumberCode.Length;
            var wait = complete ? Math.Min(EndOfAnswerPauseMilliseconds, Math.Max(remaining, 1)) : remaining;
            var value = wait > 0 ? activeSerial.ReadByte(wait) : -1;
            if (value < 0)
            {
                if (complete) break;
                if (answer.Length == 0)
                    throw new TimeoutException($"{options.Model} on {options.PortName} sent no code within {options.TimeoutMilliseconds} ms.");
                throw new BarcodeReadException($"{options.Model} on {options.PortName} sent an incomplete code '{answer}'.");
            }
            if (value == '\n') break;
            if (value == '\r') continue;
            if (answer.Length == MaximumAnswerCharacters)
                throw new BarcodeReadException($"{options.Model} answer exceeded {MaximumAnswerCharacters} characters.");
            answer.Append((char)value);
        }
        var code = answer.ToString();
        if (!SerialNumberCode.IsValid(code))
            throw new BarcodeReadException($"{options.Model} on {options.PortName} returned '{code}', expected {SerialNumberCode.Description}.");
        return code;
    }

    private void EnsureConnected()
    {
        if (serial is null) throw new InvalidOperationException($"The {options.Model} is not connected.");
    }

    private static MatrixOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<MatrixOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("DatalogicMatrixSerial connection parameters must be a JSON object.");
        var model = SupportedModels.SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item, input.Model))
            ?? throw new JsonException($"DatalogicMatrixSerial model must be one of: {string.Join(", ", SupportedModels)}.");
        if (string.IsNullOrWhiteSpace(input.PortName) || input.PortName.Any(char.IsWhiteSpace))
            throw new JsonException("DatalogicMatrixSerial portName is required, for example \"COM3\".");
        if (input.BaudRate is < 1_200 or > 921_600)
            throw new JsonException("DatalogicMatrixSerial baudRate must be between 1200 and 921600.");
        if (string.IsNullOrEmpty(input.Trigger) || input.Trigger.Length > 32 || !Ascii.IsValid(input.Trigger))
            throw new JsonException("DatalogicMatrixSerial trigger must be 1 to 32 ASCII characters (default \"R\").");
        if (input.TimeoutMilliseconds is < 1 or > 120_000)
            throw new JsonException("DatalogicMatrixSerial timeoutMilliseconds must be between 1 and 120000.");
        return new MatrixOptions(model, input.PortName, input.BaudRate, Encoding.ASCII.GetBytes(input.Trigger),
            input.TimeoutMilliseconds);
    }

    private sealed class MatrixOptionsInput
    {
        public string Model { get; init; } = "";
        public string PortName { get; init; } = "";
        // Legacy Scanner.c opens the port with 115200 baud; "R" starts the reading phase on both models.
        public int BaudRate { get; init; } = 115_200;
        public string Trigger { get; init; } = "R";
        public int TimeoutMilliseconds { get; init; } = 5000;
    }

    private sealed record MatrixOptions(string Model, string PortName, int BaudRate, byte[] Trigger,
        int TimeoutMilliseconds);
}

public static class DatalogicMatrixSerialRegistration
{
    public static DriverRegistry AddDatalogicMatrixSerial(this DriverRegistry registry) =>
        registry.AddDatalogicMatrixSerial(SerialPortConnection.Open);

    internal static DriverRegistry AddDatalogicMatrixSerial(this DriverRegistry registry, SerialConnectionFactory serialFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(serialFactory);
        registry.Register("DatalogicMatrixSerial", configuration => new DatalogicMatrixSerial(configuration, serialFactory));
        return registry;
    }
}
