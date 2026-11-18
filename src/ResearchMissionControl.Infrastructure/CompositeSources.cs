using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Fans a search out to several approved sources and round-robin merges their hits, so the
// top results draw from multiple sources instead of only the first. A single source being
// unavailable (transport error) degrades to the others rather than failing the search —
// the host still never searches an unapproved source. "The model proposes; the application
// authorizes" is unchanged: this only governs which approved sources are consulted.
public sealed class CompositeSourceSearch : ISourceSearch
{
    private readonly IReadOnlyList<ISourceSearch> _providers;

    public CompositeSourceSearch(IEnumerable<ISourceSearch> providers) =>
        _providers = providers.ToList();

    public async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var perProvider = new List<IReadOnlyList<SourceHit>>(_providers.Count);
        foreach (var provider in _providers)
        {
            try
            {
                perProvider.Add(await provider.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false));
            }
            catch (InferenceTransportException)
            {
                perProvider.Add(System.Array.Empty<SourceHit>()); // one source down → use the rest
            }
            catch (ArgumentException)
            {
                perProvider.Add(System.Array.Empty<SourceHit>());
            }
        }
        return RoundRobinMerge(perProvider, limit);
    }

    // Interleave the providers' results (provider1[0], provider2[0], provider1[1], ...),
    // de-duplicated by URL and bounded by limit, so no single source dominates the top hits.
    public static IReadOnlyList<SourceHit> RoundRobinMerge(IReadOnlyList<IReadOnlyList<SourceHit>> perProvider, int limit)
    {
        var merged = new List<SourceHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longest = perProvider.Count == 0 ? 0 : perProvider.Max(p => p.Count);
        for (var i = 0; i < longest && merged.Count < limit; i++)
        {
            foreach (var hits in perProvider)
            {
                if (i >= hits.Count)
                    continue;
                var hit = hits[i];
                if (!seen.Add(hit.Url))
                    continue;
                merged.Add(hit);
                if (merged.Count >= limit)
                    break;
            }
        }
        return merged;
    }
}

// Routes a fetch to the approved adapter whose host matches the URL. Fails closed: a URL
// whose host is not an approved source is rejected (never fetched). Each underlying adapter
// still enforces its own host lock, so this is defence in depth, not a replacement for it.
public sealed class CompositeSourceAdapter : ISourceAdapter
{
    private readonly IReadOnlyDictionary<string, ISourceAdapter> _byHost;

    public CompositeSourceAdapter(IReadOnlyDictionary<string, ISourceAdapter> adaptersByHost) =>
        _byHost = new Dictionary<string, ISourceAdapter>(adaptersByHost, StringComparer.OrdinalIgnoreCase);

    public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var host = new Uri(url, UriKind.Absolute).Host;
        if (!_byHost.TryGetValue(host, out var adapter))
            throw new ArgumentException($"No approved source adapter for host '{host}'.", nameof(url));
        return adapter.FetchAsync(url, cancellationToken);
    }
}
