using Microsoft.Data.Sqlite;

namespace ResearchMissionControl.Infrastructure;

// Inference and tool reservations (spec §12/§25). Reservations survive a crash and are
// counted toward consumed budget across recovery generations — recovery never resets them.
public sealed class ReservationStore
{
    private readonly SqliteConnection _connection;

    public ReservationStore(SqliteConnection connection) => _connection = connection;

    public void ReserveInference(string reservationId, string jobId, long executionGeneration, string modelId, string modelDigest, long reservedAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO inference_reservations
                (reservation_id, job_id, execution_generation, model_id, model_digest, reserved_at)
            VALUES ($id, $job, $gen, $model, $digest, $at)
            """;
        WorkerDatabase.Bind(command, "$id", reservationId);
        WorkerDatabase.Bind(command, "$job", jobId);
        WorkerDatabase.Bind(command, "$gen", executionGeneration);
        WorkerDatabase.Bind(command, "$model", modelId);
        WorkerDatabase.Bind(command, "$digest", modelDigest);
        WorkerDatabase.Bind(command, "$at", reservedAt);
        command.ExecuteNonQuery();
    }

    public void ConsumeInference(string reservationId, long releasedAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE inference_reservations SET consumed = 1, released_at = $at WHERE reservation_id = $id";
        WorkerDatabase.Bind(command, "$at", releasedAt);
        WorkerDatabase.Bind(command, "$id", reservationId);
        command.ExecuteNonQuery();
    }

    // All reserved inference attempts for the job, regardless of generation.
    public int CountInferenceAttempts(string jobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM inference_reservations WHERE job_id = $job";
        WorkerDatabase.Bind(command, "$job", jobId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
    }
}
