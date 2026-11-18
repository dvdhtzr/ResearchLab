namespace ResearchMissionControl.Application;

// A single verbose progress update emitted by the research workflow. Detail is an optional
// structured payload (serialized to JSON for the cloud; rendered for the console).
public sealed record ProgressUpdate(string Stage, string Message, string Level = "info", object? Detail = null);

// Sink for verbose progress. The orchestrator reports to it at each workflow stage;
// implementations forward to the cloud and/or the console. Best-effort by contract — an
// implementation must never throw into the research workflow.
public interface IProgressSink
{
    Task ReportAsync(ProgressUpdate update, CancellationToken cancellationToken);
}

// No-op sink: the default when no verbose reporting is wired, and for model-free tests.
public sealed class NullProgressSink : IProgressSink
{
    public static readonly NullProgressSink Instance = new();
    public Task ReportAsync(ProgressUpdate update, CancellationToken cancellationToken) => Task.CompletedTask;
}
