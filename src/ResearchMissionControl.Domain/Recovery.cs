namespace ResearchMissionControl.Domain;

public enum RecoveryMode
{
    Resume,
    Restart,
    Terminate,
}

public sealed record RecoveryDecision(RecoveryMode Mode, string? CheckpointDigest);

// The result of authorizing an owner recovery decision.
public sealed record RecoveryOutcome
{
    public bool Accepted { get; private init; }
    public bool IsRequeue { get; private init; }
    public bool IsTerminate { get; private init; }
    public long NewGeneration { get; private init; }
    public string? RejectionReason { get; private init; }

    public static RecoveryOutcome Requeue(long newGeneration) =>
        new() { Accepted = true, IsRequeue = true, NewGeneration = newGeneration };

    public static RecoveryOutcome Terminate() =>
        new() { Accepted = true, IsTerminate = true };

    public static RecoveryOutcome Rejected(string reason) =>
        new() { Accepted = false, RejectionReason = reason };
}

// Owner-controlled recovery semantics (spec §13). No automatic resume/restart.
// Recovery never resets consumed budget (enforced by callers reusing the same stores).
public static class RecoveryPolicy
{
    public static RecoveryOutcome Authorize(
        RecoveryDecision decision,
        long currentGeneration,
        CheckpointValidationResult? checkpoint)
    {
        switch (decision.Mode)
        {
            case RecoveryMode.Resume:
                if (string.IsNullOrEmpty(decision.CheckpointDigest))
                    return RecoveryOutcome.Rejected("resume_requires_checkpoint_digest");
                if (checkpoint is null || !checkpoint.IsValid)
                    return RecoveryOutcome.Rejected(checkpoint?.FailureReason ?? "checkpoint_unavailable");
                if (!string.Equals(checkpoint.Digest, decision.CheckpointDigest, StringComparison.Ordinal))
                    return RecoveryOutcome.Rejected("checkpoint_digest_mismatch");
                return RecoveryOutcome.Requeue(currentGeneration + 1);

            case RecoveryMode.Restart:
                // Start a new fenced generation from the beginning; budget preserved.
                return RecoveryOutcome.Requeue(currentGeneration + 1);

            case RecoveryMode.Terminate:
                return RecoveryOutcome.Terminate();

            default:
                return RecoveryOutcome.Rejected("unknown_mode");
        }
    }
}
