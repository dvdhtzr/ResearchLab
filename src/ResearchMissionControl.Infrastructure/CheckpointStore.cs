using Microsoft.Data.Sqlite;
using ResearchMissionControl.Domain;

namespace ResearchMissionControl.Infrastructure;

public sealed class CheckpointStore
{
    private readonly SqliteConnection _connection;

    public CheckpointStore(SqliteConnection connection) => _connection = connection;

    // Returns the stored integrity digest.
    public string Save(Checkpoint checkpoint, long createdAt)
    {
        var digest = checkpoint.ComputeDigest();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workflow_checkpoints
                (checkpoint_id, job_id, execution_generation, workflow_stage, round, journal_cursor,
                 evidence_snapshot_ids, committed_outputs, resource_accounting, model_prompt_manifest,
                 schema_version, integrity_digest, created_at)
            VALUES ($id, $job, $gen, $stage, $round, $cursor, $ev, $out, $res, $manifest, $sv, $digest, $at)
            """;
        WorkerDatabase.Bind(command, "$id", checkpoint.CheckpointId);
        WorkerDatabase.Bind(command, "$job", checkpoint.JobId);
        WorkerDatabase.Bind(command, "$gen", checkpoint.ExecutionGeneration);
        WorkerDatabase.Bind(command, "$stage", checkpoint.WorkflowStage);
        WorkerDatabase.Bind(command, "$round", checkpoint.Round);
        WorkerDatabase.Bind(command, "$cursor", checkpoint.JournalCursor);
        WorkerDatabase.Bind(command, "$ev", checkpoint.EvidenceSnapshotIdsJson);
        WorkerDatabase.Bind(command, "$out", checkpoint.CommittedOutputsJson);
        WorkerDatabase.Bind(command, "$res", checkpoint.ResourceAccountingJson);
        WorkerDatabase.Bind(command, "$manifest", checkpoint.ModelPromptManifestJson);
        WorkerDatabase.Bind(command, "$sv", checkpoint.SchemaVersion);
        WorkerDatabase.Bind(command, "$digest", digest);
        WorkerDatabase.Bind(command, "$at", createdAt);
        command.ExecuteNonQuery();
        return digest;
    }

    public StoredCheckpoint? LoadLatest(string jobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT checkpoint_id, job_id, execution_generation, workflow_stage, round, journal_cursor,
                   evidence_snapshot_ids, committed_outputs, resource_accounting, model_prompt_manifest,
                   schema_version, integrity_digest
              FROM workflow_checkpoints
             WHERE job_id = $job
             ORDER BY created_at DESC, rowid DESC
             LIMIT 1
            """;
        WorkerDatabase.Bind(command, "$job", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var checkpoint = new Checkpoint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
        return new StoredCheckpoint(checkpoint, reader.GetString(11));
    }
}

public sealed record StoredCheckpoint(Checkpoint Checkpoint, string StoredDigest);

// Rejects corrupted, incompatible, or incomplete checkpoints on restore.
public static class RestoreValidator
{
    public const string CurrentSchemaVersion = "checkpoint.v1";

    public static CheckpointValidationResult Validate(Checkpoint checkpoint, string storedDigest)
    {
        if (checkpoint.SchemaVersion != CurrentSchemaVersion)
            return new CheckpointValidationResult(false, storedDigest, "incompatible_schema_version");
        var recomputed = checkpoint.ComputeDigest();
        if (!string.Equals(recomputed, storedDigest, StringComparison.Ordinal))
            return new CheckpointValidationResult(false, storedDigest, "integrity_digest_mismatch");
        return new CheckpointValidationResult(true, storedDigest, null);
    }

    public static CheckpointValidationResult Validate(StoredCheckpoint stored) =>
        Validate(stored.Checkpoint, stored.StoredDigest);
}
