using Microsoft.Data.Sqlite;

namespace ResearchMissionControl.Infrastructure;

// Opens a local worker SQLite database and applies the embedded schema (idempotent).
// Pooling is disabled so a Dispose() truly closes the handle — this lets tests
// simulate a process crash by reopening the same file.
public static class WorkerDatabase
{
    public static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Migrate(connection);
        return connection;
    }

    public static void Migrate(SqliteConnection connection)
    {
        var existing = Scalar(connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name='operation_journal'");
        if (existing is not null)
            return;
        Execute(connection, ReadSchema());
    }

    private static string ReadSchema()
    {
        var assembly = typeof(WorkerDatabase).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("0001_worker_core.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded schema not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : result;
    }

    internal static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
