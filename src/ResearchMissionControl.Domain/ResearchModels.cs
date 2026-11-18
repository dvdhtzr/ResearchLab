namespace ResearchMissionControl.Domain;

// Committed research artifacts (spec §22). The model proposes these as JSON; the host
// validates them against captured evidence — the model never self-certifies.
public sealed record Finding(
    string FindingId,
    string Statement,
    IReadOnlyList<string> EvidencePassageIds,
    string Limitations);

public sealed record Challenge(
    string TargetFindingId,
    string Classification,
    string Explanation);

public sealed record ResearchReport(
    string Question,
    string ExecutiveSummary,
    IReadOnlyList<string> CitedFindingIds,
    string Disposition);

public static class ChallengeClassifications
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "insufficient_evidence",
        "contradiction",
        "conflicting_sources",
        "missing_context",
        "reasoning_gap",
    };
}

public sealed record FindingsValidation(
    IReadOnlyList<Finding> Accepted,
    IReadOnlyList<string> RejectedFindingIds);

public static class ResearchValidators
{
    // A finding is rejected unless it cites at least one evidence passage and EVERY cited
    // passage was actually captured (unknown evidence IDs cause rejection).
    public static FindingsValidation ValidateFindings(
        IEnumerable<Finding> findings,
        Func<string, bool> passageExists)
    {
        var accepted = new List<Finding>();
        var rejected = new List<string>();
        foreach (var finding in findings)
        {
            if (finding.EvidencePassageIds.Count > 0 && finding.EvidencePassageIds.All(passageExists))
                accepted.Add(finding);
            else
                rejected.Add(finding.FindingId);
        }
        return new FindingsValidation(accepted, rejected);
    }

    // A challenge is valid only if it targets a committed finding and uses an allowed class.
    public static bool IsChallengeValid(Challenge challenge, IReadOnlySet<string> committedFindingIds) =>
        committedFindingIds.Contains(challenge.TargetFindingId)
        && ChallengeClassifications.Allowed.Contains(challenge.Classification);

    // The Report Builder cannot introduce claims absent from committed findings: every
    // cited finding id must be a committed finding, and at least one must be cited.
    public static bool IsReportValid(ResearchReport report, IReadOnlySet<string> committedFindingIds) =>
        report.CitedFindingIds.Count > 0
        && report.CitedFindingIds.All(committedFindingIds.Contains);
}
