using Dapper;
using Microsoft.Data.Sqlite;

namespace Engram.Worker.Storage;

/// <summary>
/// The single SQLite file. The worker is its only writer. WAL lets readers (later: MCP, viewer) run while
/// the writer works.
/// </summary>
internal sealed class Database(string path)
{
    public string Path { get; } = path;

    private string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 5,
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    /// <summary>Creates the file if needed, switches to WAL, backs up, and applies pending migrations.</summary>
    public MigrationResult Initialize(string backupDir, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        return Migrations.Apply(connection, backupDir, time);
    }
}
