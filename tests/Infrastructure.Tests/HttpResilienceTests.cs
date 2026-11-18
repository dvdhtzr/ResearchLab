using System.Net;
using System.Net.Http;
using ResearchMissionControl.Application;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: the source adapters retry transient HTTP failures (e.g. Wikipedia/Wikidata
// 429 rate-limiting) with backoff and only surface a transport error when attempts are
// exhausted. Driven by a stub handler with zero backoff — no network, no waiting.
public sealed class HttpResilienceTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int Calls { get; private set; }

        public SequenceHandler(params Func<HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var factory = _responses.Count > 0 ? _responses.Dequeue() : () => Ok("{}");
            return Task.FromResult(factory());
        }
    }

    private sealed class AlwaysHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public int Calls { get; private set; }
        public AlwaysHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpClient Client(HttpMessageHandler handler, string baseUrl) =>
        new(handler) { BaseAddress = new Uri(baseUrl) };

    [Fact]
    public async Task Wikidata_Retries429ThenSucceeds()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests),
            () => Ok("""{ "search": [ { "id": "Q1", "label": "A" } ] }"""));
        using var http = Client(handler, "https://www.wikidata.org/");
        using var adapter = new WikidataSourceAdapter(http, maxAttempts: 4, retryBaseDelay: TimeSpan.Zero);

        var hits = await adapter.SearchAsync("A", 5, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal(2, handler.Calls); // one 429, one success
    }

    [Fact]
    public async Task Wikipedia_Retries503ThenSucceeds()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Ok("""{ "query": { "search": [ { "title": "PostgreSQL" } ] } }"""));
        using var http = Client(handler, "https://en.wikipedia.org/");
        using var adapter = new WikipediaSourceAdapter(http, maxAttempts: 4, retryBaseDelay: TimeSpan.Zero);

        var hits = await adapter.SearchAsync("PostgreSQL", 5, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task PersistentRateLimit_ThrowsTransportAfterMaxAttempts()
    {
        var handler = new AlwaysHandler(HttpStatusCode.TooManyRequests);
        using var http = Client(handler, "https://en.wikipedia.org/");
        using var adapter = new WikipediaSourceAdapter(http, maxAttempts: 3, retryBaseDelay: TimeSpan.Zero);

        await Assert.ThrowsAsync<InferenceTransportException>(() =>
            adapter.SearchAsync("x", 3, CancellationToken.None));
        Assert.Equal(3, handler.Calls); // tried exactly maxAttempts times
    }

    [Fact]
    public async Task NonTransientError_IsNotRetried()
    {
        var handler = new AlwaysHandler(HttpStatusCode.BadRequest);
        using var http = Client(handler, "https://www.wikidata.org/");
        using var adapter = new WikidataSourceAdapter(http, maxAttempts: 4, retryBaseDelay: TimeSpan.Zero);

        await Assert.ThrowsAsync<InferenceTransportException>(() =>
            adapter.SearchAsync("x", 3, CancellationToken.None));
        Assert.Equal(1, handler.Calls); // 400 is final — no retry
    }
}
