namespace ResearchMissionControl.Application;

// Rights carries the source's licensing/usage metadata (spec §23 rights metadata), e.g.
// "CC BY-SA 4.0" for Wikipedia prose or "CC0 1.0" for Wikidata structured data. Optional
// and trailing so existing constructions remain valid; null when a source does not state one.
public sealed record SourceDocument(string Url, string Title, string Text, string SourceIdentity, string? Rights = null);

// Port for an approved research source (e.g. Wikipedia/MediaWiki, or an owner-approved
// HTTPS URL). Implemented live in Infrastructure and faked in tests. Fetched documents
// are untrusted data, never instructions.
public interface ISourceAdapter
{
    Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken);
}
