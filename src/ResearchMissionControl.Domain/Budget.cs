namespace ResearchMissionControl.Domain;

public enum ExecutionProfile
{
    Quick,
    Standard,
    Extended,
}

// Server-controlled resource budgets (spec §25).
public sealed record BudgetSnapshot(
    int ModelAttempts,
    int ToolExecutions,
    int ResearchRounds,
    int ActiveDurationSeconds,
    int MaxSimultaneousInference);

public static class Budgets
{
    public static BudgetSnapshot For(ExecutionProfile profile) => profile switch
    {
        ExecutionProfile.Quick => new BudgetSnapshot(8, 15, 1, 15 * 60, 1),
        ExecutionProfile.Standard => new BudgetSnapshot(24, 50, 3, 40 * 60, 1),
        ExecutionProfile.Extended => new BudgetSnapshot(48, 100, 5, 90 * 60, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}

// Consumed budget persists across recovery generations; recovery never resets it.
public sealed record ConsumedBudget(int ModelAttempts, int ToolExecutions, int ActiveDurationSeconds)
{
    public bool ModelAttemptsExhausted(BudgetSnapshot budget) => ModelAttempts >= budget.ModelAttempts;
}

// Fencing: a holder may only commit under the job's current execution generation.
public static class Fencing
{
    public static bool CanCommit(long holderGeneration, long currentGeneration) =>
        holderGeneration == currentGeneration;
}
