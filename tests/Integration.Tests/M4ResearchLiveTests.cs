using Microsoft.Data.Sqlite;
using ResearchMissionControl.Application;
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

    [Fact]
    public async Task ComparisonQuestion_GroundsEvidenceFromBothEntities()
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
            "job-live-cmp", 1, "Compare PostgreSQL and MySQL", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        _output.WriteLine($"completed={result.Completed} disposition={result.Disposition} " +
                          $"evidence={result.EvidenceSnapshots} attempts={result.ModelAttempts} " +
                          $"reason={result.InconclusiveReason}");
        if (result.Report is not null)
            _output.WriteLine($"summary: {result.Report.ExecutiveSummary}");

        // Deterministic: the workflow ran and captured real Wikipedia evidence.
        Assert.True(result.EvidenceSnapshots >= 1, "expected at least one evidence snapshot");
        if (result.Completed)
        {
            Assert.NotNull(result.Report);
            Assert.NotEmpty(result.AcceptedFindingIds);
        }
        // Strong signal of the model-assisted planner: the comparison was split per entity, so
        // evidence was gathered from BOTH articles (prints the reason above if it does not hold).
        Assert.True(result.EvidenceSnapshots >= 2,
            $"expected balanced evidence from both entities, got {result.EvidenceSnapshots} snapshot(s): {result.InconclusiveReason}");
        Assert.True(result.Completed, $"comparison research did not complete: {result.InconclusiveReason}");
    }

    [Fact]
    public async Task MultiSource_GroundsOnWikipediaAndWikidata()
    {
        if (!Enabled) return;

        using var conn = NewDb();
        using var client = new OllamaInferenceClient();
        using var wikipedia = new WikipediaSourceAdapter();
        using var wikidata = new WikidataSourceAdapter();

        var registry = new ModelRegistry(new[] { new ApprovedModel("qwen3.5:9b", null) });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var evidence = new EvidenceStore(conn);

        // Two approved free sources behind the governed acquisition layer.
        var search = new CompositeSourceSearch(new ISourceSearch[] { wikipedia, wikidata });
        var sources = new CompositeSourceAdapter(new Dictionary<string, ISourceAdapter>
        {
            [WikipediaSourceAdapter.AllowedHost] = wikipedia,
            [WikidataSourceAdapter.AllowedHost] = wikidata,
        });
        var acquisition = new EvidenceAcquisition(sources, evidence, ToolRegistry.Default());
        var orchestrator = new ResearchOrchestrator(gateway, search, acquisition, evidence, "qwen3.5:9b");

        var result = await orchestrator.RunAsync(
            "job-live-multi", 1, "What is PostgreSQL?", Budgets.For(ExecutionProfile.Standard), CancellationToken.None);

        _output.WriteLine($"completed={result.Completed} disposition={result.Disposition} " +
                          $"evidence={result.EvidenceSnapshots} attempts={result.ModelAttempts} " +
                          $"reason={result.InconclusiveReason}");
        if (result.Report is not null)
            _output.WriteLine($"summary: {result.Report.ExecutiveSummary}");

        // Deterministic: the workflow ran and captured real evidence.
        Assert.True(result.EvidenceSnapshots >= 1, "expected at least one evidence snapshot");
        // Strong signal: evidence gathered from BOTH sources (Wikipedia prose + Wikidata facts)
        // for a single entity — the multi-source merge fetched one document from each.
        Assert.True(result.EvidenceSnapshots >= 2,
            $"expected multi-source evidence (Wikipedia + Wikidata), got {result.EvidenceSnapshots}: {result.InconclusiveReason}");
        Assert.True(result.Completed, $"multi-source research did not complete: {result.InconclusiveReason}");
    }

    [Fact]
    public async Task ArxivAdapter_SearchesAndFetchesARealAbstract()
    {
        if (!Enabled) return;

        using var arxiv = new ArxivSourceAdapter();

        var hits = await arxiv.SearchAsync("attention is all you need transformer", 3, CancellationToken.None);
        _output.WriteLine($"arxiv hits={hits.Count}");
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.StartsWith("https://export.arxiv.org/abs/", hit.Url));

        var document = await arxiv.FetchAsync(hits[0].Url, CancellationToken.None);
        _output.WriteLine($"arxiv title={document.Title}");
        _output.WriteLine($"arxiv text (first 160): {document.Text[..System.Math.Min(160, document.Text.Length)]}");

        Assert.StartsWith("arxiv:", document.SourceIdentity);
        Assert.Equal("CC0 1.0", document.Rights);
        Assert.Contains("Abstract:", document.Text); // a real abstract was captured
    }
}
