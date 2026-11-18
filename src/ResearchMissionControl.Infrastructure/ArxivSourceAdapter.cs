using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Scholarly research source: the arXiv API (Atom XML). Adds peer-adjacent technical
// grounding (titles, authors, abstracts) for questions Wikipedia/Wikidata cover thinly.
// SECURITY (spec §20/§23): HTTPS only, host locked to export.arxiv.org; responses are
// untrusted data. Descriptive metadata is CC0 1.0. No API key, no paid service.
// arXiv asks for no more than ~1 request / 3 s, so the retry base delay is set to 3 s; the
// composite keeps arXiv to a small number of calls per run.
public sealed class ArxivSourceAdapter : ISourceSearch, ISourceAdapter, IDisposable
{
    public const string AllowedHost = "export.arxiv.org";
    private const string Rights = "CC0 1.0";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBaseDelay;

    public ArxivSourceAdapter(HttpClient? http = null, int maxAttempts = 4, TimeSpan? retryBaseDelay = null)
    {
        _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(3); // arXiv throttle: ~1 req / 3 s
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri("https://export.arxiv.org/"), Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "ResearchMissionControl/0.1 (local research tool)");
    }

    public async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // "all:" searches across all fields; only the query value is escaped (the field prefix is literal).
        var requestUri = $"api/query?search_query=all:{Uri.EscapeDataString(query)}&start=0&max_results={limit}";
        var body = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return ParseSearchHits(body, limit);
    }

    public async Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only https://{AllowedHost} sources are approved; got '{url}'.", nameof(url));
        }

        var id = IdFromHandle(uri);
        var body = await GetAsync($"api/query?id_list={id}&max_results=1", cancellationToken).ConfigureAwait(false);
        var paper = ParsePaper(body);
        var canonicalUrl = $"https://arxiv.org/abs/{id}";
        if (paper is null)
            return new SourceDocument(canonicalUrl, id, string.Empty, $"arxiv:{id}", Rights);
        var title = paper.Title.Length > 0 ? paper.Title : id;
        return new SourceDocument(canonicalUrl, title, RenderPaperText(paper), $"arxiv:{id}", Rights);
    }

    // ---- pure, independently testable parsing / rendering (model-free) ----

    public static List<SourceHit> ParseSearchHits(string atomXml, int limit)
    {
        var hits = new List<SourceHit>();
        XDocument document;
        try { document = XDocument.Parse(atomXml); }
        catch (System.Xml.XmlException) { return hits; }
        if (document.Root is null)
            return hits;

        foreach (var entry in document.Root.Elements(Atom + "entry"))
        {
            if (hits.Count >= limit)
                break;
            var idUrl = entry.Element(Atom + "id")?.Value;
            if (string.IsNullOrWhiteSpace(idUrl))
                continue;
            var id = ArxivIdFromAbsUrl(idUrl);
            if (id.Length == 0)
                continue;
            var title = CollapseWhitespace(entry.Element(Atom + "title")?.Value ?? id);
            hits.Add(new SourceHit($"https://{AllowedHost}/abs/{id}", title));
        }
        return hits;
    }

    public static ArxivPaper? ParsePaper(string atomXml)
    {
        XDocument document;
        try { document = XDocument.Parse(atomXml); }
        catch (System.Xml.XmlException) { return null; }

        var entry = document.Root?.Elements(Atom + "entry").FirstOrDefault();
        if (entry is null)
            return null;

        var id = ArxivIdFromAbsUrl(entry.Element(Atom + "id")?.Value ?? string.Empty);
        var title = CollapseWhitespace(entry.Element(Atom + "title")?.Value ?? string.Empty);
        var summary = CollapseWhitespace(entry.Element(Atom + "summary")?.Value ?? string.Empty);
        var authors = entry.Elements(Atom + "author")
            .Select(author => CollapseWhitespace(author.Element(Atom + "name")?.Value ?? string.Empty))
            .Where(name => name.Length > 0)
            .ToList();
        return new ArxivPaper(id, title, authors, summary);
    }

    public static string RenderPaperText(ArxivPaper paper)
    {
        var builder = new StringBuilder();
        if (paper.Title.Length > 0)
            builder.AppendLine(paper.Title);
        if (paper.Authors.Count > 0)
            builder.AppendLine("Authors: " + string.Join(", ", paper.Authors));
        if (paper.Abstract.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Abstract: " + paper.Abstract);
        }
        return builder.ToString().TrimEnd();
    }

    // "http://arxiv.org/abs/1706.03762v7" -> "1706.03762v7"; "…/abs/math/0309136" -> "math/0309136".
    public static string ArxivIdFromAbsUrl(string absUrl)
    {
        const string marker = "/abs/";
        var index = absUrl.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : absUrl[(index + marker.Length)..].Trim();
    }

    // ---- helpers ----

    private static string IdFromHandle(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string prefix = "/abs/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path.TrimStart('/');
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private Task<string> GetAsync(string requestUri, CancellationToken cancellationToken) =>
        HttpResilience.GetStringAsync(_http, requestUri, _maxAttempts, _retryBaseDelay, "arXiv", cancellationToken);

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

public sealed record ArxivPaper(string Id, string Title, IReadOnlyList<string> Authors, string Abstract);
