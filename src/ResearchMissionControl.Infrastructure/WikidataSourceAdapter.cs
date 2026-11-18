using System.Net.Http;
using System.Text;
using System.Text.Json;
using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Structured-facts research source: Wikidata via the MediaWiki wbsearchentities /
// wbgetentities API. Complements Wikipedia prose with attributable per-entity facts
// (what it is, who made it, when, written-in language, ...) — especially useful for
// grounding comparisons. SECURITY (spec §20/§23): HTTPS only, host locked to
// www.wikidata.org; fetched data is untrusted. Structured data is CC0 1.0 (public domain).
// No commercial search API, no paid service, no API key.
public sealed class WikidataSourceAdapter : ISourceSearch, ISourceAdapter, IDisposable
{
    public const string AllowedHost = "www.wikidata.org";
    private const int MaxStatements = 24;   // bounded rendering per entity
    private const int MaxAliases = 8;
    private const int MaxLabelIdsPerBatch = 50;
    private const string Rights = "CC0 1.0";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBaseDelay;

    public WikidataSourceAdapter(HttpClient? http = null, int maxAttempts = 4, TimeSpan? retryBaseDelay = null)
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
            _http = new HttpClient { BaseAddress = new Uri("https://www.wikidata.org/"), Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "ResearchMissionControl/0.1 (local research tool)");
    }

    public async Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var requestUri =
            $"w/api.php?action=wbsearchentities&format=json&language=en&uselang=en&type=item&limit={limit}&search={Uri.EscapeDataString(query)}";
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

        var entityId = EntityIdFromUrl(uri);
        var entityBody = await GetAsync(
            $"w/api.php?action=wbgetentities&format=json&languages=en&props=labels%7Cdescriptions%7Caliases%7Cclaims&ids={Uri.EscapeDataString(entityId)}",
            cancellationToken).ConfigureAwait(false);
        var entity = ParseEntity(entityBody, entityId, MaxStatements);

        // Resolve the labels of the property IDs and entity-valued facts in one bounded batch
        // so the rendered facts read as "instance of: musical composition", not "P31: Q7366".
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelIds = CollectLabelIds(entity);
        if (labelIds.Count > 0)
        {
            var batch = labelIds.Take(MaxLabelIdsPerBatch).ToList();
            var labelBody = await GetAsync(
                $"w/api.php?action=wbgetentities&format=json&languages=en&props=labels&ids={Uri.EscapeDataString(string.Join('|', batch))}",
                cancellationToken).ConfigureAwait(false);
            labels = ParseLabels(labelBody);
        }

        var text = RenderText(entity, labels, MaxStatements + 4);
        var title = string.IsNullOrEmpty(entity.Label) ? entityId : entity.Label;
        return new SourceDocument(url, title, text, $"wikidata:{entityId}", Rights);
    }

    // ---- pure, independently testable parsing / rendering (model-free) ----

    public static List<SourceHit> ParseSearchHits(string json, int limit)
    {
        var hits = new List<SourceHit>();
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return hits; }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.Array)
                return hits;
            foreach (var item in search.EnumerateArray())
            {
                if (hits.Count >= limit)
                    break;
                if (!item.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id || id.Length == 0)
                    continue;
                var label = item.TryGetProperty("label", out var labelElement) ? labelElement.GetString() ?? id : id;
                hits.Add(new SourceHit($"https://{AllowedHost}/wiki/{Uri.EscapeDataString(id)}", label));
            }
        }
        return hits;
    }

    public static WikidataEntity ParseEntity(string json, string entityId, int maxStatements)
    {
        var empty = new WikidataEntity(entityId, string.Empty, string.Empty,
            System.Array.Empty<string>(), System.Array.Empty<WikidataStatement>());
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return empty; }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("entities", out var entities) ||
                !entities.TryGetProperty(entityId, out var entity))
                return empty;

            var label = ReadLangValue(entity, "labels");
            var description = ReadLangValue(entity, "descriptions");
            var aliases = ReadAliases(entity);
            var statements = ReadStatements(entity, maxStatements);
            return new WikidataEntity(entityId, label, description, aliases, statements);
        }
    }

    public static IReadOnlyList<string> CollectLabelIds(WikidataEntity entity)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? id)
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
                ids.Add(id);
        }
        foreach (var statement in entity.Statements)
        {
            Add(statement.PropertyId);
            Add(statement.EntityValueId);
        }
        return ids;
    }

    public static Dictionary<string, string> ParseLabels(string json)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return labels; }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
                return labels;
            foreach (var entity in entities.EnumerateObject())
            {
                var value = ReadLangValue(entity.Value, "labels");
                if (value.Length > 0)
                    labels[entity.Name] = value;
            }
        }
        return labels;
    }

    public static string RenderText(WikidataEntity entity, IReadOnlyDictionary<string, string> labels, int maxLines)
    {
        var builder = new StringBuilder();
        if (entity.Label.Length > 0)
            builder.AppendLine(entity.Label);
        if (entity.Description.Length > 0)
            builder.AppendLine(entity.Description);
        if (entity.Aliases.Count > 0)
            builder.AppendLine("Also known as: " + string.Join(", ", entity.Aliases));

        var lines = 0;
        var wroteFactsHeader = false;
        foreach (var statement in entity.Statements)
        {
            if (lines >= maxLines)
                break;
            var propertyLabel = Label(labels, statement.PropertyId);
            var value = statement.EntityValueId is { Length: > 0 } entityValue
                ? Label(labels, entityValue)
                : statement.LiteralValue ?? string.Empty;
            if (value.Length == 0)
                continue;
            if (!wroteFactsHeader)
            {
                builder.AppendLine();
                builder.AppendLine("Facts:");
                wroteFactsHeader = true;
            }
            builder.AppendLine($"{propertyLabel}: {value}");
            lines++;
        }
        return builder.ToString().TrimEnd();
    }

    // ---- helpers ----

    private static string Label(IReadOnlyDictionary<string, string> labels, string id) =>
        labels.TryGetValue(id, out var label) && label.Length > 0 ? label : id;

    private static string ReadLangValue(JsonElement entity, string group)
    {
        if (entity.TryGetProperty(group, out var groupElement) &&
            groupElement.TryGetProperty("en", out var en) &&
            en.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static IReadOnlyList<string> ReadAliases(JsonElement entity)
    {
        var aliases = new List<string>();
        if (entity.TryGetProperty("aliases", out var aliasGroup) &&
            aliasGroup.TryGetProperty("en", out var en) &&
            en.ValueKind == JsonValueKind.Array)
        {
            foreach (var alias in en.EnumerateArray())
            {
                if (aliases.Count >= MaxAliases)
                    break;
                if (alias.TryGetProperty("value", out var value) && value.GetString() is { Length: > 0 } text)
                    aliases.Add(text);
            }
        }
        return aliases;
    }

    private static IReadOnlyList<WikidataStatement> ReadStatements(JsonElement entity, int maxStatements)
    {
        var statements = new List<WikidataStatement>();
        if (!entity.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Object)
            return statements;

        foreach (var property in claims.EnumerateObject())
        {
            if (statements.Count >= maxStatements)
                break;
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var claim in property.Value.EnumerateArray())
            {
                if (statements.Count >= maxStatements)
                    break;
                var statement = ReadStatement(property.Name, claim);
                if (statement is not null)
                    statements.Add(statement);
            }
        }
        return statements;
    }

    private static WikidataStatement? ReadStatement(string propertyId, JsonElement claim)
    {
        if (!claim.TryGetProperty("mainsnak", out var mainsnak) ||
            !mainsnak.TryGetProperty("snaktype", out var snaktype) ||
            snaktype.GetString() != "value" ||
            !mainsnak.TryGetProperty("datavalue", out var datavalue) ||
            !datavalue.TryGetProperty("type", out var typeElement) ||
            !datavalue.TryGetProperty("value", out var value))
            return null;

        switch (typeElement.GetString())
        {
            case "wikibase-entityid":
                return value.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } entityId
                    ? new WikidataStatement(propertyId, null, entityId)
                    : null;
            case "string":
                return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    ? new WikidataStatement(propertyId, text, null)
                    : null;
            case "monolingualtext":
                return value.TryGetProperty("text", out var mono) && mono.GetString() is { Length: > 0 } monoText
                    ? new WikidataStatement(propertyId, monoText, null)
                    : null;
            case "time":
                return value.TryGetProperty("time", out var time) && time.GetString() is { Length: > 0 } timeText
                    ? new WikidataStatement(propertyId, FormatTime(timeText), null)
                    : null;
            case "quantity":
                return value.TryGetProperty("amount", out var amount) && amount.GetString() is { Length: > 0 } amountText
                    ? new WikidataStatement(propertyId, amountText.TrimStart('+'), null)
                    : null;
            default:
                return null;
        }
    }

    // "+2000-01-01T00:00:00Z" -> "2000-01-01"; leaves anything unexpected as-is (minus a leading '+').
    private static string FormatTime(string raw)
    {
        var value = raw.TrimStart('+');
        var t = value.IndexOf('T');
        return t > 0 ? value[..t] : value;
    }

    private static string EntityIdFromUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string prefix = "/wiki/";
        var raw = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path.TrimStart('/');
        return Uri.UnescapeDataString(raw);
    }

    private Task<string> GetAsync(string requestUri, CancellationToken cancellationToken) =>
        HttpResilience.GetStringAsync(_http, requestUri, _maxAttempts, _retryBaseDelay, "Wikidata", cancellationToken);

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

// A single rendered Wikidata fact: a property and either a literal value or a referenced
// entity id (whose label is resolved separately). Public for independent parse/render tests.
public sealed record WikidataStatement(string PropertyId, string? LiteralValue, string? EntityValueId);

public sealed record WikidataEntity(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<WikidataStatement> Statements);
