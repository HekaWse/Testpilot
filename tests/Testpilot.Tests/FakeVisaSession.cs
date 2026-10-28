using Testpilot.Drivers;

namespace Testpilot.Tests;

/// <summary>
/// Scripted VISA session: returns the response for the last written command (without the trailing newline).
/// SYST:ERR? answers "no error" unless the script returns something else.
/// </summary>
internal sealed class FakeVisaSession(Func<string, string?> respond) : IVisaMessageSession
{
    private string? lastCommand;

    public List<string> Writes { get; } = [];
    public bool Disposed { get; private set; }
    public bool TimeoutOnRead { get; set; }

    public void Write(string text)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Writes.Add(text);
        lastCommand = text.TrimEnd('\n');
    }

    public string ReadString()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (TimeoutOnRead) throw new TimeoutException("Simulated VISA timeout.");
        var response = respond(lastCommand ?? "") ?? (lastCommand == "SYST:ERR?" ? "+0,\"No error\"" : null);
        return (response ?? throw new TimeoutException($"No response scripted for '{lastCommand}'.")) + "\n";
    }

    public void Dispose() => Disposed = true;
}
