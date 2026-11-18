using System.Text.Json;

namespace ResearchMissionControl.Domain;

// Distinct inference failure categories (spec §24). An unknown completion is never
// recorded as success.
public enum InferenceFailureCategory
{
    ModelUnavailable,
    DigestMismatch,
    Timeout,
    Cancelled,
    TransportError,
    InvalidJson,
    InvalidSchema,
    BudgetExhausted,
    InterruptedUnknown,
}

public sealed record InferenceRequest(
    string JobId,
    long ExecutionGeneration,
    string ModelId,
    string AgentRole,
    string WorkflowStage,
    string Prompt,
    IReadOnlyList<string> RequiredJsonProperties);

public sealed record InferenceOutcome
{
    public bool Success { get; private init; }
    public string? Json { get; private init; }
    public InferenceFailureCategory? Failure { get; private init; }
    public string ReservationId { get; private init; } = string.Empty;

    public static InferenceOutcome Ok(string json, string reservationId) =>
        new() { Success = true, Json = json, ReservationId = reservationId };

    public static InferenceOutcome Failed(InferenceFailureCategory category, string reservationId) =>
        new() { Success = false, Failure = category, ReservationId = reservationId };
}

// Owner-approved local model registry (spec §24). A model must be approved, installed,
// and match its expected digest (when one is pinned).
public sealed record ApprovedModel(string ModelId, string? ExpectedDigest);

public sealed record ModelCheck(bool Ok, InferenceFailureCategory? Failure, string? Reason);

public sealed class ModelRegistry
{
    private readonly Dictionary<string, ApprovedModel> _models;

    public ModelRegistry(IEnumerable<ApprovedModel> models) =>
        _models = models.ToDictionary(m => m.ModelId, StringComparer.Ordinal);

    public bool IsApproved(string modelId) => _models.ContainsKey(modelId);

    // installedDigest is what Ollama reports for the installed model, or null if absent.
    public ModelCheck Check(string modelId, string? installedDigest)
    {
        if (!_models.TryGetValue(modelId, out var approved))
            return new ModelCheck(false, InferenceFailureCategory.ModelUnavailable, "not_approved");
        if (installedDigest is null)
            return new ModelCheck(false, InferenceFailureCategory.ModelUnavailable, "not_installed");
        if (approved.ExpectedDigest is not null &&
            !string.Equals(approved.ExpectedDigest, installedDigest, StringComparison.Ordinal))
            return new ModelCheck(false, InferenceFailureCategory.DigestMismatch, "digest_mismatch");
        return new ModelCheck(true, null, null);
    }
}

// Validates a model's structured output. Never trusts structured-output mode: the JSON
// is independently parsed and checked for required top-level properties.
public static class StructuredOutputValidator
{
    public static InferenceFailureCategory? Validate(string text, IReadOnlyList<string> requiredProperties)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return InferenceFailureCategory.InvalidJson;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return InferenceFailureCategory.InvalidSchema;
            foreach (var property in requiredProperties)
            {
                if (!document.RootElement.TryGetProperty(property, out _))
                    return InferenceFailureCategory.InvalidSchema;
            }
        }

        return null;
    }
}
