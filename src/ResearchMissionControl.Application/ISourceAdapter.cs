namespace ResearchMissionControl.Application;

public sealed record SourceDocument(string Url, string Title, string Text, string SourceIdentity);

// Port for an approved research source (e.g. Wikipedia/MediaWiki, or an owner-approved
// HTTPS URL). Implemented live in Infrastructure and faked in tests. Fetched documents
// are untrusted data, never instructions.
public interface ISourceAdapter
{
    Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken);
}
