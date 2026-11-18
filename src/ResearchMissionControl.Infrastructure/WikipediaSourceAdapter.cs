using System.Net.Http;
using System.Text.Json;
using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Approved free research source: English Wikipedia via the MediaWiki REST API.
// SECURITY (spec §20/§23): HTTPS only, host is locked to en.wikipedia.org, fetched
// documents are treated as untrusted data. No commercial search API, no paid service.
public sealed class WikipediaSourceAdapter : ISourceSearch, ISourceAdapter, IDisposable
{
    public const string AllowedHost = "en.wikipedia.org";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBaseDelay;

    public WikipediaSourceAdapter(HttpClient? http = null, int maxAttempts = 4, TimeSpan? retryBaseDelay = null)
    {
        _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri("https://en.wikipedia.org/"), Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "ResearchMissionControl/0.1 (local research tool)");
    }

    public async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var requestUri =
            $"w/api.php?action=query&list=search&format=json&srlimit={limit}&srsearch={Uri.EscapeDataString(query)}";
        var body = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);

        var hits = new List<SourceHit>();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("query", out var queryElement) &&
            queryElement.TryGetProperty("search", out var search) &&
            search.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in search.EnumerateArray())
            {
                if (item.TryGetProperty("title", out var title) && title.GetString() is { } titleText)
                    hits.Add(new SourceHit(TitleToUrl(titleText), titleText));
            }
        }
        return hits;
    }

    public async Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only https://{AllowedHost} sources are approved; got '{url}'.", nameof(url));
        }

        var title = TitleFromUrl(uri);
        var requestUri =
            $"w/api.php?action=query&prop=extracts&explaintext=1&redirects=1&format=json&titles={Uri.EscapeDataString(title)}";
        var body = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var text = string.Empty;
        if (document.RootElement.TryGetProperty("query", out var queryElement) &&
            queryElement.TryGetProperty("pages", out var pages) &&
            pages.ValueKind == JsonValueKind.Object)
        {
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("extract", out var extract) && extract.GetString() is { } extractText)
                {
                    text = extractText;
                    break;
                }
            }
        }
        // Wikipedia article prose is licensed CC BY-SA 4.0 (recorded as rights metadata).
        return new SourceDocument(url, title, text, $"wikipedia:en:{title}", "CC BY-SA 4.0");
    }

    private Task<string> GetAsync(string requestUri, CancellationToken cancellationToken) =>
        HttpResilience.GetStringAsync(_http, requestUri, _maxAttempts, _retryBaseDelay, "Wikipedia", cancellationToken);

    private static string TitleToUrl(string title) =>
        $"https://{AllowedHost}/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";

    private static string TitleFromUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string prefix = "/wiki/";
        var raw = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path.TrimStart('/');
        return Uri.UnescapeDataString(raw).Replace('_', ' ');
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
