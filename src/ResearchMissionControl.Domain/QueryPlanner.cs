namespace ResearchMissionControl.Domain;

// Deterministic planning (spec §21: the Planner may use deterministic logic for
// straightforward requests). Extracts the salient terms from a question so the source
// search targets the subject ("suggest alternatives to azure service bus" -> "azure
// service bus") instead of matching on filler words. Falls back to the whole question.
public static class QueryPlanner
{
    private static readonly char[] Separators =
        { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '/' };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "whats", "is", "are", "the", "a", "an", "of", "to", "for", "and", "or", "vs", "versus",
        "suggest", "suggestion", "suggestions", "alternative", "alternatives", "compare", "comparison",
        "between", "how", "does", "do", "explain", "tell", "me", "about", "list", "best", "good",
        "which", "why", "when", "give", "some", "provide", "recommend", "recommendations",
        "difference", "differences", "into", "with", "on", "in", "using", "use", "can", "could",
        "should", "would", "i", "we", "options", "option",
    };

    public static string PlanQuery(string question)
    {
        var tokens = question
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !StopWords.Contains(token))
            .ToList();

        var query = string.Join(' ', tokens).Trim();
        return query.Length > 0 ? query : question.Trim();
    }
}
