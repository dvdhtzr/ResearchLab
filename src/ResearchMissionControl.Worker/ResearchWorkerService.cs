using Microsoft.Extensions.Hosting;
using ResearchMissionControl.Infrastructure;

namespace ResearchMissionControl.Worker;

// The hosted poll loop: claim a job, run it, repeat; sleep when the queue is empty.
public sealed class ResearchWorkerService : BackgroundService
{
    private readonly WorkerLoop _loop;
    private readonly TimeSpan _pollInterval;

    public ResearchWorkerService(WorkerLoop loop, TimeSpan pollInterval)
    {
        _loop = loop;
        _pollInterval = pollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork;
            try
            {
                didWork = await _loop.ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[worker] loop error: {ex.Message}");
                didWork = false;
            }

            if (!didWork)
            {
                try
                {
                    await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
