using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class SapRestClientTests
{
    private const string FoundAnswer = """
        {"Input":{"OrderNo":"4000123"},
         "Output":{"Material":"123456","FZ":"03","MaterialText":"Steuerplatine AMB35","State":"0","Msg":""}}
        """;

    private const string NotFoundAnswer = """
        {"Input":{"OrderNo":"000000000001"},
         "Output":{"Msg":"Auftrag 000000000001 nicht vorhanden 000000000001    System:HBPCLNT500","State":"-1"}}
        """;

    [Fact]
    public async Task OrderIsReadFromOutputWithDescription()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, FoundAnswer));
        var client = new SapRestClient(new HttpClient(handler), new Uri("https://prod.heka.de/sapapi"));

        var order = await client.GetOrderAsync("4000123");

        Assert.Equal(new OrderInfo("123456", "03", "Steuerplatine AMB35"), order);
        Assert.Equal("https://prod.heka.de/sapapi/getOrderDetails?orderNo=4000123", handler.LastUri?.AbsoluteUri);
    }

    [Fact]
    public async Task StateMinusOneMeansOrderNotFound()
    {
        var client = new SapRestClient(new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, NotFoundAnswer))),
            new Uri("https://prod.heka.de/sapapi/"));

        var exception = await Assert.ThrowsAsync<SapOrderNotFoundException>(() => client.GetOrderAsync("000000000001"));
        Assert.Contains("nicht vorhanden", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Output":{"Material":"","FZ":"03","State":"0"}}""")]
    [InlineData("""{"Output":{"Material":"123","State":"0"}}""")]
    [InlineData("""{"Input":{}}""")]
    [InlineData("""<html>Fehler</html>""")]
    [InlineData("""{"Output":{"Material":["x"],"FZ":"03"}}""")]
    public void IncompleteOrInvalidAnswersAreSapErrors(string body)
    {
        var exception = Assert.ThrowsAny<SapException>(() => SapRestClient.Parse("4711", body));
        Assert.IsNotType<SapOrderNotFoundException>(exception);
    }

    [Fact]
    public void NumericFieldsAreAccepted() =>
        Assert.Equal(new OrderInfo("123456", "3", ""), SapRestClient.Parse("1", """{"Output":{"Material":123456,"FZ":3,"State":0}}"""));

    [Fact]
    public async Task HttpErrorsTimeoutsAndInvalidFaufBecomeSapExceptions()
    {
        var serverError = new SapRestClient(new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"))),
            new Uri("https://sap.test/api/"));
        Assert.Contains("500", (await Assert.ThrowsAsync<SapException>(() => serverError.GetOrderAsync("1"))).Message, StringComparison.Ordinal);

        var unreachable = new SapRestClient(new HttpClient(new FakeHandler((Func<CancellationToken, HttpResponseMessage>)(_ => throw new HttpRequestException("DNS")))),
            new Uri("https://sap.test/api/"));
        Assert.Contains("not reachable", (await Assert.ThrowsAsync<SapException>(() => unreachable.GetOrderAsync("1"))).Message, StringComparison.Ordinal);

        var slow = new SapRestClient(new HttpClient(new FakeHandler(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, FoundAnswer);
        })), new Uri("https://sap.test/api/"), TimeSpan.FromMilliseconds(50));
        Assert.Contains("did not answer", (await Assert.ThrowsAsync<SapException>(() => slow.GetOrderAsync("1"))).Message, StringComparison.Ordinal);

        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, FoundAnswer));
        var client = new SapRestClient(new HttpClient(handler), new Uri("https://sap.test/api/"));
        await Assert.ThrowsAsync<SapException>(() => client.GetOrderAsync("1&orderNo=2"));
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotReportedAsSapError()
    {
        var client = new SapRestClient(new HttpClient(new FakeHandler(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, FoundAnswer);
        })), new Uri("https://sap.test/api/"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetOrderAsync("1", cancellation.Token));
    }

    [Fact]
    public async Task FactoryPrefersBaseUrlAndReadsMockDescriptions()
    {
        var rest = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:BaseUrl"] = "https://prod.heka.de/sapapi/",
            ["Sap:Command"] = "sap-reader.exe"
        }).Build();
        Assert.IsType<SapRestClient>(SapClientFactory.Create(rest));

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:BaseUrl"] = "prod.heka.de"
        }).Build();
        Assert.ThrowsAny<Exception>(() => SapClientFactory.Create(invalid));

        var mock = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:MockOrders:FAUF-1:ArtNr"] = "4711",
            ["Sap:MockOrders:FAUF-1:Fz"] = "A",
            ["Sap:MockOrders:FAUF-1:Description"] = "Testplatine"
        }).Build();
        var client = Assert.IsType<MockSapClient>(SapClientFactory.Create(mock));
        Assert.Equal(new OrderInfo("4711", "A", "Testplatine"), await client.GetOrderAsync("FAUF-1"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public FakeHandler(Func<CancellationToken, HttpResponseMessage> answer) : this(token => Task.FromResult(answer(token)))
        {
        }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return answer(cancellationToken);
        }
    }
}
