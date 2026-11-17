using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// M4 phase B (model-free): the full orchestrator driven by a scripted inference client
// and fake source — verifies the Plan→Researcher→Skeptic→Report workflow and its
// governance without Ollama. The live equivalent is gated in Integration.Tests.
public sealed class M4OrchestratorTests
{
    private sealed class ScriptedClient : IInferenceClient
    {
        private readonly Queue<string> _responses;
        public ScriptedClient(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("digest-abc");

        public Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "{}");
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

    private static ResearchOrchestrator Build(Microsoft.Data.Sqlite.SqliteConnection conn, params string[] scripted)
    {
        var client = new ScriptedClient(scripted);
        var registry = new ModelRegistry(new[] { new ApprovedModel("qwen3.5:9b", "digest-abc") });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var evidence = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(new TextSource(), evidence, ToolRegistry.Default());
        return new ResearchOrchestrator(gateway, new OneHitSearch(), acquisition, evidence, "qwen3.5:9b");
    }

    [Fact]
    public async Task CompletesWithEvidenceGroundedReport()
    {
        using var db = new TestDb();
        var orchestrator = Build(
            db.Open(),
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"PostgreSQL uses MVCC\",\"evidence\":[\"p1\"],\"limitations\":\"single source\"}]}",
            "{\"challenges\":[]}",
            "{\"summary\":\"PostgreSQL is a capable database.\",\"findingIds\":[\"f1\"],\"disposition\":\"supported\"}");

        var result = await orchestrator.RunAsync(
            "job-1", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.True(result.Completed, result.InconclusiveReason);
        Assert.NotNull(result.Report);
        Assert.Contains("f1", result.Report!.CitedFindingIds);
        Assert.Contains("f1", result.AcceptedFindingIds);
        Assert.Equal(1, result.EvidenceSnapshots);
        Assert.Equal("supported", result.Disposition);
    }

    [Fact]
    public async Task UngroundedFindings_YieldInconclusive()
    {
        using var db = new TestDb();
        // The researcher cites a passage label that was never provided → rejected.
        var orchestrator = Build(
            db.Open(),
            "{\"findings\":[{\"id\":\"f1\",\"statement\":\"Fabricated\",\"evidence\":[\"p9\"],\"limitations\":\"\"}]}");

        var result = await orchestrator.RunAsync(
            "job-1", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal("no_supported_findings", result.InconclusiveReason);
        Assert.Equal(1, result.EvidenceSnapshots); // evidence was still captured
    }
}
