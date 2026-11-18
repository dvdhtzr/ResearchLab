using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// M4 phase B (model-free): the full orchestrator driven by a scripted inference client
// and fake source — verifies the Plan→Researcher→Skeptic→Report workflow and its
// governance without Ollama. The live equivalent is gated in Integration.Tests.
//
// The scripted responses are consumed in workflow order; the first is the PLANNER's
// proposed queries, then findings, challenges, and the report.
public sealed class M4OrchestratorTests
{
    private sealed class ScriptedClient : IInferenceClient
    {
        private readonly Queue<string> _responses;
        public List<InferenceRequest> Requests { get; } = new();

        public ScriptedClient(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("digest-abc");

        public Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "{}");
        }
    }

    private sealed class OneHitSearch : ISourceSearch
    {
        public Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceHit>>(
                new[] { new SourceHit("https://en.wikipedia.org/wiki/PostgreSQL", "PostgreSQL") });
    }

    private sealed class TextSource : ISourceAdapter
    {
        public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDocument(
                url, "PostgreSQL",
                "PostgreSQL uses MVCC for concurrency control. It is open source and ACID compliant.",
                "wikipedia:en:PostgreSQL"));
    }

    // Two-entity fakes: each query resolves to its own article, and each article has its own
    // distinctive text — so a balanced acquisition can be observed in the researcher's prompt.
    private sealed class TwoEntitySearch : ISourceSearch
    {
        public Task<IReadOnlyList<SourceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            var isMySql = query.Contains("MySQL", StringComparison.OrdinalIgnoreCase);
            var url = isMySql
                ? "https://en.wikipedia.org/wiki/MySQL"
                : "https://en.wikipedia.org/wiki/PostgreSQL";
            var title = isMySql ? "MySQL" : "PostgreSQL";
            return Task.FromResult<IReadOnlyList<SourceHit>>(new[] { new SourceHit(url, title) });
        }
    }

    private sealed class TwoEntitySource : ISourceAdapter
    {
        public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
        {
            var isMySql = url.Contains("MySQL", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isMySql
                ? new SourceDocument(url, "MySQL", "MySQL uses a pluggable storage engine architecture.", "wikipedia:en:MySQL")
                : new SourceDocument(url, "PostgreSQL", "PostgreSQL uses MVCC for concurrency control.", "wikipedia:en:PostgreSQL"));
        }
    }

    // Throws for the MySQL article (one source/host failing mid-run), succeeds for PostgreSQL.
    private sealed class FlakySource : ISourceAdapter
    {
        public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
        {
            if (url.Contains("MySQL", StringComparison.OrdinalIgnoreCase))
                throw new InferenceTransportException("mysql source down");
            return Task.FromResult(new SourceDocument(
                url, "PostgreSQL", "PostgreSQL uses MVCC for concurrency control.", "wikipedia:en:PostgreSQL"));
        }
    }

    private static ResearchOrchestrator Build(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        ScriptedClient client,
        ISourceSearch search,
        ISourceAdapter source)
    {
        var registry = new ModelRegistry(new[] { new ApprovedModel("qwen3.5:9b", "digest-abc") });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var evidence = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(source, evidence, ToolRegistry.Default());
        return new ResearchOrchestrator(gateway, search, acquisition, evidence, "qwen3.5:9b");
    }

    [Fact]
    public async Task CompletesWithEvidenceGroundedReport()
    {
        using var db = new TestDb();
        var client = new ScriptedClient(
            "{\"queries\":[\"PostgreSQL\"],\"comparison\":false}",
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"PostgreSQL uses MVCC\",\"evidence\":[\"p1\"],\"limitations\":\"single source\"}]}",
            "{\"challenges\":[]}",
            "{\"summary\":\"PostgreSQL is a capable database.\",\"findingIds\":[\"f1\"],\"disposition\":\"supported\"}");
        var orchestrator = Build(db.Open(), client, new OneHitSearch(), new TextSource());

        var result = await orchestrator.RunAsync(
            "job-1", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.True(result.Completed, result.InconclusiveReason);
        Assert.NotNull(result.Report);
        Assert.Contains("f1", result.Report!.CitedFindingIds);
        Assert.Contains("f1", result.AcceptedFindingIds);
        Assert.Equal(1, result.EvidenceSnapshots);
        Assert.Equal("supported", result.Disposition);
        // The planner ran as its own stage before the researcher.
        Assert.Contains(client.Requests, r => r.AgentRole == "planner");
    }

    [Fact]
    public async Task UngroundedFindings_YieldInconclusive()
    {
        using var db = new TestDb();
        // The researcher cites a passage label that was never provided → rejected.
        var client = new ScriptedClient(
            "{\"queries\":[\"PostgreSQL\"],\"comparison\":false}",
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"Fabricated\",\"evidence\":[\"p9\"],\"limitations\":\"\"}]}");
        var orchestrator = Build(db.Open(), client, new OneHitSearch(), new TextSource());

        var result = await orchestrator.RunAsync(
            "job-1", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal("no_supported_findings", result.InconclusiveReason);
        Assert.Equal(1, result.EvidenceSnapshots); // evidence was still captured
    }

    [Fact]
    public async Task PlannerFailure_FallsBackToDeterministicPlanAndStillCompletes()
    {
        using var db = new TestDb();
        // First scripted response is NOT a valid plan (missing "queries") → planner fails →
        // deterministic fallback query is used. The remaining responses drive the workflow.
        var client = new ScriptedClient(
            "{\"unexpected\":true}",
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"PostgreSQL uses MVCC\",\"evidence\":[\"p1\"],\"limitations\":\"\"}]}",
            "{\"challenges\":[]}",
            "{\"summary\":\"PostgreSQL is a capable database.\",\"findingIds\":[\"f1\"],\"disposition\":\"supported\"}");
        var orchestrator = Build(db.Open(), client, new OneHitSearch(), new TextSource());

        var result = await orchestrator.RunAsync(
            "job-fallback", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.True(result.Completed, result.InconclusiveReason);
        Assert.Equal(1, result.EvidenceSnapshots);
    }

    [Fact]
    public async Task ComparisonPlan_GroundsEvidenceFromBothEntities()
    {
        using var db = new TestDb();
        var client = new ScriptedClient(
            "{\"queries\":[\"PostgreSQL\",\"MySQL\"],\"comparison\":true}",
            "{\"findings\":[" +
                "{\"id\":\"f1\",\"statement\":\"PostgreSQL uses MVCC\",\"evidence\":[\"p1\"],\"limitations\":\"\"}," +
                "{\"id\":\"f2\",\"statement\":\"MySQL uses pluggable storage engines\",\"evidence\":[\"p2\"],\"limitations\":\"\"}]}",
            "{\"challenges\":[]}",
            "{\"summary\":\"Both are capable relational databases.\",\"findingIds\":[\"f1\",\"f2\"],\"disposition\":\"supported\"}");
        var orchestrator = Build(db.Open(), client, new TwoEntitySearch(), new TwoEntitySource());

        var result = await orchestrator.RunAsync(
            "job-cmp", 1, "Compare PostgreSQL and MySQL", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.True(result.Completed, result.InconclusiveReason);
        Assert.Equal(2, result.EvidenceSnapshots); // one snapshot per entity — balanced acquisition

        // The researcher was given evidence from BOTH entities, not just the first article.
        var researcherPrompt = client.Requests.Single(r => r.AgentRole == "researcher").Prompt;
        Assert.Contains("MVCC", researcherPrompt);           // PostgreSQL passage reached the model
        Assert.Contains("storage engine", researcherPrompt); // MySQL passage reached the model

        Assert.Contains("f1", result.AcceptedFindingIds);
        Assert.Contains("f2", result.AcceptedFindingIds);
    }

    [Fact]
    public async Task OneSourceFetchFailure_StillCompletesOnTheHealthySource()
    {
        using var db = new TestDb();
        var client = new ScriptedClient(
            "{\"queries\":[\"PostgreSQL\",\"MySQL\"],\"comparison\":true}",
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"PostgreSQL uses MVCC\",\"evidence\":[\"p1\"],\"limitations\":\"\"}]}",
            "{\"challenges\":[]}",
            "{\"summary\":\"PostgreSQL uses MVCC.\",\"findingIds\":[\"f1\"],\"disposition\":\"supported\"}");
        // Two queries → fetch order [PostgreSQL, MySQL]; the MySQL fetch throws and is skipped.
        var orchestrator = Build(db.Open(), client, new TwoEntitySearch(), new FlakySource());

        var result = await orchestrator.RunAsync(
            "job-flaky", 1, "Compare PostgreSQL and MySQL", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.True(result.Completed, result.InconclusiveReason);
        Assert.Equal(1, result.EvidenceSnapshots); // only the healthy source produced evidence
    }
}
