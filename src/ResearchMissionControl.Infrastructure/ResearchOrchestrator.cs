using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

public sealed record ResearchResult(
    bool Completed,
    ResearchReport? Report,
    string Disposition,
    IReadOnlyList<string> AcceptedFindingIds,
    IReadOnlyList<string> FindingStatements,
    int EvidenceSnapshots,
    int ModelAttempts,
    string? InconclusiveReason)
{
    public static ResearchResult Inconclusive(string reason, int attempts, int evidence) =>
        new(false, null, "inconclusive", System.Array.Empty<string>(), System.Array.Empty<string>(), evidence, attempts, reason);
}

// The research laboratory workflow (spec §21): Plan → acquire authorized evidence →
// Researcher → Skeptic → deterministic host decision → Report Builder. Every model
// output is validated against captured evidence; nothing self-certifies. Governed by the
// InferenceGateway (durable, budgeted) and the EvidenceAcquisition tool authorization.
public sealed class ResearchOrchestrator
{
    private const int MaxPromptPassages = 6;
    private const int MaxPassageChars = 500;
    private const int SearchHitsPerQuery = 3;
    private const int MaxDocumentsPerQuery = 2;
    private const int MaxDocumentsTotal = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly InferenceGateway _gateway;
    private readonly ISourceSearch _search;
    private readonly EvidenceAcquisition _acquisition;
    private readonly EvidenceStore _evidence;
    private readonly string _modelId;

    public ResearchOrchestrator(
        InferenceGateway gateway,
        ISourceSearch search,
        EvidenceAcquisition acquisition,
        EvidenceStore evidence,
        string modelId)
    {
        _gateway = gateway;
        _search = search;
        _acquisition = acquisition;
        _evidence = evidence;
        _modelId = modelId;
    }

    public async Task<ResearchResult> RunAsync(
        string jobId,
        long generation,
        string question,
        BudgetSnapshot budget,
        CancellationToken cancellationToken)
    {
        var attempts = 0;

        // 1. Plan: the model PROPOSES targeted search queries (and splits comparisons into one
        //    query per entity); PlanValidator AUTHORIZES them. A failed or unusable proposal
        //    degrades to deterministic keyword planning, so planning never leaves us worse off.
        var plan = await PlanAsync(jobId, generation, question, budget, attempts, cancellationToken).ConfigureAwait(false);
        attempts++; // the planner attempt is budgeted whether or not the model succeeded

        // 2. Acquire authorized evidence, BALANCED across the planned queries. Each query is
        //    searched independently and documents are fetched round-robin, so a comparison
        //    grounds on every entity instead of only the first article's passages.
        var hitsByQuery = new List<List<SourceHit>>(plan.Queries.Count);
        foreach (var query in plan.Queries)
        {
            var hits = await _search.SearchAsync(query, SearchHitsPerQuery, cancellationToken).ConfigureAwait(false);
            hitsByQuery.Add(hits.ToList());
        }
        if (hitsByQuery.All(hits => hits.Count == 0))
            return ResearchResult.Inconclusive("no_sources", attempts, 0);

        var docPassages = new List<List<string>>();
        foreach (var url in SelectBalancedUrls(hitsByQuery))
        {
            var proposal = new ToolProposal("source.fetch.v1", $"{{\"url\":{JsonSerializer.Serialize(url)}}}");
            var acquired = await _acquisition.FetchAsync(jobId, proposal, url, cancellationToken).ConfigureAwait(false);
            if (acquired is not null && acquired.PassageIds.Count > 0)
                docPassages.Add(acquired.PassageIds.ToList());
        }
        if (docPassages.Count == 0)
            return ResearchResult.Inconclusive("no_evidence", attempts, _evidence.CountSnapshots(jobId));

        var labelToId = new Dictionary<string, string>(StringComparer.Ordinal);
        var passageBlock = new StringBuilder();
        // Interleave passages across documents so both sides of a comparison reach the model
        // even when one article is far longer than the other.
        var labeled = InterleavePassages(docPassages, MaxPromptPassages);
        for (var i = 0; i < labeled.Count; i++)
        {
            var label = "p" + (i + 1);
            labelToId[label] = labeled[i];
            var text = _evidence.GetPassageText(labeled[i]) ?? string.Empty;
            passageBlock.AppendLine($"[{label}] {Truncate(text, MaxPassageChars)}");
        }

        // 3. Researcher.
        var researcher = new InferenceRequest(
            jobId, generation, _modelId, "researcher", "research",
            ResearcherPrompt(question, passageBlock.ToString()), new[] { "findings" });
        var researchOut = await _gateway.ExecuteAsync(researcher, generation, budget, attempts, cancellationToken).ConfigureAwait(false);
        attempts++;
        if (!researchOut.Success)
            return ResearchResult.Inconclusive($"research_failed:{researchOut.Failure}", attempts, _evidence.CountSnapshots(jobId));

        var findings = ParseFindings(researchOut.Json!, labelToId);
        var validation = ResearchValidators.ValidateFindings(findings, _evidence.PassageExists);
        var committed = validation.Accepted;
        if (committed.Count == 0)
            return ResearchResult.Inconclusive("no_supported_findings", attempts, _evidence.CountSnapshots(jobId));
        var committedIds = new HashSet<string>(committed.Select(f => f.FindingId), StringComparer.Ordinal);

        // 4. Skeptic (challenges that reference genuine committed findings only).
        var skeptic = new InferenceRequest(
            jobId, generation, _modelId, "skeptic", "challenge",
            SkepticPrompt(committed), new[] { "challenges" });
        var skepticOut = await _gateway.ExecuteAsync(skeptic, generation, budget, attempts, cancellationToken).ConfigureAwait(false);
        attempts++;
        var challenges = skepticOut.Success
            ? ParseChallenges(skepticOut.Json!).Where(c => ResearchValidators.IsChallengeValid(c, committedIds)).ToList()
            : new List<Challenge>();

        // 5. Deterministic host decision (bounded by the round budget).
        _ = ResearchHost.Decide(challenges.Count, 1, budget.ResearchRounds);

        // 6. Report Builder (cannot introduce claims absent from committed findings).
        var reportRequest = new InferenceRequest(
            jobId, generation, _modelId, "report_builder", "report",
            ReportPrompt(question, committed, challenges.Count), new[] { "summary", "findingIds", "disposition" });
        var reportOut = await _gateway.ExecuteAsync(reportRequest, generation, budget, attempts, cancellationToken).ConfigureAwait(false);
        attempts++;
        if (!reportOut.Success)
            return ResearchResult.Inconclusive($"report_failed:{reportOut.Failure}", attempts, _evidence.CountSnapshots(jobId));

        var parsed = ParseReport(reportOut.Json!);
        var citedIds = (parsed.FindingIds ?? new List<string>()).Where(committedIds.Contains).ToList();
        if (citedIds.Count == 0)
            citedIds = committedIds.ToList(); // never cite a non-committed finding; fall back to all committed
        var disposition = DeriveDisposition(challenges.Count);
        var report = new ResearchReport(
            question,
            string.IsNullOrWhiteSpace(parsed.Summary) ? "See findings." : parsed.Summary!,
            citedIds,
            disposition);

        if (!ResearchValidators.IsReportValid(report, committedIds))
            return ResearchResult.Inconclusive("invalid_report", attempts, _evidence.CountSnapshots(jobId));

        return new ResearchResult(
            true, report, disposition, committedIds.ToList(),
            committed.Select(f => f.Statement).ToList(),
            _evidence.CountSnapshots(jobId), attempts, null);
    }

    // Model-assisted planning: propose search queries, then authorize them. Any failure
    // (transport, budget, invalid output) falls back to the deterministic single query.
    private async Task<ResearchPlan> PlanAsync(
        string jobId,
        long generation,
        string question,
        BudgetSnapshot budget,
        int attempts,
        CancellationToken cancellationToken)
    {
        var request = new InferenceRequest(
            jobId, generation, _modelId, "planner", "plan",
            PlannerPrompt(question), new[] { "queries" });
        var outcome = await _gateway.ExecuteAsync(request, generation, budget, attempts, cancellationToken).ConfigureAwait(false);
        if (!outcome.Success)
            return new ResearchPlan(new[] { QueryPlanner.PlanQuery(question) }, false);

        var dto = ParsePlan(outcome.Json!);
        return PlanValidator.Validate(dto.Queries, dto.Comparison ?? false, question);
    }

    // Round-robin document selection across the planned queries: take the top hit of each
    // query before any query's second hit, bounded per-query and in total. Guarantees a
    // two-entity comparison fetches a document for each entity before a second for either.
    private static List<string> SelectBalancedUrls(IReadOnlyList<List<SourceHit>> hitsByQuery)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < MaxDocumentsPerQuery; round++)
        {
            foreach (var hits in hitsByQuery)
            {
                if (round >= hits.Count)
                    continue;
                var url = hits[round].Url;
                if (!seen.Add(url))
                    continue;
                urls.Add(url);
                if (urls.Count >= MaxDocumentsTotal)
                    return urls;
            }
        }
        return urls;
    }

    // Interleave each document's ordered passages (doc1.p0, doc2.p0, doc1.p1, ...) up to the
    // prompt cap, so no single long document crowds the others out of the evidence block.
    private static List<string> InterleavePassages(IReadOnlyList<List<string>> docPassages, int max)
    {
        var result = new List<string>();
        var longest = docPassages.Count == 0 ? 0 : docPassages.Max(doc => doc.Count);
        for (var i = 0; i < longest && result.Count < max; i++)
        {
            foreach (var doc in docPassages)
            {
                if (i >= doc.Count)
                    continue;
                result.Add(doc[i]);
                if (result.Count >= max)
                    break;
            }
        }
        return result;
    }

    private static string DeriveDisposition(int challengeCount) =>
        challengeCount == 0 ? "supported" : "mixed";

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] : text;

    // ---- prompts ----

    private static string PlannerPrompt(string question) =>
        "You are a research planner. Propose the search queries that will find the best " +
        "encyclopedia evidence to answer the question.\n" +
        "- Use a single query for a simple factual question.\n" +
        "- For a comparison or recommendation (\"X vs Y\", \"compare X and Y\", \"alternatives to X\"), " +
        "produce a SEPARATE query for EACH entity so each is researched on its own.\n" +
        "- Each query is short keywords (an encyclopedia topic or title), not a full sentence. " +
        "At most 3 queries.\n\n" +
        $"Question: {question}\n\n" +
        "Respond with ONLY a JSON object of this exact shape:\n" +
        "{\"queries\":[\"...\"],\"comparison\":false}\n" +
        "Set \"comparison\" to true only when the question compares or chooses between multiple entities.";

    private static string ResearcherPrompt(string question, string passages) =>
        "You are a research analyst. Using ONLY the numbered evidence passages below, extract findings that help " +
        "answer the question. Cite the passage labels you used.\n\n" +
        $"Question: {question}\n\nEvidence:\n{passages}\n\n" +
        "Respond with ONLY a JSON object of this exact shape:\n" +
        "{\"findings\":[{\"id\":\"f1\",\"statement\":\"...\",\"evidence\":[\"p1\"],\"limitations\":\"...\"}]}\n" +
        "Cite only passage labels that appear above. Omit any claim not supported by the passages.";

    private static string SkepticPrompt(IReadOnlyList<Finding> findings)
    {
        var builder = new StringBuilder();
        foreach (var finding in findings)
            builder.AppendLine($"- {finding.FindingId}: {finding.Statement}");
        return "You are a skeptical reviewer. Challenge weak findings only.\n\n" +
            $"Findings:\n{builder}\n" +
            "Respond with ONLY JSON:\n" +
            "{\"challenges\":[{\"target\":\"f1\",\"classification\":\"insufficient_evidence\",\"explanation\":\"...\"}]}\n" +
            "classification must be one of: insufficient_evidence, contradiction, conflicting_sources, missing_context, reasoning_gap. " +
            "If there are no concerns, respond {\"challenges\":[]}.";
    }

    private static string ReportPrompt(string question, IReadOnlyList<Finding> findings, int challengeCount)
    {
        var builder = new StringBuilder();
        foreach (var finding in findings)
            builder.AppendLine($"- {finding.FindingId}: {finding.Statement}");
        return "You are a report builder. Write an executive summary answering the question using ONLY the findings " +
            "listed; do not introduce new claims.\n\n" +
            $"Question: {question}\nFindings:\n{builder}\nChallenges raised: {challengeCount}\n\n" +
            "Respond with ONLY JSON:\n" +
            "{\"summary\":\"...\",\"findingIds\":[\"f1\"],\"disposition\":\"supported\"}";
    }

    // ---- parsing (defensive: model variability degrades to inconclusive, never a crash) ----

    private static PlanDto ParsePlan(string json)
    {
        try { return JsonSerializer.Deserialize<PlanDto>(json, JsonOptions) ?? new PlanDto(null, null); }
        catch (JsonException) { return new PlanDto(null, null); }
    }

    private List<Finding> ParseFindings(string json, IReadOnlyDictionary<string, string> labelToId)
    {
        var result = new List<Finding>();
        FindingsDto? dto;
        try { dto = JsonSerializer.Deserialize<FindingsDto>(json, JsonOptions); }
        catch (JsonException) { return result; }
        if (dto?.Findings is null) return result;

        var index = 0;
        foreach (var item in dto.Findings)
        {
            index++;
            var id = string.IsNullOrWhiteSpace(item.Id) ? "f" + index : item.Id!;
            var evidence = (item.Evidence ?? new List<string>())
                .Select(label => labelToId.TryGetValue(label, out var real) ? real : label)
                .ToList();
            result.Add(new Finding(id, item.Statement ?? string.Empty, evidence, item.Limitations ?? string.Empty));
        }
        return result;
    }

    private static List<Challenge> ParseChallenges(string json)
    {
        var result = new List<Challenge>();
        ChallengesDto? dto;
        try { dto = JsonSerializer.Deserialize<ChallengesDto>(json, JsonOptions); }
        catch (JsonException) { return result; }
        if (dto?.Challenges is null) return result;
        foreach (var item in dto.Challenges)
            result.Add(new Challenge(item.Target ?? string.Empty, item.Classification ?? string.Empty, item.Explanation ?? string.Empty));
        return result;
    }

    private static ReportDto ParseReport(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReportDto>(json, JsonOptions) ?? new ReportDto(null, null, null);
        }
        catch (JsonException)
        {
            return new ReportDto(null, null, null);
        }
    }

    private sealed record PlanDto(
        [property: JsonPropertyName("queries")] List<string>? Queries,
        [property: JsonPropertyName("comparison")] bool? Comparison);

    private sealed record FindingsDto(
        [property: JsonPropertyName("findings")] List<FindingDto>? Findings);

    private sealed record FindingDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("statement")] string? Statement,
        [property: JsonPropertyName("evidence")] List<string>? Evidence,
        [property: JsonPropertyName("limitations")] string? Limitations);

    private sealed record ChallengesDto(
        [property: JsonPropertyName("challenges")] List<ChallengeDto>? Challenges);

    private sealed record ChallengeDto(
        [property: JsonPropertyName("target")] string? Target,
        [property: JsonPropertyName("classification")] string? Classification,
        [property: JsonPropertyName("explanation")] string? Explanation);

    private sealed record ReportDto(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("findingIds")] List<string>? FindingIds,
        [property: JsonPropertyName("disposition")] string? Disposition);
}
