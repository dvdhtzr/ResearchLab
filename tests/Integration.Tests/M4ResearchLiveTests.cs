using Microsoft.Data.Sqlite;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace ResearchMissionControl.Integration.Tests;

// M4 phase B — live research workflow. Runs ONLY when RMC_OLLAMA_TESTS=1 and requires
// a local Ollama (qwen3.5:9b) AND outbound HTTPS to en.wikipedia.org. Drives the full
// Planner→Researcher→Skeptic→Report loop over real evidence.
public sealed class M4ResearchLiveTests
{
    private readonly ITestOutputHelper _output;
    public M4ResearchLiveTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("RMC_OLLAMA_TESTS") == "1";

    private static SqliteConnection NewDb() =>
        WorkerDatabase.Open(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rmc-m4-{Guid.NewGuid():N}.sqlite"));

    [Fact]
    public async Task ResearchWorkflow_ProducesEvidenceGroundedReport()
    {
        if (!Enabled) return;

        using var conn = NewDb();
        using var client = new OllamaInferenceClient();
        using var wikipedia = new WikipediaSourceAdapter();

        var registry = new ModelRegistry(new[] { new ApprovedModel("qwen3.5:9b", null) });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var evidence = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(wikipedia, evidence, ToolRegistry.Default());
        var orchestrator = new ResearchOrchestrator(gateway, wikipedia, acquisition, evidence, "qwen3.5:9b");

        var result = await orchestrator.RunAsync(
            "job-live", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Quick), CancellationToken.None);

        _output.WriteLine($"completed={result.Completed} disposition={result.Disposition} " +
                          $"evidence={result.EvidenceSnapshots} attempts={result.ModelAttempts} " +
                          $"reason={result.InconclusiveReason}");
        if (result.Report is not null)
            _output.WriteLine($"summary: {result.Report.ExecutiveSummary}");

        // Deterministic: the workflow ran end-to-end and captured real Wikipedia evidence.
        Assert.True(result.EvidenceSnapshots >= 1, "expected at least one evidence snapshot");
        // Consistency: when it completes, the report is evidence-grounded.
        if (result.Completed)
        {
            Assert.NotNull(result.Report);
            Assert.NotEmpty(result.AcceptedFindingIds);
        }
        // Strong signal (may surface model variability — reason is printed above if it fails).
        Assert.True(result.Completed, $"research did not complete: {result.InconclusiveReason}");
    }
}
