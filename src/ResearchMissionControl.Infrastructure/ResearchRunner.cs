using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

// Executes a claimed job's research using the real orchestrator: per-job SQLite journal,
// the governed Ollama inference gateway, and the Wikipedia evidence source. Maps the
// orchestrator result to the cloud-facing outcome (an honest "inconclusive" still
// completes the job).
public sealed class ResearchRunner : IResearchRunner
{
    private readonly string _dbDirectory;
    private readonly string _modelId;
    private readonly string _ollamaBaseUrl;

    public ResearchRunner(string dbDirectory, string modelId, string ollamaBaseUrl = "http://localhost:11434")
    {
        _dbDirectory = dbDirectory;
        _modelId = modelId;
        _ollamaBaseUrl = ollamaBaseUrl;
    }

    public async Task<ResearchRunOutcome> RunAsync(
        string jobId,
        long generation,
        string question,
        string executionProfile,
        CancellationToken cancellationToken)
    {
        var budget = Budgets.For(MapProfile(executionProfile));
        var dbPath = Path.Combine(_dbDirectory, $"job-{jobId}.sqlite");

        using var connection = WorkerDatabase.Open(dbPath);
        using var inference = new OllamaInferenceClient(_ollamaBaseUrl);
        using var wikipedia = new WikipediaSourceAdapter();

        var registry = new ModelRegistry(new[] { new ApprovedModel(_modelId, null) });
        var gateway = new InferenceGateway(inference, registry, new OperationJournalStore(connection), new ReservationStore(connection));
        var evidence = new EvidenceStore(connection);
        var acquisition = new EvidenceAcquisition(wikipedia, evidence, ToolRegistry.Default());
        var orchestrator = new ResearchOrchestrator(gateway, wikipedia, acquisition, evidence, _modelId);

        var result = await orchestrator.RunAsync(jobId, generation, question, budget, cancellationToken).ConfigureAwait(false);

        var report = new ReportSummary(
            result.Report?.Question ?? question,
            result.Report?.ExecutiveSummary ?? string.Empty,
            result.FindingStatements,
            result.Disposition,
            result.Completed ? null : result.InconclusiveReason);

        return new ResearchRunOutcome(result.Disposition, result.Completed ? null : result.InconclusiveReason, report);
    }

    private static ExecutionProfile MapProfile(string profile) => profile switch
    {
        "quick" => ExecutionProfile.Quick,
        "extended" => ExecutionProfile.Extended,
        _ => ExecutionProfile.Standard,
    };
}
