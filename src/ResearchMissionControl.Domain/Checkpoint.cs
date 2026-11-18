using System.Security.Cryptography;
using System.Text;

namespace ResearchMissionControl.Domain;

// A validated workflow checkpoint (spec §12). Recoverable only after its SQLite
// transaction commits; the integrity digest is SHA-256 over a canonical body.
public sealed record Checkpoint(
    string CheckpointId,
    string JobId,
    long ExecutionGeneration,
    string WorkflowStage,
    int Round,
    string JournalCursor,
    string EvidenceSnapshotIdsJson,
    string CommittedOutputsJson,
    string ResourceAccountingJson,
    string ModelPromptManifestJson,
    string SchemaVersion)
{
    // Fixed field order; unit separator avoids ambiguity between concatenated fields.
    public string CanonicalBody() => string.Join(
        '\u001f',
        CheckpointId,
        JobId,
        ExecutionGeneration.ToString(),
        WorkflowStage,
        Round.ToString(),
        JournalCursor,
        EvidenceSnapshotIdsJson,
        CommittedOutputsJson,
        ResourceAccountingJson,
        ModelPromptManifestJson,
        SchemaVersion);

    public string ComputeDigest()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalBody()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record CheckpointValidationResult(bool IsValid, string Digest, string? FailureReason);
