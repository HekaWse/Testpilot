using Ivi.Visa;

namespace Testpilot.Drivers;

/// <summary>Minimal message-based VISA session; isolated so tests can run without an installed VISA.</summary>
internal interface IVisaMessageSession : IDisposable
{
    void Write(string text);
    string ReadString();
}

internal delegate IVisaMessageSession VisaSessionFactory(string resource, int timeoutMilliseconds);

/// <summary>
/// Opens sessions through the IVI VISA.NET global resource manager. The vendor implementation
/// (Keysight or NI) is selected by the installed VISA and the IVI conflict manager, not by this code.
/// </summary>
internal sealed class IviVisaMessageSession : IVisaMessageSession
{
    private readonly IMessageBasedSession session;

    private IviVisaMessageSession(IMessageBasedSession session) => this.session = session;

    public static IVisaMessageSession Open(string resource, int timeoutMilliseconds)
    {
        var opened = GlobalResourceManager.Open(resource, AccessModes.None, timeoutMilliseconds);
        if (opened is not IMessageBasedSession messageSession)
        {
            opened.Dispose();
            throw new NotSupportedException($"VISA resource '{resource}' is not a message-based session.");
        }
        try
        {
            messageSession.TimeoutMilliseconds = timeoutMilliseconds;
            messageSession.TerminationCharacter = (byte)'\n';
            messageSession.TerminationCharacterEnabled = true;
            return new IviVisaMessageSession(messageSession);
        }
        catch
        {
            messageSession.Dispose();
            throw;
        }
    }

    public void Write(string text)
    {
        try
        {
            session.RawIO.Write(text);
        }
        catch (IOTimeoutException exception)
        {
            throw new TimeoutException("The VISA write timed out.", exception);
        }
    }

    public string ReadString()
    {
        try
        {
            return session.RawIO.ReadString();
        }
        catch (IOTimeoutException exception)
        {
            throw new TimeoutException("The VISA read timed out.", exception);
        }
    }

    public void Dispose() => session.Dispose();
}

internal sealed class VisaScpiTransport(string resource, int timeoutMilliseconds, VisaSessionFactory sessionFactory)
    : IScpiTransport
{
    private const int MaximumResponseCharacters = 8192;
    private readonly SemaphoreSlim ioLock = new(1, 1);
    private IVisaMessageSession? session;

    public bool IsOpen => session is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            if (session is not null) throw new InvalidOperationException("The VISA session is already open.");
            session = await Task.Run(() => sessionFactory(resource, timeoutMilliseconds), cancellationToken);
        }
        finally
        {
            ioLock.Release();
        }
    }

    public Task WriteAsync(string command, CancellationToken cancellationToken) =>
        RunAsync(command, activeSession =>
        {
            activeSession.Write(command + "\n");
            return "";
        }, cancellationToken);

    public Task<string> QueryAsync(string command, CancellationToken cancellationToken) =>
        RunAsync(command, activeSession =>
        {
            activeSession.Write(command + "\n");
            var response = activeSession.ReadString().Trim();
            if (response.Length > MaximumResponseCharacters)
                throw new ScpiProtocolException($"SCPI response exceeded {MaximumResponseCharacters} characters.");
            return response;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await ioLock.WaitAsync();
        try
        {
            CloseSession();
        }
        finally
        {
            ioLock.Release();
        }
    }

    // VISA I/O is blocking and cannot be cancelled mid-call; the session timeout bounds each call.
    private async Task<string> RunAsync(string command, Func<IVisaMessageSession, string> operation,
        CancellationToken cancellationToken)
    {
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            var activeSession = session ?? throw new InvalidOperationException("The VISA session is not open.");
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => operation(activeSession), CancellationToken.None);
        }
        catch (TimeoutException exception)
        {
            // A late response would desynchronize the next query, so the session is closed.
            CloseSession();
            throw new TimeoutException($"SCPI command '{command}' timed out after {timeoutMilliseconds} ms.", exception);
        }
        catch (Exception exception) when (exception is VisaException or IOException)
        {
            CloseSession();
            throw;
        }
        finally
        {
            ioLock.Release();
        }
    }

    private void CloseSession()
    {
        session?.Dispose();
        session = null;
    }
}
