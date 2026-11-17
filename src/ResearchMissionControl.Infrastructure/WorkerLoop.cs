using ResearchMissionControl.Application;

namespace ResearchMissionControl.Infrastructure;

// The claim→start→run→complete loop. Depends only on the ports, so it is tested
// model-free. A research outcome (including an honest "inconclusive") completes the job;
// only an execution exception fails it (the cloud then allows owner-controlled recovery).
public sealed class WorkerLoop
{
    private readonly ICloudApiClient _cloud;
    private readonly IResearchRunner _runner;
    private readonly TimeSpan _heartbeatInterval;
    private readonly Action<string>? _log;

    public WorkerLoop(
        ICloudApiClient cloud,
        IResearchRunner runner,
        TimeSpan? heartbeatInterval = null,
        Action<string>? log = null)
    {
        _cloud = cloud;
        _runner = runner;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
        _log = log;
    }

    // Claims a single job and drives it to a terminal cloud state. Returns true if work
    // was found, false if the queue was empty.
    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var job = await _cloud.ClaimAsync(cancellationToken).ConfigureAwait(false);
        if (job is null)
            return false;
        _log?.Invoke($"claimed {job.JobId} (generation {job.Generation})");

        try
        {
            await _cloud.StartAsync(job.JobId, job.Generation, job.LeaseId, cancellationToken).ConfigureAwait(false);

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = HeartbeatAsync(job, heartbeatCts.Token);

            ResearchRunOutcome outcome;
            try
            {
                outcome = await _runner
                    .RunAsync(job.JobId, job.Generation, job.Question, job.ExecutionProfile, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                heartbeatCts.Cancel();
                await Quiet(heartbeat).ConfigureAwait(false);
            }

            await _cloud.CompleteAsync(job.JobId, job.Generation, job.LeaseId, outcome.Disposition, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"completed {job.JobId} (disposition {outcome.Disposition})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"failing {job.JobId}: {ex.Message}");
            try
            {
                await _cloud.FailAsync(job.JobId, job.Generation, job.LeaseId, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best effort; if the fail call cannot be delivered, the lease expires into
                // RecoveryRequired and the owner decides.
            }
        }
        return true;
    }

    private async Task HeartbeatAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                await _cloud.HeartbeatAsync(job.JobId, job.Generation, job.LeaseId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on completion
        }
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
}
