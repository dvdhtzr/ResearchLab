using Microsoft.Data.Sqlite;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Integration.Tests;

// M3 phase B — live-model tests. They run ONLY when RMC_OLLAMA_TESTS=1 (set by
// scripts/check-ollama.ps1) and require a local Ollama with qwen3.5:9b installed.
// When the gate is off they no-op so they never fail an ordinary test run.
public sealed class M3OllamaLiveTests
{
    private const string Model = "qwen3.5:9b";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("RMC_OLLAMA_TESTS") == "1";

    private static SqliteConnection NewDb() =>
        WorkerDatabase.Open(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rmc-live-{Guid.NewGuid():N}.sqlite"));

    [Fact]
    public async Task InstalledModel_DigestResolves()
    {
        if (!Enabled) return;
        using var client = new OllamaInferenceClient();
        var digest = await client.GetInstalledDigestAsync(Model, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(digest));
    }

    [Fact]
    public async Task MissingModel_IsModelUnavailable()
    {
        if (!Enabled) return;
        using var conn = NewDb();
        using var client = new OllamaInferenceClient();
        var registry = new ModelRegistry(new[] { new ApprovedModel("does-not-exist:0b", null) });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var request = new InferenceRequest("job-live", 1, "does-not-exist:0b", "researcher", "plan", "hi", new[] { "answer" });

        var outcome = await gateway.ExecuteAsync(request, 1, Budgets.For(ExecutionProfile.Quick), 0, CancellationToken.None);

        Assert.Equal(InferenceFailureCategory.ModelUnavailable, outcome.Failure);
    }

    [Fact]
    public async Task StructuredResponse_IsValidJson()
    {
        if (!Enabled) return;
        using var conn = NewDb();
        using var client = new OllamaInferenceClient();
        // Digest not pinned for this smoke; the registry just approves the installed model.
        var registry = new ModelRegistry(new[] { new ApprovedModel(Model, null) });
        var gateway = new InferenceGateway(client, registry, new OperationJournalStore(conn), new ReservationStore(conn));
        var prompt =
            "Return a JSON object with a single property \"answer\" whose value is the capital of France. " +
            "Respond with only JSON.";
        var request = new InferenceRequest("job-live", 1, Model, "researcher", "plan", prompt, new[] { "answer" });

        var outcome = await gateway.ExecuteAsync(request, 1, Budgets.For(ExecutionProfile.Quick), 0, CancellationToken.None);

        Assert.True(outcome.Success, $"expected success; got failure category {outcome.Failure}");
        Assert.NotNull(outcome.Json);
    }
}
