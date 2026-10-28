using System.Net.Sockets;
using System.Text;

namespace Testpilot.Drivers;

internal sealed class ScpiTcpConnection(string host, int port, int timeoutMilliseconds) : IScpiTransport
{
    private const int MaximumResponseBytes = 8192;
    private readonly SemaphoreSlim ioLock = new(1, 1);
    private TcpClient? client;
    private NetworkStream? stream;

    public bool IsOpen => stream is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (client is not null) throw new InvalidOperationException("The SCPI connection is already open.");
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var connectedClient = new TcpClient();
        try
        {
            await connectedClient.ConnectAsync(host, port, linked.Token);
            client = connectedClient;
            stream = connectedClient.GetStream();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            connectedClient.Dispose();
            throw new TimeoutException($"Connecting to SCPI endpoint {host}:{port} timed out.");
        }
        catch
        {
            connectedClient.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            var activeStream = stream ?? throw new InvalidOperationException("The SCPI connection is not open.");
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await activeStream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), linked.Token);
            await activeStream.FlushAsync(linked.Token);
        }
        catch (IOException)
        {
            CloseConnection();
            throw;
        }
        catch (OperationCanceledException)
        {
            CloseConnection();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"SCPI command '{command}' timed out after {timeoutMilliseconds} ms.");
        }
        finally
        {
            ioLock.Release();
        }
    }

    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            var activeStream = stream ?? throw new InvalidOperationException("The SCPI connection is not open.");
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await activeStream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), linked.Token);
            await activeStream.FlushAsync(linked.Token);
            var response = new byte[MaximumResponseBytes];
            var count = 0;
            var oneByte = new byte[1];
            while (true)
            {
                var read = await activeStream.ReadAsync(oneByte, linked.Token);
                if (read == 0) throw new ScpiProtocolException("The SCPI endpoint closed before returning a response.");
                if (oneByte[0] == (byte)'\n') break;
                if (oneByte[0] != (byte)'\r')
                {
                    if (count == response.Length)
                        throw new ScpiProtocolException($"SCPI response exceeded {MaximumResponseBytes} bytes.");
                    response[count++] = oneByte[0];
                }
            }
            return Encoding.ASCII.GetString(response, 0, count).Trim();
        }
        catch (IOException)
        {
            CloseConnection();
            throw;
        }
        catch (OperationCanceledException)
        {
            CloseConnection();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"SCPI command '{command}' timed out after {timeoutMilliseconds} ms.");
        }
        finally
        {
            ioLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ioLock.WaitAsync();
        try
        {
            CloseConnection();
        }
        finally
        {
            ioLock.Release();
        }
    }

    private void CloseConnection()
    {
        stream?.Dispose();
        client?.Dispose();
        stream = null;
        client = null;
    }
}

public sealed class ScpiProtocolException(string message) : IOException(message);
