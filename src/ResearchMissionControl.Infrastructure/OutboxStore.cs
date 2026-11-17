using Microsoft.Data.Sqlite;

namespace ResearchMissionControl.Infrastructure;

// Durable local outbox (spec §13). Messages are persisted BEFORE transmission; a
// duplicate (operation, idempotency_key) is idempotent, so a replayed message after a
// lost acknowledgment never produces a second effect.
public sealed class OutboxStore
{
    private readonly SqliteConnection _connection;

    public OutboxStore(SqliteConnection connection) => _connection = connection;

    // Returns true if a new row was enqueued, false if an identical one already existed.
    public bool Enqueue(string outboxId, string jobId, string operation, string idempotencyKey, string payloadJson, long createdAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO outbound_events
                (outbox_id, job_id, operation, idempotency_key, payload_json, created_at)
            VALUES ($id, $job, $op, $key, $payload, $at)
            """;
        WorkerDatabase.Bind(command, "$id", outboxId);
        WorkerDatabase.Bind(command, "$job", jobId);
        WorkerDatabase.Bind(command, "$op", operation);
        WorkerDatabase.Bind(command, "$key", idempotencyKey);
        WorkerDatabase.Bind(command, "$payload", payloadJson);
        WorkerDatabase.Bind(command, "$at", createdAt);
        return command.ExecuteNonQuery() > 0;
    }

    public void MarkSent(string operation, string idempotencyKey, string receiptId, long sentAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE outbound_events
               SET sent_at = $at, acked_receipt_id = $receipt
             WHERE operation = $op AND idempotency_key = $key
            """;
        WorkerDatabase.Bind(command, "$at", sentAt);
        WorkerDatabase.Bind(command, "$receipt", receiptId);
        WorkerDatabase.Bind(command, "$op", operation);
        WorkerDatabase.Bind(command, "$key", idempotencyKey);
        command.ExecuteNonQuery();
    }

    public int CountUnsent()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbound_events WHERE sent_at IS NULL";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
    }

    public int Count(string operation, string idempotencyKey)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM outbound_events WHERE operation = $op AND idempotency_key = $key";
        WorkerDatabase.Bind(command, "$op", operation);
        WorkerDatabase.Bind(command, "$key", idempotencyKey);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
    }
}
