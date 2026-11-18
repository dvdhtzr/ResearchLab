using ResearchMissionControl.Application;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: the multi-source merge (round-robin, de-duplicated, bounded) and the
// host-routed fetch (fail closed on an unapproved host), plus resilience to one source
// being unavailable.
public sealed class CompositeSourceTests
{
    private sealed class FakeSearch : ISourceSearch
    {
        private readonly IReadOnlyList<SourceHit> _hits;
        public FakeSearch(params SourceHit[] hits) => _hits = hits;
        public Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(_hits);
    }

    private sealed class ThrowingSearch : ISourceSearch
    {
        public Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            throw new InferenceTransportException("source down");
    }

    private sealed class HostSource : ISourceAdapter
    {
        private readonly string _tag;
        public HostSource(string tag) => _tag = tag;
        public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDocument(url, _tag, $"text from {_tag}", $"{_tag}:doc"));
    }

    private static SourceHit Hit(string host, string id) =>
        new($"https://{host}/wiki/{id}", id);

    [Fact]
    public void RoundRobinMerge_InterleavesDeduplicatesAndBounds()
    {
        var a = new[] { Hit("en.wikipedia.org", "A1"), Hit("en.wikipedia.org", "A2") };
        var b = new[] { Hit("www.wikidata.org", "B1"), Hit("en.wikipedia.org", "A1") /* dup url */ };

        var merged = CompositeSourceSearch.RoundRobinMerge(new IReadOnlyList<SourceHit>[] { a, b }, 5);

        // Interleaved (A1, B1, A2), the duplicate A1 from provider b dropped.
        Assert.Equal(new[] { "https://en.wikipedia.org/wiki/A1", "https://www.wikidata.org/wiki/B1", "https://en.wikipedia.org/wiki/A2" },
            merged.Select(h => h.Url).ToArray());
    }

    [Fact]
    public void RoundRobinMerge_RespectsLimit()
    {
        var a = new[] { Hit("en.wikipedia.org", "A1"), Hit("en.wikipedia.org", "A2") };
        var b = new[] { Hit("www.wikidata.org", "B1"), Hit("www.wikidata.org", "B2") };

        var merged = CompositeSourceSearch.RoundRobinMerge(new IReadOnlyList<SourceHit>[] { a, b }, 2);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { "https://en.wikipedia.org/wiki/A1", "https://www.wikidata.org/wiki/B1" },
            merged.Select(h => h.Url).ToArray());
    }

    [Fact]
    public async Task Search_MergesAcrossProvidersTopResultFromEach()
    {
        var search = new CompositeSourceSearch(new ISourceSearch[]
        {
            new FakeSearch(Hit("en.wikipedia.org", "A1")),
            new FakeSearch(Hit("www.wikidata.org", "B1")),
        });

        var hits = await search.SearchAsync("q", 3, CancellationToken.None);

        Assert.Equal(new[] { "https://en.wikipedia.org/wiki/A1", "https://www.wikidata.org/wiki/B1" },
            hits.Select(h => h.Url).ToArray());
    }

    [Fact]
    public async Task Search_ToleratesOneSourceBeingDown()
    {
        var search = new CompositeSourceSearch(new ISourceSearch[]
        {
            new ThrowingSearch(),
            new FakeSearch(Hit("www.wikidata.org", "B1")),
        });

        var hits = await search.SearchAsync("q", 3, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal("https://www.wikidata.org/wiki/B1", hits[0].Url);
    }

    [Fact]
    public async Task Adapter_RoutesFetchByHost()
    {
        var adapter = new CompositeSourceAdapter(new Dictionary<string, ISourceAdapter>
        {
            ["en.wikipedia.org"] = new HostSource("wikipedia"),
            ["www.wikidata.org"] = new HostSource("wikidata"),
        });

        var wiki = await adapter.FetchAsync("https://en.wikipedia.org/wiki/PostgreSQL", CancellationToken.None);
        var data = await adapter.FetchAsync("https://www.wikidata.org/wiki/Q192490", CancellationToken.None);

        Assert.Equal("wikipedia", wiki.Title);
        Assert.Equal("wikidata", data.Title);
    }

    [Fact]
    public async Task Adapter_FailsClosedOnUnapprovedHost()
    {
        var adapter = new CompositeSourceAdapter(new Dictionary<string, ISourceAdapter>
        {
            ["en.wikipedia.org"] = new HostSource("wikipedia"),
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.FetchAsync("https://example.com/wiki/Anything", CancellationToken.None));
    }
}
