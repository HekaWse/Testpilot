using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class Phase5ScannerTests
{
    [Fact]
    public void BothCodeReaderDriversUseTheSharedBarcodeScannerProfile()
    {
        var registry = new DriverRegistry().AddSensopartTcp().AddDatalogicMatrixSerial((_, _, _) => new FakeSerial());

        var sensopart = registry.GetProfile(DriverConfiguration.Parse("reader1", "SensopartTcp", "{\"host\":\"192.0.2.30\"}"));
        var matrix = registry.GetProfile(DriverConfiguration.Parse("reader2", "DatalogicMatrixSerial",
            "{\"model\":\"Matrix120\",\"portName\":\"COM3\"}"));

        foreach (var profile in new[] { sensopart, matrix })
        {
            Assert.Equal("code-reader", profile.Id);
            Assert.Equal([DeviceCapability.BarcodeScanner], profile.Capabilities);
            Assert.Single(profile.Channels);
        }
    }

    [Fact]
    public async Task SensopartTriggersAgainAfterCannotReadAndReturnsTheCode()
    {
        await using var sensor = new FakeSensopart("CANNOTREAD\n", "1234567890\r\n");
        var host = new DriverHost(new DriverRegistry().AddSensopartTcp(), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(sensor.Configuration());

        var code = await lease.GetCapability<IBarcodeScanner>().ScanAsync();

        Assert.Equal("1234567890", code);
        Assert.Equal(["TRG", "TRG"], sensor.Triggers);
        Assert.True((await lease.Instrument.SelfCheckAsync()).Passed);
    }

    [Fact]
    public async Task SensopartReportsReadErrorAfterAllAttemptsAndStaysConnected()
    {
        await using var sensor = new FakeSensopart("CANNOTREAD\n", "12345ABCDE\n", "123456789\n", "0987654321\n");
        var host = new DriverHost(new DriverRegistry().AddSensopartTcp(), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(sensor.Configuration("\"attempts\":3"));
        var scanner = lease.GetCapability<IBarcodeScanner>();

        var error = await Assert.ThrowsAsync<BarcodeReadException>(() => scanner.ScanAsync());

        Assert.Contains("'123456789'", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, sensor.Triggers.Count);
        Assert.Equal("0987654321", await scanner.ScanAsync());
    }

    [Fact]
    public async Task SensopartTimeoutClosesTheConnections()
    {
        await using var sensor = new FakeSensopart([null]);
        var host = new DriverHost(new DriverRegistry().AddSensopartTcp(), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(sensor.Configuration("\"timeoutMilliseconds\":300"));
        var scanner = lease.GetCapability<IBarcodeScanner>();

        await Assert.ThrowsAsync<TimeoutException>(() => scanner.ScanAsync());

        Assert.Single(sensor.Triggers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"host\":\"192.0.2.30\",\"triggerPort\":2005}")]
    [InlineData("{\"host\":\"192.0.2.30\",\"resultPort\":0}")]
    [InlineData("{\"host\":\"192.0.2.30\",\"attempts\":0}")]
    [InlineData("{\"host\":\"192.0.2.30\",\"codeLength\":0}")]
    [InlineData("{\"host\":\"192.0.2.30\",\"port\":2006}")]
    public void InvalidSensopartOptionsAreRejected(string json)
    {
        var registry = new DriverRegistry().AddSensopartTcp();

        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("reader1", "SensopartTcp", json)));
    }

    [Theory]
    [InlineData("1234567890\r\n")]
    [InlineData("1234567890\n")]
    [InlineData("1234567890")]
    public async Task MatrixSendsTriggerAndReadsCodeWithOrWithoutLineEnd(string answer)
    {
        var serial = new FakeSerial(answer);
        string? openedPort = null;
        int openedBaudRate = 0;
        var registry = new DriverRegistry().AddDatalogicMatrixSerial((port, baudRate, _) =>
        {
            openedPort = port;
            openedBaudRate = baudRate;
            return serial;
        });
        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        await using (var lease = await host.AcquireAsync(DriverConfiguration.Parse("reader1", "DatalogicMatrixSerial",
            "{\"model\":\"Matrix200\",\"portName\":\"COM7\",\"trigger\":\"\\u0016T\\r\"}")))
        {
            Assert.Equal("1234567890", await lease.GetCapability<IBarcodeScanner>().ScanAsync());
            Assert.Contains("Matrix200 on COM7", await lease.Instrument.IdentifyAsync(), StringComparison.Ordinal);
        }

        Assert.Equal("COM7", openedPort);
        Assert.Equal(115_200, openedBaudRate);
        Assert.Equal([0x16, (byte)'T', (byte)'\r'], serial.Written);
        Assert.Equal(1, serial.DiscardCount);
        Assert.True(serial.Disposed);
    }

    [Theory]
    [InlineData("12345\r\n")]
    [InlineData("12345")]
    [InlineData("12345ABCDE\r\n")]
    [InlineData("123456789012\r\n")]
    public async Task MatrixRejectsIncompleteOrInvalidCodes(string answer)
    {
        await using var lease = await AcquireMatrixAsync(new FakeSerial(answer));

        await Assert.ThrowsAsync<BarcodeReadException>(() => lease.GetCapability<IBarcodeScanner>().ScanAsync());
    }

    [Fact]
    public async Task MatrixWithoutAnswerTimesOut()
    {
        await using var lease = await AcquireMatrixAsync(new FakeSerial(""));

        await Assert.ThrowsAsync<TimeoutException>(() => lease.GetCapability<IBarcodeScanner>().ScanAsync());
    }

    [Fact]
    public async Task MatrixSendsTheDefaultTriggerR()
    {
        var serial = new FakeSerial("0123456789\r\n");
        await using (var lease = await AcquireMatrixAsync(serial))
        {
            Assert.Equal("0123456789", await lease.GetCapability<IBarcodeScanner>().ScanAsync());
        }

        Assert.Equal([(byte)'R'], serial.Written);
    }

    [Theory]
    [InlineData("{\"model\":\"Matrix200\",\"portName\":\"COM3\",\"codeLength\":6}")]
    [InlineData("{\"model\":\"Matrix200\",\"portName\":\"COM3\",\"trigger\":\"\"}")]
    [InlineData("{\"model\":\"Matrix200\",\"portName\":\"COM3\",\"trigger\":\"\u00e4\"}")]
    [InlineData("{\"model\":\"DP8530\",\"portName\":\"COM3\",\"trigger\":\"T\"}")]
    [InlineData("{\"model\":\"Matrix200\",\"trigger\":\"T\"}")]
    [InlineData("{\"model\":\"Matrix200\",\"portName\":\"COM3\",\"trigger\":\"T\",\"baudRate\":0}")]
    [InlineData("{\"model\":\"Matrix200\",\"portName\":\"COM3\",\"trigger\":\"T\",\"parity\":\"even\"}")]
    public void InvalidMatrixOptionsAreRejected(string json)
    {
        var registry = new DriverRegistry().AddDatalogicMatrixSerial((_, _, _) => new FakeSerial());

        Assert.Throws<JsonException>(() => registry.GetProfile(DriverConfiguration.Parse("reader1", "DatalogicMatrixSerial", json)));
    }

    private static async Task<IDeviceLease> AcquireMatrixAsync(FakeSerial serial)
    {
        var registry = new DriverRegistry().AddDatalogicMatrixSerial((_, _, _) => serial);
        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        return await host.AcquireAsync(DriverConfiguration.Parse("reader1", "DatalogicMatrixSerial",
            "{\"model\":\"Matrix120\",\"portName\":\"COM3\",\"timeoutMilliseconds\":500}"));
    }

    /// <summary>Serial port double: returns the queued answer byte by byte, then reports timeouts.</summary>
    private sealed class FakeSerial(string answer = "") : ISerialConnection
    {
        private readonly Queue<byte> pending = new(Encoding.ASCII.GetBytes(answer));
        public List<byte> Written { get; } = [];
        public int DiscardCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Write(byte[] data) => Written.AddRange(data);
        public int ReadByte(int timeoutMilliseconds) => pending.Count > 0 ? pending.Dequeue() : -1;
        public void DiscardInput() => DiscardCount++;
        public void Dispose() => Disposed = true;
    }

    /// <summary>SensoPart double: trigger and result listener; each "TRG" is answered with the next queued line (null = no answer).</summary>
    private sealed class FakeSensopart : IAsyncDisposable
    {
        private readonly TcpListener triggerListener = new(IPAddress.Loopback, 0);
        private readonly TcpListener resultListener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<string?> answers;
        private readonly ConcurrentQueue<string> triggers = new();
        private readonly Task serverTask;

        public FakeSensopart(params string?[] answers)
        {
            this.answers = new ConcurrentQueue<string?>(answers);
            triggerListener.Start();
            resultListener.Start();
            serverTask = Task.Run(ServeAsync);
        }

        public IReadOnlyList<string> Triggers => triggers.ToArray();

        public DriverConfiguration Configuration(string extraOptions = "")
        {
            var triggerPort = ((IPEndPoint)triggerListener.LocalEndpoint).Port;
            var resultPort = ((IPEndPoint)resultListener.LocalEndpoint).Port;
            var json = $"{{\"host\":\"127.0.0.1\",\"triggerPort\":{triggerPort},\"resultPort\":{resultPort}"
                + (extraOptions.Length > 0 ? "," + extraOptions : "") + "}";
            return DriverConfiguration.Parse("reader1", "SensopartTcp", json);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var trigger = await triggerListener.AcceptTcpClientAsync();
                using var result = await resultListener.AcceptTcpClientAsync();
                var triggerStream = trigger.GetStream();
                var resultStream = result.GetStream();
                var buffer = new byte[3];
                while (true)
                {
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await triggerStream.ReadAsync(buffer.AsMemory(read));
                        if (count == 0) return;
                        read += count;
                    }
                    triggers.Enqueue(Encoding.ASCII.GetString(buffer));
                    if (answers.TryDequeue(out var answer) && answer is not null)
                        await resultStream.WriteAsync(Encoding.ASCII.GetBytes(answer));
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // The driver closed the connection.
            }
        }

        public async ValueTask DisposeAsync()
        {
            triggerListener.Stop();
            resultListener.Stop();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
