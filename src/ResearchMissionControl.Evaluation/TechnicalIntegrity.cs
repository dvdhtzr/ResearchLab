using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Evaluation;

public enum CheckStatus
{
    Pass,
    Fail,
}

public sealed record EvaluationCheck(string Name, CheckStatus Status, string? Detail);

public sealed record TechnicalIntegrityReport(IReadOnlyList<EvaluationCheck> Checks)
{
    public bool AllPassed => Checks.All(c => c.Status == CheckStatus.Pass);
}

// Lane 1 — deterministic technical-integrity checks over a COMPLETED research record.
// This never mutates the record; evaluation is independent of execution.
public static class TechnicalIntegrityEvaluator
{
    public static TechnicalIntegrityReport Evaluate(
        ResearchReport report,
        IReadOnlyList<Finding> committedFindings,
        int modelAttempts,
        BudgetSnapshot budget,
        Func<string, bool> passageExists)
    {
        var checks = new List<EvaluationCheck>();

        var referenceIntegrity = committedFindings.Count > 0 &&
            committedFindings.All(f => f.EvidencePassageIds.Count > 0 && f.EvidencePassageIds.All(passageExists));
        checks.Add(new EvaluationCheck(
            "reference_integrity",
            referenceIntegrity ? CheckStatus.Pass : CheckStatus.Fail,
            referenceIntegrity ? null : "a committed finding cites missing evidence"));

        var committedIds = new HashSet<string>(committedFindings.Select(f => f.FindingId), StringComparer.Ordinal);
        var reportSchema = ResearchValidators.IsReportValid(report, committedIds);
        checks.Add(new EvaluationCheck(
            "report_schema",
            reportSchema ? CheckStatus.Pass : CheckStatus.Fail,
            reportSchema ? null : "report cites a non-committed finding"));

        var budgetOk = modelAttempts <= budget.ModelAttempts;
        checks.Add(new EvaluationCheck(
            "budget_enforced",
            budgetOk ? CheckStatus.Pass : CheckStatus.Fail,
            $"{modelAttempts}/{budget.ModelAttempts} attempts"));

        var dispositionOk = !string.IsNullOrWhiteSpace(report.Disposition);
        checks.Add(new EvaluationCheck(
            "disposition_present",
            dispositionOk ? CheckStatus.Pass : CheckStatus.Fail,
            report.Disposition));

        return new TechnicalIntegrityReport(checks);
    }
}

// Lane 3 — evaluation laboratory over frozen cases with independently-reviewed expected
// outcomes. Synthetic fixtures stay labeled provisional until independently reviewed.
public sealed record FrozenCase(
    string CaseId,
    IReadOnlyList<string> ReferencePassageIds,
    string ExpectedDisposition,
    bool Provisional);

public sealed record LaboratoryResult(
    string CaseId,
    bool TechnicalPass,
    bool DispositionMatches,
    bool Provisional);

public static class LaboratoryEvaluator
{
    public static LaboratoryResult Evaluate(
        FrozenCase frozenCase,
        ResearchReport report,
        IReadOnlyList<Finding> committedFindings,
        int modelAttempts,
        BudgetSnapshot budget,
        Func<string, bool> passageExists)
    {
        var technical = TechnicalIntegrityEvaluator.Evaluate(report, committedFindings, modelAttempts, budget, passageExists);
        var dispositionMatches = string.Equals(report.Disposition, frozenCase.ExpectedDisposition, StringComparison.Ordinal);
        return new LaboratoryResult(frozenCase.CaseId, technical.AllPassed, dispositionMatches, frozenCase.Provisional);
    }
}
