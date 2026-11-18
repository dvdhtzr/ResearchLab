using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// Forwards verbose progress to the cloud (the lease-fenced progress endpoint) and to the
// console. Best-effort: a failed post is logged and swallowed so verbose reporting never
// disrupts a research run.
public sealed class CloudProgressSink : IProgressSink
{
    private readonly ICloudApiClient _cloud;
    private readonly string _jobId;
    private readonly long _generation;
    private readonly string _leaseId;
    private readonly Action<string>? _log;

    public CloudProgressSink(ICloudApiClient cloud, string jobId, long generation, string leaseId, Action<string>? log = null)
    {
        _cloud = cloud;
        _jobId = jobId;
        _generation = generation;
        _leaseId = leaseId;
        _log = log;
    }

    public async Task ReportAsync(ProgressUpdate update, CancellationToken cancellationToken)
    {
        _log?.Invoke($"  · [{update.Stage}] {update.Message}");
        try
        {
            await _cloud.ReportProgressAsync(_jobId, _generation, _leaseId, new[] { update }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"  · (progress post failed: {ex.Message})");
        }
    }
}
