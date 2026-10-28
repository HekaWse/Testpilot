using System.Globalization;
using System.Text.Json;

namespace Testpilot.Drivers;

/// <summary>Line-based SCPI transport. One command or query is processed at a time.</summary>
internal interface IScpiTransport : IAsyncDisposable
{
    /// <summary>False before ConnectAsync and after a timeout or I/O error closed the connection.</summary>
    bool IsOpen { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task WriteAsync(string command, CancellationToken cancellationToken);
    Task<string> QueryAsync(string command, CancellationToken cancellationToken);
}

/// <summary>Either a raw TCP socket (host/port) or a VISA resource string, never both.</summary>
internal sealed record ScpiEndpoint(string? Host, int Port, string? Resource)
{
    private const int MaximumResourceLength = 256;

    public static ScpiEndpoint Parse(string driverName, string? host, int port, string? resource)
    {
        var hasHost = !string.IsNullOrWhiteSpace(host);
        var hasResource = !string.IsNullOrWhiteSpace(resource);
        if (hasHost == hasResource)
            throw new JsonException($"{driverName} requires exactly one of 'host' (raw TCP) or 'resource' (VISA).");
        if (hasHost && port is < 1 or > 65_535)
            throw new JsonException($"{driverName} port must be between 1 and 65535.");
        if (hasResource && (resource!.Length > MaximumResourceLength || resource.Any(char.IsControl)))
            throw new JsonException($"{driverName} resource must be a VISA resource string without control characters.");
        return hasHost ? new(host, port, null) : new(null, port, resource);
    }

    public IScpiTransport CreateTransport(int timeoutMilliseconds, VisaSessionFactory visaSessionFactory) =>
        Resource is null
            ? new ScpiTcpConnection(Host!, Port, timeoutMilliseconds)
            : new VisaScpiTransport(Resource, timeoutMilliseconds, visaSessionFactory);

    public override string ToString() => Resource ?? $"{Host}:{Port}";
}

internal static class ScpiTransportExtensions
{
    /// <summary>
    /// A timeout or an aborted query closes the transport. Safe-state and adapter commands must still reach
    /// the instrument afterwards, so they reopen the connection first.
    /// </summary>
    public static async Task ReopenIfClosedAsync(this IScpiTransport transport, CancellationToken cancellationToken)
    {
        if (!transport.IsOpen)
            await transport.ConnectAsync(cancellationToken);
    }

    /// <summary>
    /// Reads SYST:ERR? until the instrument reports 0 ("No error") and throws when errors were queued.
    /// Both the 34980A and the HMP answer in the form &lt;code&gt;,"&lt;text&gt;".
    /// </summary>
    public static async Task ThrowIfInstrumentErrorsAsync(this IScpiTransport transport, string instrument,
        string commands, CancellationToken cancellationToken)
    {
        const int maximumEntries = 20;
        var errors = new List<string>();
        for (var index = 0; index < maximumEntries; index++)
        {
            var response = await transport.QueryAsync("SYST:ERR?", cancellationToken);
            var separator = response.IndexOf(',');
            var codeText = (separator < 0 ? response : response[..separator]).Trim();
            if (!int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new ScpiProtocolException($"{instrument} returned an invalid SYST:ERR? response '{response}'.");
            if (code == 0)
                break;
            errors.Add(response);
        }
        if (errors.Count != 0)
            throw new ScpiProtocolException($"{instrument} reported error(s) after '{commands}': {string.Join("; ", errors)}");
    }
}

internal static class ScpiCommandText
{
    /// <summary>Rejects user-configured commands that could break the line-based protocol.</summary>
    public static void Validate(string command, string driverName)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 512 || command.Any(char.IsControl))
            throw new JsonException($"{driverName} commands must be non-empty single-line text up to 512 characters.");
    }
}
