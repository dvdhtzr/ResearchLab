using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// M4 phase A (model-free): evidence governance, tool authorization, role-output validators.
public sealed class M4GovernanceTests
{
    private sealed class FakeSource : ISourceAdapter
    {
        public int Calls { get; private set; }
        public string Text { get; set; } = "Alpha beta gamma delta. Epsilon zeta eta theta.";

        public Task<SourceDocument> FetchAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SourceDocument(url, "Fake Title", Text, "fake:source"));
        }
    }

    [Fact]
    public async Task UnauthorizedTool_IsNeverFetched()
    {
        using var db = new TestDb();
        var conn = db.Open();
        var source = new FakeSource();
        var store = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(source, store, ToolRegistry.Default());

        var proposal = new ToolProposal("filesystem.read.v1", "{\"path\":\"/etc/passwd\"}");
        var result = await acquisition.FetchAsync("job-1", proposal, "https://example.org", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, source.Calls);          // the fetch never ran
        Assert.Equal(0, store.CountSnapshots("job-1"));
    }

    [Fact]
    public async Task AuthorizedTool_CapturesImmutableEvidence()
    {
        using var db = new TestDb();
        var conn = db.Open();
        var store = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(new FakeSource(), store, ToolRegistry.Default());

        var proposal = new ToolProposal("source.fetch.v1", "{\"url\":\"https://example.org\"}");
        var result = await acquisition.FetchAsync("job-1", proposal, "https://example.org", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.PassageIds);
        Assert.Equal(1, store.CountSnapshots("job-1"));
        foreach (var passageId in result.PassageIds)
            Assert.True(store.PassageExists(passageId));
    }

    [Fact]
    public async Task Findings_CitingCapturedEvidence_AreAccepted_UnknownAreRejected()
    {
        using var db = new TestDb();
        var conn = db.Open();
        var store = new EvidenceStore(conn);
        var acquisition = new EvidenceAcquisition(new FakeSource(), store, ToolRegistry.Default());
        var evidence = await acquisition.FetchAsync(
            "job-1", new ToolProposal("source.fetch.v1", "{}"), "https://example.org", CancellationToken.None);
        var realPassage = evidence!.PassageIds[0];

        var findings = new[]
        {
            new Finding("f1", "Grounded claim", new[] { realPassage }, "limited"),
            new Finding("f2", "Hallucinated claim", new[] { "passage-does-not-exist" }, ""),
            new Finding("f3", "No citations", System.Array.Empty<string>(), ""),
        };

        var validation = ResearchValidators.ValidateFindings(findings, store.PassageExists);

        Assert.Single(validation.Accepted);
        Assert.Equal("f1", validation.Accepted[0].FindingId);
        Assert.Contains("f2", validation.RejectedFindingIds);
        Assert.Contains("f3", validation.RejectedFindingIds);
    }

    [Fact]
    public void Challenges_MustTargetCommittedFindings_AndUseAllowedClass()
    {
        var committed = new HashSet<string>(System.StringComparer.Ordinal) { "f1", "f2" };

        Assert.True(ResearchValidators.IsChallengeValid(
            new Challenge("f1", "insufficient_evidence", "needs more"), committed));
        Assert.False(ResearchValidators.IsChallengeValid(
            new Challenge("f9", "insufficient_evidence", "unknown target"), committed));
        Assert.False(ResearchValidators.IsChallengeValid(
            new Challenge("f1", "vibes", "bad class"), committed));
    }

    [Fact]
    public void Report_CannotIntroduceClaimsAbsentFromCommittedFindings()
    {
        var committed = new HashSet<string>(System.StringComparer.Ordinal) { "f1", "f2" };

        var valid = new ResearchReport("Q?", "summary", new[] { "f1", "f2" }, "supported");
        Assert.True(ResearchValidators.IsReportValid(valid, committed));

        var introducesNewClaim = new ResearchReport("Q?", "summary", new[] { "f1", "f3" }, "supported");
        Assert.False(ResearchValidators.IsReportValid(introducesNewClaim, committed));

        var empty = new ResearchReport("Q?", "summary", System.Array.Empty<string>(), "inconclusive");
        Assert.False(ResearchValidators.IsReportValid(empty, committed));
    }

    [Theory]
    [InlineData(2, 0, 3, HostDecision.Revise)]   // challenges + rounds remain → revise
    [InlineData(2, 3, 3, HostDecision.Conclude)] // challenges but no rounds left → conclude
    [InlineData(0, 0, 3, HostDecision.Conclude)] // no challenges → conclude
    public void HostDecision_RespectsChallengesAndRoundBudget(int challenges, int used, int max, HostDecision expected)
    {
        Assert.Equal(expected, ResearchHost.Decide(challenges, used, max));
    }
}
