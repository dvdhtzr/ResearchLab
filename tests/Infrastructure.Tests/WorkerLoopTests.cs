using ResearchMissionControl.Application;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// Model-free: the worker poll loop drives claim → start → run → complete/fail using the
// ports, with no HTTP or Ollama.
public sealed class WorkerLoopTests
{
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromHours(1);

    private sealed class FakeCloud : ICloudApiClient
    {
        private readonly Queue<ClaimedJob?> _claims;
        public FakeCloud(params ClaimedJob?[] claims) => _claims = new Queue<ClaimedJob?>(claims);
        public List<string> Calls { get; } = new();
        public string? CompletedDisposition { get; private set; }
        public string? FailedReason { get; private set; }

        public Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_claims.Count > 0 ? _claims.Dequeue() : null);
        public Task StartAsync(string j, long g, string l, CancellationToken ct) { Calls.Add("start"); return Task.CompletedTask; }
        public Task HeartbeatAsync(string j, long g, string l, CancellationToken ct) { Calls.Add("heartbeat"); return Task.CompletedTask; }
        public Task CompleteAsync(string j, long g, string l, string disposition, ReportSummary? report, CancellationToken ct) { Calls.Add("complete"); CompletedDisposition = disposition; return Task.CompletedTask; }
        public Task FailAsync(string j, long g, string l, string reason, CancellationToken ct) { Calls.Add("fail"); FailedReason = reason; return Task.CompletedTask; }
        public Task ReportProgressAsync(string j, long g, string l, IReadOnlyList<ProgressUpdate> u, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedRunner : IResearchRunner
    {
        private readonly ResearchRunOutcome _outcome;
        private readonly bool _throw;
        public FixedRunner(ResearchRunOutcome outcome, bool doThrow = false) { _outcome = outcome; _throw = doThrow; }
        public Task<ResearchRunOutcome> RunAsync(string j, long g, string q, string p, IProgressSink progress, CancellationToken ct)
        {
            if (_throw) throw new InvalidOperationException("boom");
            return Task.FromResult(_outcome);
        }
    }

    private static ClaimedJob Job() => new("job-1", 1, "lease-1", "What is PostgreSQL?", "hybrid-governed", "standard");

    [Fact]
    public async Task Claim_Start_Run_Complete()
    {
        var cloud = new FakeCloud(Job(), null);
        var loop = new WorkerLoop(cloud, new FixedRunner(new ResearchRunOutcome("supported", null)), NoHeartbeat);

        Assert.True(await loop.ProcessOnceAsync(CancellationToken.None));
        Assert.Contains("start", cloud.Calls);
        Assert.Contains("complete", cloud.Calls);
        Assert.Equal("supported", cloud.CompletedDisposition);

        Assert.False(await loop.ProcessOnceAsync(CancellationToken.None)); // queue empty
    }

    [Fact]
    public async Task Inconclusive_StillCompletes_NotFails()
    {
        var cloud = new FakeCloud(Job());
        var loop = new WorkerLoop(cloud, new FixedRunner(new ResearchRunOutcome("inconclusive", "no_supported_findings")), NoHeartbeat);

        await loop.ProcessOnceAsync(CancellationToken.None);
        Assert.Equal("inconclusive", cloud.CompletedDisposition);
        Assert.DoesNotContain("fail", cloud.Calls);
    }

    private sealed class ClaimThrowsCloud : ICloudApiClient
    {
        public List<string> Calls { get; } = new();
        public Task<ClaimedJob?> ClaimAsync(CancellationToken ct) => throw new InvalidOperationException("connection reset");
        public Task StartAsync(string j, long g, string l, CancellationToken ct) { Calls.Add("start"); return Task.CompletedTask; }
        public Task HeartbeatAsync(string j, long g, string l, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string j, long g, string l, string d, ReportSummary? report, CancellationToken ct) { Calls.Add("complete"); return Task.CompletedTask; }
        public Task FailAsync(string j, long g, string l, string r, CancellationToken ct) { Calls.Add("fail"); return Task.CompletedTask; }
        public Task ReportProgressAsync(string j, long g, string l, IReadOnlyList<ProgressUpdate> u, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ClaimTransportError_IsBenign_NoWork()
    {
        var cloud = new ClaimThrowsCloud();
        var loop = new WorkerLoop(cloud, new FixedRunner(new ResearchRunOutcome("supported", null)), NoHeartbeat);
        Assert.False(await loop.ProcessOnceAsync(CancellationToken.None)); // treated as "no work", retried next tick
        Assert.Empty(cloud.Calls);
    }

    [Fact]
    public async Task RunnerException_FailsTheJob()
    {
        var cloud = new FakeCloud(Job());
        var loop = new WorkerLoop(cloud, new FixedRunner(new ResearchRunOutcome("x", null), doThrow: true), NoHeartbeat);

        await loop.ProcessOnceAsync(CancellationToken.None);
        Assert.Contains("fail", cloud.Calls);
        Assert.DoesNotContain("complete", cloud.Calls);
    }
}
