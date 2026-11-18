namespace ResearchMissionControl.Application;

public sealed record SourceHit(string Url, string Title);

// Port for searching an approved source for candidate documents. Kept separate from
// ISourceAdapter so existing fakes need not change. Implemented live by the Wikipedia
// adapter and faked in tests.
public interface ISourceSearch
{
    Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
