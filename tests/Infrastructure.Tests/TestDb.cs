using Microsoft.Data.Sqlite;
using ResearchMissionControl.Infrastructure;

namespace ResearchMissionControl.Infrastructure.Tests;

// Shared temp-file SQLite database for tests. Pooling is off (see WorkerDatabase), so
// disposing a connection truly closes the handle and a reopen reads persisted data.
internal sealed class TestDb : IDisposable
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
        foreach (var connection in _opened)
        {
            try { connection.Dispose(); } catch { /* ignore */ }
        }
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { System.IO.File.Delete(Path + suffix); } catch { /* ignore */ }
        }
    }
}
