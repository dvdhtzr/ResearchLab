using Microsoft.Data.Sqlite;
using ResearchMissionControl.Domain;
using ResearchMissionControl.Infrastructure;
using Xunit;

namespace ResearchMissionControl.Infrastructure.Tests;

// M2 — Durable recovery fault-injection suite. Runs against a temp SQLite file; no
// Ollama, no cloud. Proves: no stale write, no fabricated completion, budget preserved,
// checkpoints integrity-validated, idempotent outbox, owner-controlled recovery.
public sealed class M2DurabilityTests
{
    private sealed class TempDb : IDisposable
    {
        private readonly List<SqliteConnection> _opened = new();
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rmc-{Guid.NewGuid():N}.sqlite");

        public SqliteConnection Open()
        {
            var connection = WorkerDatabase.Open(Path);
            _opened.Add(connection);
            return connection;
        }

        public void Dispose()
        {
            foreach (var c in _opened)
            {
                try { c.Dispose(); } catch { /* ignore */ }
            }
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { System.IO.File.Delete(Path + suffix); } catch { /* ignore */ }
            }
        }
    }

    [Fact] // process termination: a reserved-but-uncompleted attempt survives and is not fabricated
    public void InterruptedAttempt_SurvivesCrash_AndIsNotFabricated()
    {
        using var db = new TempDb();
        var conn1 = db.Open();
        new OperationJournalStore(conn1).Reserve(
            "op-1", "job-1", 1, "inference", "researcher", "plan", "res-1", "indigest", "qwen3.5:9b", 100);
        conn1.Dispose(); // simulate crash before Complete()

        var conn2 = db.Open(); // reopen the same database file
        var journal = new OperationJournalStore(conn2);
        var entry = journal.Get("op-1");

        Assert.NotNull(entry);
        Assert.Null(entry!.Outcome);            // never reconstructed as success
        Assert.Equal(1, journal.CountInterrupted("job-1"));
    }

    [Fact] // a finalized journal entry cannot be completed twice
    public void Complete_IsSingleShot()
    {
        using var db = new TempDb();
        var journal = new OperationJournalStore(db.Open());
        journal.Reserve("op-x", "job-1", 1, "tool", null, null, null, null, null, 1);
        journal.Complete("op-x", "committed", "rd", null, 2);

        Assert.Throws<InvalidOperationException>(
            () => journal.Complete("op-x", "committed", "rd", null, 3));
    }

    [Fact] // duplicate result / lost-ack replay: the outbox is idempotent per (operation, key)
    public void Outbox_DuplicateEnqueue_IsIdempotent()
    {
        using var db = new TempDb();
        var outbox = new OutboxStore(db.Open());

        Assert.True(outbox.Enqueue("o1", "job-1", "worker.complete", "key-1", "{}", 1));
        Assert.False(outbox.Enqueue("o2", "job-1", "worker.complete", "key-1", "{}", 2));
        Assert.Equal(1, outbox.Count("worker.complete", "key-1"));

        Assert.Equal(1, outbox.CountUnsent());
        outbox.MarkSent("worker.complete", "key-1", "receipt-1", 3);
        Assert.Equal(0, outbox.CountUnsent());
    }

    [Fact] // interrupted acknowledgment: an unsent message persists for replay after reconnect
    public void Outbox_UnsentMessage_PersistsAcrossCrash()
    {
        using var db = new TempDb();
        var conn1 = db.Open();
        new OutboxStore(conn1).Enqueue("o1", "job-1", "worker.start", "key-1", "{}", 1);
        conn1.Dispose();

        var outbox = new OutboxStore(db.Open());
        Assert.Equal(1, outbox.CountUnsent()); // still awaiting delivery
    }

    [Fact] // a valid checkpoint round-trips and passes the restore validator
    public void Checkpoint_Valid_RoundTrips()
    {
        using var db = new TempDb();
        var conn = db.Open();
        var store = new CheckpointStore(conn);
        var cp = NewCheckpoint("c1", "job-1", 1);
        store.Save(cp, 1);

        var loaded = store.LoadLatest("job-1");
        Assert.NotNull(loaded);
        var validation = RestoreValidator.Validate(loaded!);
        Assert.True(validation.IsValid);
    }

    [Fact] // a tampered checkpoint is rejected on restore
    public void Checkpoint_Damaged_IsRejected()
    {
        using var db = new TempDb();
        var conn = db.Open();
        var store = new CheckpointStore(conn);
        store.Save(NewCheckpoint("c1", "job-1", 1), 1);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE workflow_checkpoints SET committed_outputs = 'TAMPERED' WHERE checkpoint_id = 'c1'";
            cmd.ExecuteNonQuery();
        }

        var loaded = store.LoadLatest("job-1");
        var validation = RestoreValidator.Validate(loaded!);
        Assert.False(validation.IsValid);
        Assert.Equal("integrity_digest_mismatch", validation.FailureReason);
    }

    [Fact] // an incompatible checkpoint schema version is rejected
    public void Checkpoint_IncompatibleSchema_IsRejected()
    {
        var cp = NewCheckpoint("c1", "job-1", 1) with { SchemaVersion = "checkpoint.v999" };
        var validation = RestoreValidator.Validate(cp, cp.ComputeDigest());
        Assert.False(validation.IsValid);
        Assert.Equal("incompatible_schema_version", validation.FailureReason);
    }

    [Fact] // consumed budget persists across a recovery generation (never reset)
    public void Budget_PreservedAcrossRecoveryGeneration()
    {
        using var db = new TempDb();
        var res = new ReservationStore(db.Open());
        res.ReserveInference("r1", "job-1", 1, "qwen3.5:9b", "digest", 1);
        res.ReserveInference("r2", "job-1", 1, "qwen3.5:9b", "digest", 2);
        Assert.Equal(2, res.CountInferenceAttempts("job-1"));

        // Recovery bumps the generation; prior consumption remains counted.
        res.ReserveInference("r3", "job-1", 2, "qwen3.5:9b", "digest", 3);
        Assert.Equal(3, res.CountInferenceAttempts("job-1"));
    }

    [Fact] // stale generation cannot commit (INV-06)
    public void StaleGeneration_CannotCommit()
    {
        Assert.False(Fencing.CanCommit(holderGeneration: 1, currentGeneration: 2));
        Assert.True(Fencing.CanCommit(holderGeneration: 2, currentGeneration: 2));
    }

    [Theory] // owner-controlled recovery authorization semantics
    [InlineData(RecoveryMode.Restart)]
    public void Recovery_Restart_BumpsGeneration(RecoveryMode mode)
    {
        var outcome = RecoveryPolicy.Authorize(new RecoveryDecision(mode, null), 5, null);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.IsRequeue);
        Assert.Equal(6, outcome.NewGeneration);
    }

    [Fact]
    public void Recovery_Resume_RequiresValidatedMatchingCheckpoint()
    {
        // no digest supplied
        var noDigest = RecoveryPolicy.Authorize(new RecoveryDecision(RecoveryMode.Resume, null), 1, null);
        Assert.False(noDigest.Accepted);
        Assert.Equal("resume_requires_checkpoint_digest", noDigest.RejectionReason);

        // digest supplied but checkpoint invalid
        var invalid = new CheckpointValidationResult(false, "digestA", "integrity_digest_mismatch");
        var badCp = RecoveryPolicy.Authorize(new RecoveryDecision(RecoveryMode.Resume, "digestA"), 1, invalid);
        Assert.False(badCp.Accepted);

        // digest does not match the validated checkpoint
        var valid = new CheckpointValidationResult(true, "digestA", null);
        var mismatch = RecoveryPolicy.Authorize(new RecoveryDecision(RecoveryMode.Resume, "digestB"), 1, valid);
        Assert.False(mismatch.Accepted);
        Assert.Equal("checkpoint_digest_mismatch", mismatch.RejectionReason);

        // valid + matching → requeue with a new generation
        var ok = RecoveryPolicy.Authorize(new RecoveryDecision(RecoveryMode.Resume, "digestA"), 1, valid);
        Assert.True(ok.Accepted);
        Assert.True(ok.IsRequeue);
        Assert.Equal(2, ok.NewGeneration);
    }

    [Fact]
    public void Recovery_Terminate_IsTerminal()
    {
        var outcome = RecoveryPolicy.Authorize(new RecoveryDecision(RecoveryMode.Terminate, null), 1, null);
        Assert.True(outcome.Accepted);
        Assert.True(outcome.IsTerminate);
    }

    private static Checkpoint NewCheckpoint(string id, string jobId, long generation) =>
        new(
            id,
            jobId,
            generation,
            "plan",
            1,
            "journal-cursor-0",
            "[]",
            "{}",
            "{}",
            "{}",
            RestoreValidator.CurrentSchemaVersion);
}
