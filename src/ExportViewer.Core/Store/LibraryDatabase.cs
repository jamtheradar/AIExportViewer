using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Store;

/// <summary>
/// One SQLite database per library: a local, derived copy of the export used for browsing and search.
/// It can always be rebuilt from the export folder, so a schema change simply resets it.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    /// <summary>Database file, or null when the library is held in memory only.</summary>
    public string? FilePath { get; }

    public bool InMemory => FilePath is null;

    private LibraryDatabase(string connectionString, string? filePath)
    {
        _connectionString = connectionString;
        FilePath = filePath;
        if (filePath is null)
        {
            // A shared in-memory database lives only while at least one connection is open.
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
        }
        EnsureSchema();
    }

    public static LibraryDatabase OpenFile(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        return new LibraryDatabase(cs, path);
    }

    public static LibraryDatabase OpenInMemory()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = "lib-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        return new LibraryDatabase(cs, null);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = InMemory
            ? "PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;"
            : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        if (ReadVersion(c) is { } v && v != SchemaVersion) DropAll(c);
        Exec(c, Schema.Sql);
        Exec(c, $"INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '{SchemaVersion}');");
    }

    /// <summary>Removes all loaded data so the library can be rebuilt from scratch.</summary>
    public void Reset()
    {
        using var c = Open();
        DropAll(c);
        Exec(c, Schema.Sql);
        Exec(c, $"INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '{SchemaVersion}');");
    }

    public string? GetMeta(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static int? ReadVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='meta'";
        if (cmd.ExecuteScalar() is null) return null;
        cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        return int.TryParse(cmd.ExecuteScalar() as string, out var v) ? v : 0;
    }

    private static void DropAll(SqliteConnection c)
    {
        var names = new List<(string Type, string Name)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'search_fts_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (type, name) in names) Exec(c, $"DROP {type.ToUpperInvariant()} IF EXISTS \"{name}\";");
        if (!c.DataSource.StartsWith("lib-", StringComparison.Ordinal)) Exec(c, "VACUUM;");
    }

    internal static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
