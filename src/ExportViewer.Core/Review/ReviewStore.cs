using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Review;

/// <param name="MessageUuid">Null when the whole conversation is flagged.</param>
public sealed record ReviewFlag(long Id, string ConversationUuid, string? MessageUuid, string Note, string CreatedAt, string UpdatedAt)
{
    public bool IsConversation => MessageUuid is null;
}

/// <summary>
/// The reviewer's own flags and notes for one library. Kept in a separate database from the library copy, because the
/// library copy can be rebuilt from the export at any time and this is work that can't be. Records refer to
/// conversations and messages by their export IDs, which stay the same across rebuilds and later exports.
/// </summary>
public sealed class ReviewStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS flags(
            id INTEGER PRIMARY KEY,
            conversation_uuid TEXT NOT NULL,
            message_uuid TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(conversation_uuid, message_uuid));
        """;

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public string? FilePath { get; }

    public event Action? Changed;

    private ReviewStore(string connectionString, string? filePath)
    {
        _connectionString = connectionString;
        FilePath = filePath;
        if (filePath is null)
        {
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
        }
        using var c = Open();
        Exec(c, Schema);
    }

    public static ReviewStore OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new ReviewStore(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString(), path);
    }

    public static ReviewStore OpenInMemory() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = "review-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString(), null);

    public bool InMemory => FilePath is null;

    /// <summary>Records which library these flags belong to, so Settings can show it.</summary>
    public void SetLibraryLabel(string label)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ('library', $v)";
        cmd.Parameters.AddWithValue("$v", label);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ReviewFlag> All() => Query("SELECT * FROM flags ORDER BY updated_at DESC");

    public IReadOnlyList<ReviewFlag> ForConversation(string conversationUuid) =>
        Query("SELECT * FROM flags WHERE conversation_uuid=$c ORDER BY id", ("$c", conversationUuid));

    public ReviewFlag? Get(string conversationUuid, string? messageUuid) =>
        Query("SELECT * FROM flags WHERE conversation_uuid=$c AND message_uuid=$m", ("$c", conversationUuid), ("$m", messageUuid ?? "")).FirstOrDefault();

    /// <summary>Conversations with any flag, on the conversation or on one of its messages.</summary>
    public IReadOnlySet<string> FlaggedConversations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT conversation_uuid FROM flags";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public int Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM flags";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Flags the conversation (or one message in it) or updates the note on an existing flag.</summary>
    public ReviewFlag Set(string conversationUuid, string? messageUuid, string? note)
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO flags(conversation_uuid, message_uuid, note, created_at, updated_at) VALUES($c, $m, $n, $t, $t)
                ON CONFLICT(conversation_uuid, message_uuid) DO UPDATE SET note=excluded.note, updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$c", conversationUuid);
            cmd.Parameters.AddWithValue("$m", messageUuid ?? "");
            cmd.Parameters.AddWithValue("$n", (note ?? "").Trim());
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke();
        return Get(conversationUuid, messageUuid)!;
    }

    public void Remove(string conversationUuid, string? messageUuid)
    {
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM flags WHERE conversation_uuid=$c AND message_uuid=$m";
            cmd.Parameters.AddWithValue("$c", conversationUuid);
            cmd.Parameters.AddWithValue("$m", messageUuid ?? "");
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        using (var c = Open()) Exec(c, "DELETE FROM flags;");
        Changed?.Invoke();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        if (!InMemory) Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        return c;
    }

    private List<ReviewFlag> Query(string sql, params (string Name, object Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<ReviewFlag>();
        while (r.Read())
        {
            var m = r.GetString(r.GetOrdinal("message_uuid"));
            list.Add(new ReviewFlag(r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("conversation_uuid")), m.Length == 0 ? null : m,
                r.GetString(r.GetOrdinal("note")), r.GetString(r.GetOrdinal("created_at")), r.GetString(r.GetOrdinal("updated_at"))));
        }
        return list;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The library a review database belongs to, read without opening it for writing (for Settings).</summary>
    public static string? ReadLibraryLabel(string path)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='library'";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
