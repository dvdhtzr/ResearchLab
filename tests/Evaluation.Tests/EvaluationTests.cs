using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Evaluation;
using Xunit;

namespace ResearchMissionControl.Evaluation.Tests;

// M5 — three evaluation lanes, verified model-free. Evaluation is INDEPENDENT of
// execution: the evaluators take a completed research record and never mutate it.
public sealed class EvaluationTests
{
    private sealed class ScriptedClient : IInferenceClient
    {
        private readonly Queue<string> _responses;
        public ScriptedClient(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("digest");

        public Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "{}");
    }

    private static readonly BudgetSnapshot Budget = Budgets.For(ExecutionProfile.Standard);

    private static (ResearchReport Report, List<Finding> Findings, HashSet<string> Passages) GroundedRecord()
    {
        var passages = new HashSet<string>(StringComparer.Ordinal) { "p1", "p2" };
        var findings = new List<Finding>
        {
            new("f1", "PostgreSQL uses MVCC", new[] { "p1" }, "one source"),
            new("f2", "SQL Server supports snapshot isolation", new[] { "p2" }, ""),
        };
        var report = new ResearchReport("Compare them", "Both are capable.", new[] { "f1", "f2" }, "supported");
        return (report, findings, passages);
    }

    // ---- Lane 1: technical integrity ----

    [Fact]
    public void TechnicalIntegrity_PassesForAGroundedRecord()
    {
        var (report, findings, passages) = GroundedRecord();
        var result = TechnicalIntegrityEvaluator.Evaluate(report, findings, 3, Budget, passages.Contains);
        Assert.True(result.AllPassed);
    }

    [Fact]
    public void TechnicalIntegrity_FailsWhenAFindingCitesMissingEvidence()
    {
        var (report, _, passages) = GroundedRecord();
        var badFindings = new List<Finding> { new("f1", "Fabricated", new[] { "p-missing" }, "") };
        var result = TechnicalIntegrityEvaluator.Evaluate(report, badFindings, 3, Budget, passages.Contains);
        Assert.False(result.AllPassed);
        Assert.Contains(result.Checks, c => c.Name == "reference_integrity" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void TechnicalIntegrity_FailsWhenBudgetExceeded()
    {
        var (report, findings, passages) = GroundedRecord();
        var result = TechnicalIntegrityEvaluator.Evaluate(report, findings, Budget.ModelAttempts + 1, Budget, passages.Contains);
        Assert.False(result.AllPassed);
        Assert.Contains(result.Checks, c => c.Name == "budget_enforced" && c.Status == CheckStatus.Fail);
    }

    // ---- Lane 2: semantic (local-model-assisted; scripted here) ----

    [Fact]
    public async Task Semantic_MapsModelAssessmentsAndPreservesUncertainty()
    {
        var (_, findings, _) = GroundedRecord();
        var client = new ScriptedClient(
            "{\"assessment\":\"supported\"}",
            "{\"assessment\":\"not-a-real-label\"}"); // out-of-vocabulary → Unknown (uncertainty preserved)
        var evaluator = new SemanticEvaluator(client, "qwen3.5:9b");

        var results = await evaluator.EvaluateAsync(findings, _ => "evidence text", 1, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(ClaimAssessment.Supported, results[0].Assessment);
        Assert.Equal(ClaimAssessment.Unknown, results[1].Assessment);
    }

    [Fact]
    public async Task Semantic_TransportFailureBecomesUnknown_NotAVerdict()
    {
        var (_, findings, _) = GroundedRecord();
        var evaluator = new SemanticEvaluator(new ThrowingClient(), "qwen3.5:9b");
        var results = await evaluator.EvaluateAsync(findings, _ => "e", 1, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(ClaimAssessment.Unknown, r.Assessment));
    }

    private sealed class ThrowingClient : IInferenceClient
    {
        public Task<string?> GetInstalledDigestAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("digest");
        public Task<string> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken) =>
            throw new InferenceTransportException("down");
    }

    // ---- Lane 3: evaluation laboratory (frozen fixtures, labeled provisional) ----

    [Fact]
    public void Laboratory_FrozenCase_ComparesDispositionAndStaysProvisional()
    {
        var (report, findings, passages) = GroundedRecord();
        var frozen = new FrozenCase("case-sqlvspg", new[] { "p1", "p2" }, "supported", Provisional: true);

        var result = LaboratoryEvaluator.Evaluate(frozen, report, findings, 3, Budget, passages.Contains);

        Assert.True(result.TechnicalPass);
        Assert.True(result.DispositionMatches);
        Assert.True(result.Provisional); // synthetic fixtures remain provisional until independently reviewed
    }

    [Fact]
    public void Laboratory_DetectsDispositionMismatch()
    {
        var (report, findings, passages) = GroundedRecord(); // disposition "supported"
        var frozen = new FrozenCase("case-x", new[] { "p1" }, "inconclusive", Provisional: true);
        var result = LaboratoryEvaluator.Evaluate(frozen, report, findings, 3, Budget, passages.Contains);
        Assert.False(result.DispositionMatches);
    }
}
