namespace ResearchMissionControl.Application;

// A job claimed from the cloud API, with the research request the worker must execute.
public sealed record ClaimedJob(
    string JobId,
    long Generation,
    string LeaseId,
    string Question,
    string EvidencePolicy,
    string ExecutionProfile);

// Port over the authoritative cloud Worker API. The cloud owns state; this only reports.
public interface ICloudApiClient
{
    Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken);
    Task StartAsync(string jobId, long generation, string leaseId, CancellationToken cancellationToken);
    Task HeartbeatAsync(string jobId, long generation, string leaseId, CancellationToken cancellationToken);
    Task CompleteAsync(string jobId, long generation, string leaseId, string disposition, CancellationToken cancellationToken);
    Task FailAsync(string jobId, long generation, string leaseId, string reason, CancellationToken cancellationToken);
}

public sealed record ResearchRunOutcome(string Disposition, string? FailureReason);

// Port over the research execution (the orchestrator). Keeps the worker loop free of
// Ollama / SQLite / HTTP so it can be tested model-free.
public interface IResearchRunner
{
    Task<ResearchRunOutcome> RunAsync(
        string jobId,
        long generation,
        string question,
        string executionProfile,
        CancellationToken cancellationToken);
}
