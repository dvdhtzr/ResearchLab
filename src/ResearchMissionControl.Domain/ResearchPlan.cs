namespace ResearchMissionControl.Domain;

// A validated research plan: the search queries the host authorized the source search to
// run for a job. Spec §21 — the model PROPOSES queries; the application (PlanValidator)
// AUTHORIZES them. The validator bounds count and length, trims and de-duplicates, and
// guarantees a non-empty plan by degrading an empty/unusable proposal to the deterministic
// single query (QueryPlanner). A single query is never treated as a comparison.
public sealed record ResearchPlan(IReadOnlyList<string> Queries, bool IsComparison);

public static class PlanValidator
{
    public const int MaxQueries = 3;
    public const int MaxQueryChars = 80;

    public static ResearchPlan Validate(
        IEnumerable<string>? proposedQueries,
        bool proposedComparison,
        string question)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = new List<string>();
        foreach (var raw in proposedQueries ?? Enumerable.Empty<string>())
        {
            if (raw is null)
                continue;
            var query = Collapse(raw);
            if (query.Length > MaxQueryChars)
                query = query[..MaxQueryChars].Trim();
            if (query.Length == 0)
                continue;
            if (!seen.Add(query))
                continue;
            queries.Add(query);
            if (queries.Count >= MaxQueries)
                break;
        }

        // Authorize: an empty or unusable proposal degrades to deterministic planning so the
        // workflow is never left without a query to run.
        if (queries.Count == 0)
            return new ResearchPlan(new[] { QueryPlanner.PlanQuery(question) }, false);

        // A comparison needs at least two distinct lookups to balance evidence across entities;
        // a single surviving query is never a comparison, whatever the model proposed.
        var isComparison = proposedComparison && queries.Count >= 2;
        return new ResearchPlan(queries, isComparison);
    }

    // Collapse all runs of whitespace (including newlines a model may emit) to single spaces.
    private static string Collapse(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).Trim();
    }
}
