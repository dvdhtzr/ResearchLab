using Microsoft.Data.Sqlite;

namespace ResearchMissionControl.Infrastructure;

// Immutable evidence snapshots and their bounded passages (spec §23). A cited source is
// not assumed correct merely because it was fetched; this only records what was captured.
public sealed class EvidenceStore
{
    private readonly SqliteConnection _connection;

    public EvidenceStore(SqliteConnection connection) => _connection = connection;

    public void SaveSnapshot(
        string snapshotId,
        string jobId,
        string sourceIdentity,
        string? retrievalUrl,
        long retrievedAt,
        string contentDigest,
        string? rightsMetadata,
        string provenance,
        string retrievalOutcome)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_snapshots
                (snapshot_id, job_id, source_identity, retrieval_url, retrieved_at,
                 content_digest, rights_metadata, provenance, retrieval_outcome)
            VALUES ($id, $job, $src, $url, $at, $digest, $rights, $prov, $outcome)
            """;
        WorkerDatabase.Bind(command, "$id", snapshotId);
        WorkerDatabase.Bind(command, "$job", jobId);
        WorkerDatabase.Bind(command, "$src", sourceIdentity);
        WorkerDatabase.Bind(command, "$url", retrievalUrl);
        WorkerDatabase.Bind(command, "$at", retrievedAt);
        WorkerDatabase.Bind(command, "$digest", contentDigest);
        WorkerDatabase.Bind(command, "$rights", rightsMetadata);
        WorkerDatabase.Bind(command, "$prov", provenance);
        WorkerDatabase.Bind(command, "$outcome", retrievalOutcome);
        command.ExecuteNonQuery();
    }

    public void AddPassage(string passageId, string snapshotId, int ordinal, string text)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO evidence_passages (passage_id, snapshot_id, ordinal, text) VALUES ($id, $snap, $ord, $text)";
        WorkerDatabase.Bind(command, "$id", passageId);
        WorkerDatabase.Bind(command, "$snap", snapshotId);
        WorkerDatabase.Bind(command, "$ord", ordinal);
        WorkerDatabase.Bind(command, "$text", text);
        command.ExecuteNonQuery();
    }

    public bool PassageExists(string passageId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM evidence_passages WHERE passage_id = $id";
        WorkerDatabase.Bind(command, "$id", passageId);
        return command.ExecuteScalar() is not null;
    }

    public string? GetPassageText(string passageId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT text FROM evidence_passages WHERE passage_id = $id";
        WorkerDatabase.Bind(command, "$id", passageId);
        return command.ExecuteScalar() as string;
    }

    public int CountSnapshots(string jobId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM evidence_snapshots WHERE job_id = $job";
        WorkerDatabase.Bind(command, "$job", jobId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L);
    }
}
