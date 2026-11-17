using System.Security.Cryptography;
using System.Text;
using ResearchMissionControl.Application;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

// Governed inference gateway (spec §24). Every inference follows the same durable flow;
// an unknown completion is never recorded as success, and the attempt is always journaled
// before invocation so an interruption stays recorded.
public sealed class InferenceGateway
{
    private readonly IInferenceClient _client;
    private readonly ModelRegistry _registry;
    private readonly OperationJournalStore _journal;
    private readonly ReservationStore _reservations;
    private readonly Func<string> _idFactory;
    private readonly Func<long> _clock;

    public InferenceGateway(
        IInferenceClient client,
        ModelRegistry registry,
        OperationJournalStore journal,
        ReservationStore reservations,
        Func<string>? idFactory = null,
        Func<long>? clock = null)
    {
        _client = client;
        _registry = registry;
        _journal = journal;
        _reservations = reservations;
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<InferenceOutcome> ExecuteAsync(
        InferenceRequest request,
        long currentGeneration,
        BudgetSnapshot budget,
        int alreadyConsumedAttempts,
        CancellationToken cancellationToken)
    {
        // 1. Confirm current execution authority (fencing).
        if (!Fencing.CanCommit(request.ExecutionGeneration, currentGeneration))
            return InferenceOutcome.Failed(InferenceFailureCategory.InterruptedUnknown, string.Empty);

        // 4. Confirm available resource budget BEFORE reserving.
        if (alreadyConsumedAttempts >= budget.ModelAttempts)
            return InferenceOutcome.Failed(InferenceFailureCategory.BudgetExhausted, string.Empty);

        // 2/3. Confirm the model is installed, approved, and matches its expected digest.
        string? installedDigest;
        try
        {
            installedDigest = await _client.GetInstalledDigestAsync(request.ModelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InferenceTransportException)
        {
            return InferenceOutcome.Failed(InferenceFailureCategory.TransportError, string.Empty);
        }

        var check = _registry.Check(request.ModelId, installedDigest);
        if (!check.Ok)
            return InferenceOutcome.Failed(check.Failure!.Value, string.Empty);

        // 5. Reserve the attempt durably BEFORE invoking (survives a crash).
        var reservationId = _idFactory();
        var operationId = _idFactory();
        var startedAt = _clock();
        _reservations.ReserveInference(
            reservationId, request.JobId, request.ExecutionGeneration, request.ModelId, installedDigest!, startedAt);
        _journal.Reserve(
            operationId, request.JobId, request.ExecutionGeneration, "inference",
            request.AgentRole, request.WorkflowStage, reservationId, null, request.ModelId, startedAt);

        // 6. Invoke.
        string text;
        try
        {
            text = await _client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _journal.Complete(operationId, "timeout", null, "timeout", _clock());
            return InferenceOutcome.Failed(InferenceFailureCategory.Timeout, reservationId);
        }
        catch (OperationCanceledException)
        {
            _journal.Complete(operationId, "cancelled", null, "cancelled", _clock());
            return InferenceOutcome.Failed(InferenceFailureCategory.Cancelled, reservationId);
        }
        catch (InferenceTransportException)
        {
            _journal.Complete(operationId, "transport_error", null, "transport_error", _clock());
            return InferenceOutcome.Failed(InferenceFailureCategory.TransportError, reservationId);
        }

        // 7. Validate the returned JSON independently (never trust structured-output mode).
        var invalid = StructuredOutputValidator.Validate(text, request.RequiredJsonProperties);
        if (invalid is not null)
        {
            var category = invalid.Value;
            var outcomeLabel = category == InferenceFailureCategory.InvalidJson ? "invalid_json" : "invalid_schema";
            _journal.Complete(operationId, outcomeLabel, null, category.ToString(), _clock());
            return InferenceOutcome.Failed(category, reservationId);
        }

        // 8/9/10. Record usage and commit accepted output.
        _reservations.ConsumeInference(reservationId, _clock());
        _journal.Complete(operationId, "committed", ComputeDigest(text), null, _clock());
        return InferenceOutcome.Ok(text, reservationId);
    }

    private static string ComputeDigest(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
