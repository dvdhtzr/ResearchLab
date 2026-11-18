namespace ResearchMissionControl.Domain;

// Mirror of the AUTHORITATIVE job state machine (apps/cloud-api/src/domain).
// The cloud is authoritative for transitions; the worker uses this to decide which
// operations it may legally request. STATUS: unverified skeleton (no dotnet here).

public enum JobState
{
    Queued,
    Claimed,
    Running,
    RecoveryRequired,
    Completed,
    Failed,
    Cancelled,
}

public enum TransitionOp
{
    Claim,
    Start,
    LeaseLost,
    StartupFail,
    Complete,
    ExecFail,
    CancelQueued,
    CancelAcked,
    RecoveryRequeue,
    RecoveryFail,
    RecoveryCancel,
}

public static class JobStateMachine
{
    public static readonly IReadOnlySet<JobState> Terminal =
        new HashSet<JobState> { JobState.Completed, JobState.Failed, JobState.Cancelled };

    // The complete, closed set of allowed transitions (spec §8).
    private static readonly IReadOnlyDictionary<TransitionOp, (JobState[] From, JobState To)> Rules =
        new Dictionary<TransitionOp, (JobState[], JobState)>
        {
            [TransitionOp.Claim] = (new[] { JobState.Queued }, JobState.Claimed),
            [TransitionOp.Start] = (new[] { JobState.Claimed }, JobState.Running),
            [TransitionOp.LeaseLost] = (new[] { JobState.Claimed, JobState.Running }, JobState.RecoveryRequired),
            [TransitionOp.StartupFail] = (new[] { JobState.Claimed }, JobState.Failed),
            [TransitionOp.Complete] = (new[] { JobState.Running }, JobState.Completed),
            [TransitionOp.ExecFail] = (new[] { JobState.Running }, JobState.Failed),
            [TransitionOp.CancelQueued] = (new[] { JobState.Queued }, JobState.Cancelled),
            [TransitionOp.CancelAcked] = (new[] { JobState.Claimed, JobState.Running }, JobState.Cancelled),
            [TransitionOp.RecoveryRequeue] = (new[] { JobState.RecoveryRequired }, JobState.Queued),
            [TransitionOp.RecoveryFail] = (new[] { JobState.RecoveryRequired }, JobState.Failed),
            [TransitionOp.RecoveryCancel] = (new[] { JobState.RecoveryRequired }, JobState.Cancelled),
        };

    public static bool IsTerminal(JobState state) => Terminal.Contains(state);

    public static bool IsAllowed(TransitionOp op, JobState from) =>
        Rules.TryGetValue(op, out var rule) && rule.From.Contains(from);

    public static JobState TargetState(TransitionOp op) => Rules[op].To;
}
