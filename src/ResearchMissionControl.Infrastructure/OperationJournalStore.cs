using Microsoft.Data.Sqlite;

namespace ResearchMissionControl.Infrastructure;

public sealed record JournalEntry(string OperationId, string? Outcome, string? ResultDigest);

// Durable operation journal (spec §12). An attempt is reserved BEFORE the model/tool
// invocation; an interrupted attempt stays recorded with a NULL outcome and is never
// reconstructed or fabricated as success.
public sealed class OperationJournalStore
{
    private readonly SqliteConnection _connection;

    public OperationJournalStore(SqliteConnection connection) => _connection = connection;

    public void Reserve(
        string operationId,
        string jobId,
        long executionGeneration,
        string operationType,
        string? agentRole,
        string? workflowStage,
        string? reservationId,
        string? inputDigest,
        string? modelOrToolVersion,
        long startedAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_journal
                (operation_id, job_id, execution_generation, operation_type, agent_role, workflow_stage,
                 reservation_id, input_digest, model_or_tool_version, started_at)
            VALUES ($id, $job, $gen, $type, $role, $stage, $res, $input, $ver, $started)
            """;
        WorkerDatabase.Bind(command, "$id", operationId);
        WorkerDatabase.Bind(command, "$job", jobId);
        WorkerDatabase.Bind(command, "$gen", executionGeneration);
        WorkerDatabase.Bind(command, "$type", operationType);
        WorkerDatabase.Bind(command, "$role", agentRole);
        WorkerDatabase.Bind(command, "$stage", workflowStage);
        WorkerDatabase.Bind(command, "$res", reservationId);
        WorkerDatabase.Bind(command, "$input", inputDigest);
        WorkerDatabase.Bind(command, "$ver", modelOrToolVersion);
        WorkerDatabase.Bind(command, "$started", startedAt);
        command.ExecuteNonQuery();
    }

    public void Complete(string operationId, string outcome, string? resultDigest, string? errorCategory, long completedAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE operation_journal
               SET outcome = $outcome, result_digest = $res, error_category = $err, completed_at = $done
             WHERE operation_id = $id AND outcome IS NULL
            """;
        WorkerDatabase.Bind(command, "$outcome", outcome);
        WorkerDatabase.Bind(command, "$res", resultDigest);
        WorkerDatabase.Bind(command, "$err", errorCategory);
        WorkerDatabase.Bind(command, "$done", completedAt);
        WorkerDatabase.Bind(command, "$id", operationId);
        var changed = command.ExecuteNonQuery();
        if (changed == 0)
            throw new InvalidOperationException(
                "Journal entry is missing or already finalized; refusing to fabricate a result.");
    }

    public JournalEntry? Get(string operationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT operation_id, outcome, result_digest FROM operation_journal WHERE operation_id = $id";
        WorkerDatabase.Bind(command, "$id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new JournalEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    // Count attempts that were reserved but never completed (interrupted).
    public int CountInterrupted(string jobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM operation_journal WHERE job_id = $job AND outcome IS NULL";
        WorkerDatabase.Bind(command, "$job", jobId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
    }
}
