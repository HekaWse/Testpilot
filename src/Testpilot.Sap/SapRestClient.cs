using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Testpilot.Sap;

/// <summary>
/// Reads an order from the company SAP REST API (read-only):
/// <c>GET {baseUrl}getOrderDetails?orderNo={fauf}</c>. The answer contains <c>Output.Material</c> (article number),
/// <c>Output.FZ</c> and <c>Output.MaterialText</c>; <c>Output.State</c> "-1" with <c>Output.Msg</c> means "not found".
/// The FAUF is sent unchanged (SAP pads it to 12 digits itself). TLS certificates are checked normally.
/// </summary>
public sealed class SapRestClient : ISapClient
{
    private static readonly Regex FaufPattern = new("^[0-9A-Za-z-]{1,40}$", RegexOptions.CultureInvariant);
    private readonly HttpClient httpClient;
    private readonly Uri baseUrl;
    private readonly TimeSpan timeout;

    /// <param name="httpClient">Shared client; its own timeout is not used, <paramref name="timeout"/> applies per request.</param>
    /// <param name="baseUrl">For example <c>https://prod.heka.de/sapapi/</c> (a missing trailing slash is added).</param>
    public SapRestClient(HttpClient httpClient, Uri baseUrl, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("The SAP base URL must be an absolute http(s) URL.", nameof(baseUrl));
        this.httpClient = httpClient;
        this.baseUrl = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<OrderInfo> GetOrderAsync(string fauf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FaufPattern.IsMatch(fauf))
            throw new SapException($"FAUF '{fauf}' has an invalid format (only letters, digits and '-', at most 40 characters).");
        var requestUri = new Uri(baseUrl, "getOrderDetails?orderNo=" + Uri.EscapeDataString(fauf));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        string body;
        try
        {
            using var response = await httpClient.GetAsync(requestUri, timeoutSource.Token);
            body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
                throw new SapException($"SAP answered {(int)response.StatusCode} {response.ReasonPhrase} for order '{fauf}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new SapException($"SAP did not answer within {timeout.TotalSeconds:0} s.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SapException($"SAP is not reachable ({requestUri.Host}): {exception.Message}", exception);
        }
        return Parse(fauf, body);
    }

    /// <summary>Reads the order from the JSON answer; public for tests.</summary>
    public static OrderInfo Parse(string fauf, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Output", out var output)
                || output.ValueKind != JsonValueKind.Object)
                throw new SapException($"SAP answer for order '{fauf}' has no 'Output' object.");
            if (Text(output, "State") == "-1")
                throw new SapOrderNotFoundException(fauf, Text(output, "Msg"));
            var artNr = Text(output, "Material");
            var fz = Text(output, "FZ");
            if (artNr.Length == 0 || fz.Length == 0)
                throw new SapException($"SAP answer for order '{fauf}' contains no article number (Material) or FZ. {Text(output, "Msg")}".TrimEnd());
            return new OrderInfo(artNr, fz, Text(output, "MaterialText"));
        }
        catch (JsonException exception)
        {
            throw new SapException($"SAP answer for order '{fauf}' is no valid JSON.", exception);
        }
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null => "",
            _ => throw new SapException($"SAP field '{name}' has the unexpected JSON type {value.ValueKind.ToString().ToLower(CultureInfo.InvariantCulture)}.")
        };
    }
}
