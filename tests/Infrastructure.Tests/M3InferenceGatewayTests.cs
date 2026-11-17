using Microsoft.Data.Sqlite;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// M3 phase A (model-free): the governed inference gateway, exercised with a fake
// IInferenceClient. No Ollama. The live-model test is gated behind RMC_OLLAMA_TESTS (M3-b).
public sealed class M3InferenceGatewayTests
{
    private sealed class FakeClient : IInferenceClient
    {
        public string? Digest { get; set; } = "digest-abc";
        public Func<InferenceRequest, string> Respond { get; set; } = _ => "{\"ok\":true}";
        public Exception? ThrowOnComplete { get; set; }

        public Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult(Digest);

        public Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnComplete is not null)
                throw ThrowOnComplete;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Respond(request));
        }
    }

    private static InferenceRequest Request(long generation = 1) =>
        new("job-1", generation, "qwen3.5:9b", "researcher", "plan", "prompt", new[] { "ok" });

    private static BudgetSnapshot Budget => Budgets.For(ExecutionProfile.Standard);

    private static (InferenceGateway Gateway, OperationJournalStore Journal, ReservationStore Reservations) Build(
        SqliteConnection connection, FakeClient client)
    {
        var journal = new OperationJournalStore(connection);
        var reservations = new ReservationStore(connection);
        var registry = new ModelRegistry(new[] { new ApprovedModel("qwen3.5:9b", "digest-abc") });
        var gateway = new InferenceGateway(client, registry, journal, reservations);
        return (gateway, journal, reservations);
    }

    [Fact]
    public async Task Success_CommitsAndConsumes()
    {
        using var db = new TestDb();
        var (gateway, journal, reservations) = Build(db.Open(), new FakeClient());
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, reservations.CountInferenceAttempts("job-1"));
        Assert.Equal(0, journal.CountInterrupted("job-1")); // committed, not left open
    }

    [Fact]
    public async Task BudgetExhausted_DoesNotReserve()
    {
        using var db = new TestDb();
        var (gateway, _, reservations) = Build(db.Open(), new FakeClient());
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, Budget.ModelAttempts, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.BudgetExhausted, outcome.Failure);
        Assert.Equal(0, reservations.CountInferenceAttempts("job-1"));
    }

    [Fact]
    public async Task DigestMismatch_IsRejected()
    {
        using var db = new TestDb();
        var (gateway, _, reservations) = Build(db.Open(), new FakeClient { Digest = "WRONG" });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.DigestMismatch, outcome.Failure);
        Assert.Equal(0, reservations.CountInferenceAttempts("job-1")); // rejected before reserving
    }

    [Fact]
    public async Task ModelNotInstalled_IsRejected()
    {
        using var db = new TestDb();
        var (gateway, _, _) = Build(db.Open(), new FakeClient { Digest = null });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.ModelUnavailable, outcome.Failure);
    }

    [Fact]
    public async Task InvalidJson_IsFinalizedNotFabricated()
    {
        using var db = new TestDb();
        var (gateway, journal, _) = Build(db.Open(), new FakeClient { Respond = _ => "not json" });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.InvalidJson, outcome.Failure);
        Assert.Equal(0, journal.CountInterrupted("job-1")); // finalized as failed, not left NULL
    }

    [Fact]
    public async Task InvalidSchema_MissingRequiredProperty()
    {
        using var db = new TestDb();
        var (gateway, _, _) = Build(db.Open(), new FakeClient { Respond = _ => "{\"other\":1}" });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.InvalidSchema, outcome.Failure);
    }

    [Fact]
    public async Task TransportError_IsRecorded()
    {
        using var db = new TestDb();
        var (gateway, _, _) = Build(db.Open(), new FakeClient { ThrowOnComplete = new InferenceTransportException("down") });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.TransportError, outcome.Failure);
    }

    [Fact]
    public async Task Timeout_IsRecorded()
    {
        using var db = new TestDb();
        var (gateway, _, _) = Build(db.Open(), new FakeClient { ThrowOnComplete = new TimeoutException() });
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.Timeout, outcome.Failure);
    }

    [Fact]
    public async Task Cancelled_IsRecorded()
    {
        using var db = new TestDb();
        var (gateway, _, _) = Build(db.Open(), new FakeClient());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await gateway.ExecuteAsync(Request(), 1, Budget, 0, cts.Token);

        Assert.Equal(InferenceFailureCategory.Cancelled, outcome.Failure);
    }

    [Fact]
    public async Task StaleGeneration_Refused()
    {
        using var db = new TestDb();
        var (gateway, _, reservations) = Build(db.Open(), new FakeClient());
        // holder holds generation 1 but the job's current generation is 2
        var outcome = await gateway.ExecuteAsync(Request(generation: 1), 2, Budget, 0, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(InferenceFailureCategory.InterruptedUnknown, outcome.Failure);
        Assert.Equal(0, reservations.CountInferenceAttempts("job-1"));
    }
}
