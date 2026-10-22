using System.Text;
using System.Text.Json;
using ExportViewer.Core.Conversations;
using ExportViewer.Core.Json;
using ExportViewer.Core.Model;
using ExportViewer.Core.Parsing;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Store;

public sealed record ExportInfo(string Folder, string? CreatedAt, int? TotalFiles);

public sealed record Overview(
    int Users, int Conversations, int Messages, int Projects, int Memories, int Artifacts, int GenericRecords,
    string? FirstActivity, string? LastActivity, int FlaggedHigh, int FlaggedMedium, int FlaggedLow, IReadOnlyList<ExportInfo> Exports);

public sealed record ActivityPoint(string AccountUuid, string CreatedAt);

public sealed record UserSummary(
    string Uuid, string? FullName, string? Email, string? Phone, int Conversations, int Messages, string? FirstActivity, string? LastActivity,
    int Logins, string? LastLogin, bool InUserList, string? Raw)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(FullName) ? FullName! : Email ?? "Unknown person";
}

public sealed record LoginRow(string? Timestamp, string? Ip, string? Browser, string? Os, string? Device, string? Method, string Raw);

public enum ConversationSort { Newest, Oldest, Longest, Shortest, TitleAz }

public sealed record ConversationFilter(
    string? AccountUuid = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? TitleContains = null,
    bool HasAttachments = false,
    bool HasToolUse = false,
    bool HasBranches = false,
    Severity MinIntegrity = Severity.None,
    ConversationSort Sort = ConversationSort.Newest,
    int Limit = 100,
    int Offset = 0);

public sealed record ConversationListItem(
    string Uuid, string Title, string? Summary, string? AccountUuid, string? UserName, string? CreatedAt, string? UpdatedAt,
    string? FirstMessageAt, string? LastMessageAt, int MessageCount, int TotalMessages, MessageTraits Traits, int ForkCount, Severity Integrity);

public sealed record MessageRow(string Uuid, string? ParentUuid, string Sender, string? CreatedAt, int Seq, MessageTraits Traits, string Raw);

public sealed record IntegrityRow(Severity Severity, string Code, string Detail, string? MessageUuid);

public sealed record ConversationDetail(ConversationListItem Header, string Raw, IReadOnlyList<MessageRow> Messages, IReadOnlyList<IntegrityRow> Flags, IReadOnlyList<string> Sources);

public sealed record ProjectSummary(
    string Uuid, string? Name, string? Description, bool IsPrivate, bool IsStarter, string? CreatedAt, string? UpdatedAt,
    string? CreatorUuid, string? CreatorName, int Docs);

public sealed record ProjectDocRow(string Uuid, string? Filename, string? Content, string? CreatedAt);

public sealed record ProjectMemoryRow(string AccountUuid, string? UserName, string Content);

public sealed record ProjectDetail(ProjectSummary Summary, string? PromptTemplate, string Raw, IReadOnlyList<ProjectDocRow> Docs,
    IReadOnlyList<ProjectMemoryRow> Memories, IReadOnlyList<string> Sources);

public sealed record MemorySnapshotRow(long Id, string AccountUuid, string? SnapshotAt, string SourcePath);

public sealed record MemoryPerson(string AccountUuid, string? UserName, string? LatestAt, int Snapshots, int Files);

public sealed record ArtifactSummary(string Id, string? Kind, string? Visibility, string? OwnerUuid, string? OwnerName, string? UpdatedAt,
    string? Title, int Versions, int VersionsWithContent, bool HasPage);

public sealed record ArtifactVersionRow(string VersionId, string? Title, string? CreatedAt, string? Description, string? ContentFolder);

public sealed record ArtifactDetail(ArtifactSummary Summary, string? ActiveVersion, string Raw, string Folder, string? PageMarkdown, string? CommentsRaw,
    string? ArtifactCommentsRaw, IReadOnlyList<ArtifactVersionRow> Versions, IReadOnlyList<string> Sources);

public sealed record CollectionInfo(string Name, int Records);

public sealed record GenericRow(long Id, string? Key, string? Label, string Raw);

public sealed record SourceFileRow(string Path, string Kind, string? Collection, long Size, int Records, int Skipped, string Status, string? Note);

public sealed record LoadIssueRow(string? Path, int Severity, string Message);

/// <summary>Read-only queries behind every screen.</summary>
public sealed class LibraryQueries(LibraryDatabase db)
{
    public Overview GetOverview()
    {
        using var c = db.Open();
        int Count(string sql) => Convert.ToInt32(Scalar(c, sql));
        var exports = new List<ExportInfo>();
        using (var r = Reader(c, "SELECT folder, created_at, total_files FROM exports ORDER BY created_at"))
            while (r.Read()) exports.Add(new ExportInfo(r.GetString(0), S(r, 1), r.IsDBNull(2) ? null : r.GetInt32(2)));
        using var range = Reader(c, "SELECT MIN(first_message_at), MAX(last_message_at) FROM conversations");
        range.Read();
        return new Overview(
            Count("SELECT count(*) FROM (SELECT uuid FROM users UNION SELECT account_uuid FROM conversations WHERE account_uuid IS NOT NULL)"),
            Count("SELECT count(*) FROM conversations"),
            Count("SELECT count(*) FROM messages"),
            Count("SELECT count(*) FROM projects"),
            Count("SELECT count(DISTINCT account_uuid) FROM memory_snapshots"),
            Count("SELECT count(*) FROM artifacts"),
            Count("SELECT count(*) FROM generic_records"),
            S(range, 0), S(range, 1),
            Count("SELECT count(*) FROM conversations WHERE integrity=3"),
            Count("SELECT count(*) FROM conversations WHERE integrity=2"),
            Count("SELECT count(*) FROM conversations WHERE integrity=1"),
            exports);
    }

    /// <summary>Timestamps of every message people sent, for activity charts (grouped by local day in the UI).</summary>
    public IReadOnlyList<ActivityPoint> GetActivity()
    {
        using var c = db.Open();
        var list = new List<ActivityPoint>();
        using var r = Reader(c, """
            SELECT c.account_uuid, m.created_at FROM messages m JOIN conversations c ON c.uuid = m.conversation_uuid
            WHERE m.sender = 'human' AND m.created_at IS NOT NULL AND c.account_uuid IS NOT NULL
            """);
        while (r.Read()) list.Add(new ActivityPoint(r.GetString(0), r.GetString(1)));
        return list;
    }

    public IReadOnlyList<UserSummary> GetUsers()
    {
        using var c = db.Open();
        var list = new List<UserSummary>();
        using var r = Reader(c, """
            WITH ids AS (SELECT uuid FROM users UNION SELECT account_uuid FROM conversations WHERE account_uuid IS NOT NULL),
            conv AS (SELECT account_uuid, count(*) n, sum(total_messages) msgs, min(first_message_at) first_at, max(last_message_at) last_at
                     FROM conversations GROUP BY account_uuid),
            lg AS (SELECT account_uuid, count(*) n, max(ts) last_ts FROM logins GROUP BY account_uuid)
            SELECT ids.uuid, u.full_name, u.email, u.phone, COALESCE(conv.n,0), COALESCE(conv.msgs,0), conv.first_at, conv.last_at,
                   COALESCE(lg.n,0), lg.last_ts, u.uuid IS NOT NULL, u.raw
            FROM ids LEFT JOIN users u ON u.uuid = ids.uuid LEFT JOIN conv ON conv.account_uuid = ids.uuid LEFT JOIN lg ON lg.account_uuid = ids.uuid
            ORDER BY COALESCE(conv.msgs,0) DESC, u.full_name
            """);
        while (r.Read())
            list.Add(new UserSummary(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), r.GetInt32(4), r.GetInt32(5), S(r, 6), S(r, 7), r.GetInt32(8), S(r, 9),
                r.GetBoolean(10), S(r, 11)));
        return list;
    }

    public UserSummary? GetUser(string uuid) => GetUsers().FirstOrDefault(u => u.Uuid == uuid);

    public IReadOnlyDictionary<string, string> GetUserNames() =>
        GetUsers().ToDictionary(u => u.Uuid, u => u.DisplayName, StringComparer.Ordinal);

    public IReadOnlyList<LoginRow> GetLogins(string accountUuid)
    {
        using var c = db.Open();
        var list = new List<LoginRow>();
        using var r = Reader(c, "SELECT ts, ip, browser, os, device, method, raw FROM logins WHERE account_uuid=$1 ORDER BY ts DESC", accountUuid);
        while (r.Read()) list.Add(new LoginRow(S(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), r.GetString(6)));
        return list;
    }

    public (int Total, IReadOnlyList<ConversationListItem> Items) GetConversations(ConversationFilter f)
    {
        var where = new StringBuilder("1=1");
        var args = new List<object?>();
        string Arg(object? v) { args.Add(v); return "$" + args.Count; }
        if (f.AccountUuid is not null) where.Append($" AND c.account_uuid = {Arg(f.AccountUuid)}");
        if (f.From is { } from) where.Append($" AND c.last_message_at >= {Arg(JsonExt.FormatUtc(from))}");
        if (f.To is { } to) where.Append($" AND c.first_message_at < {Arg(JsonExt.FormatUtc(to))}");
        if (!string.IsNullOrWhiteSpace(f.TitleContains))
            where.Append($" AND (c.title LIKE {Arg("%" + EscapeLike(f.TitleContains.Trim()) + "%")} ESCAPE '\\' OR c.summary LIKE ${args.Count} ESCAPE '\\')");
        var mask = (f.HasAttachments ? (int)(MessageTraits.HasAttachments | MessageTraits.HasFiles) : 0);
        if (f.HasAttachments) where.Append($" AND (c.traits & {mask}) <> 0");
        if (f.HasToolUse) where.Append($" AND (c.traits & {(int)MessageTraits.HasToolUse}) <> 0");
        if (f.HasBranches) where.Append(" AND c.fork_count > 0");
        if (f.MinIntegrity > Severity.None) where.Append($" AND c.integrity >= {(int)f.MinIntegrity}");
        var order = f.Sort switch
        {
            ConversationSort.Oldest => "c.last_message_at ASC",
            ConversationSort.Longest => "c.message_count DESC, c.last_message_at DESC",
            ConversationSort.Shortest => "c.message_count ASC, c.last_message_at DESC",
            ConversationSort.TitleAz => "c.title COLLATE NOCASE ASC",
            _ => "c.last_message_at DESC",
        };

        using var c = db.Open();
        var total = Convert.ToInt32(Scalar(c, $"SELECT count(*) FROM conversations c WHERE {where}", args.ToArray()));
        var items = new List<ConversationListItem>();
        var all = args.Append(f.Limit).Append(f.Offset).ToArray();
        using var r = Reader(c, $"{ConversationSelect} WHERE {where} ORDER BY {order} LIMIT ${args.Count + 1} OFFSET ${args.Count + 2}", all);
        while (r.Read()) items.Add(ReadConversation(r));
        return (total, items);
    }

    private const string ConversationSelect = """
        SELECT c.uuid, c.title, c.summary, c.account_uuid, u.full_name, c.created_at, c.updated_at, c.first_message_at, c.last_message_at,
               c.message_count, c.total_messages, c.traits, c.fork_count, c.integrity, c.raw, u.email
        FROM conversations c LEFT JOIN users u ON u.uuid = c.account_uuid
        """;

    private static ConversationListItem ReadConversation(SqliteDataReader r) =>
        new(r.GetString(0), S(r, 1) ?? "(untitled conversation)", NullIfEmpty(S(r, 2)), S(r, 3), S(r, 4) ?? S(r, 15), S(r, 5), S(r, 6), S(r, 7), S(r, 8),
            r.GetInt32(9), r.GetInt32(10), (MessageTraits)r.GetInt32(11), r.GetInt32(12), (Severity)r.GetInt32(13));

    public ConversationDetail? GetConversation(string uuid)
    {
        using var c = db.Open();
        ConversationListItem header;
        string raw;
        using (var r = Reader(c, $"{ConversationSelect} WHERE c.uuid = $1", uuid))
        {
            if (!r.Read()) return null;
            header = ReadConversation(r);
            raw = r.GetString(14);
        }
        var msgs = new List<MessageRow>();
        using (var r = Reader(c, "SELECT uuid, parent_uuid, sender, created_at, seq, traits, raw FROM messages WHERE conversation_uuid=$1 ORDER BY seq", uuid))
            while (r.Read()) msgs.Add(new MessageRow(r.GetString(0), S(r, 1), S(r, 2) ?? "unknown", S(r, 3), r.GetInt32(4), (MessageTraits)r.GetInt32(5), r.GetString(6)));
        var flags = new List<IntegrityRow>();
        using (var r = Reader(c, "SELECT severity, code, detail, message_uuid FROM integrity_flags WHERE conversation_uuid=$1 ORDER BY severity DESC, id", uuid))
            while (r.Read()) flags.Add(new IntegrityRow((Severity)r.GetInt32(0), r.GetString(1), r.GetString(2), S(r, 3)));
        return new ConversationDetail(header, raw, msgs, flags, Sources(c, "conversation", uuid));
    }

    public IReadOnlyList<ProjectSummary> GetProjects()
    {
        using var c = db.Open();
        var list = new List<ProjectSummary>();
        using var r = Reader(c, $"{ProjectSelect} ORDER BY p.updated_at DESC");
        while (r.Read()) list.Add(ReadProject(r));
        return list;
    }

    private const string ProjectSelect = """
        SELECT p.uuid, p.name, p.description, p.is_private, p.is_starter, p.created_at, p.updated_at, p.creator_uuid,
               COALESCE(u.full_name, p.creator_name), (SELECT count(*) FROM project_docs d WHERE d.project_uuid = p.uuid), p.prompt_template, p.raw
        FROM projects p LEFT JOIN users u ON u.uuid = p.creator_uuid
        """;

    private static ProjectSummary ReadProject(SqliteDataReader r) =>
        new(r.GetString(0), S(r, 1), S(r, 2), r.GetInt32(3) != 0, r.GetInt32(4) != 0, S(r, 5), S(r, 6), S(r, 7), S(r, 8), r.GetInt32(9));

    public ProjectDetail? GetProject(string uuid)
    {
        using var c = db.Open();
        ProjectSummary summary;
        string? template;
        string raw;
        using (var r = Reader(c, $"{ProjectSelect} WHERE p.uuid = $1", uuid))
        {
            if (!r.Read()) return null;
            summary = ReadProject(r);
            template = S(r, 10);
            raw = r.GetString(11);
        }
        var docs = new List<ProjectDocRow>();
        using (var r = Reader(c, "SELECT uuid, filename, content, created_at FROM project_docs WHERE project_uuid=$1 ORDER BY created_at", uuid))
            while (r.Read()) docs.Add(new ProjectDocRow(r.GetString(0), S(r, 1), S(r, 2), S(r, 3)));

        var names = GetUserNames();
        var memories = LatestSnapshots(c)
            .SelectMany(s => s.Record.ProjectMemories.Where(p => p.ProjectUuid == uuid)
                .Select(p => new ProjectMemoryRow(s.Record.AccountUuid, names.GetValueOrDefault(s.Record.AccountUuid), p.Content)))
            .ToList();
        return new ProjectDetail(summary, template, raw, docs, memories, Sources(c, "project", uuid));
    }

    public IReadOnlyList<MemoryPerson> GetMemoryPeople()
    {
        using var c = db.Open();
        var names = GetUserNames();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var r = Reader(c, "SELECT account_uuid, count(*) FROM memory_snapshots GROUP BY account_uuid"))
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        return LatestSnapshots(c)
            .Select(s => new MemoryPerson(s.Record.AccountUuid, names.GetValueOrDefault(s.Record.AccountUuid), s.Record.SnapshotAt,
                counts.GetValueOrDefault(s.Record.AccountUuid), s.Record.Files.Count))
            .OrderBy(p => p.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<MemorySnapshotRow> GetMemorySnapshots(string accountUuid)
    {
        using var c = db.Open();
        var list = new List<MemorySnapshotRow>();
        using var r = Reader(c, """
            SELECT s.id, s.account_uuid, s.snapshot_at, f.path FROM memory_snapshots s JOIN source_files f ON f.id = s.file_id
            WHERE s.account_uuid=$1 ORDER BY COALESCE(s.snapshot_at,'') DESC, s.file_id DESC
            """, accountUuid);
        while (r.Read()) list.Add(new MemorySnapshotRow(r.GetInt64(0), r.GetString(1), S(r, 2), r.GetString(3)));
        return list;
    }

    public MemoryRecord? GetMemorySnapshot(long id)
    {
        using var c = db.Open();
        using var r = Reader(c, "SELECT account_uuid, raw FROM memory_snapshots WHERE id=$1", id);
        if (!r.Read()) return null;
        using var doc = JsonDocument.Parse(r.GetString(1));
        return Mappers.Memory(doc.RootElement, r.GetString(0));
    }

    private static List<(long Id, MemoryRecord Record)> LatestSnapshots(SqliteConnection c)
    {
        var list = new List<(long, MemoryRecord)>();
        using var r = Reader(c, """
            SELECT id, account_uuid, raw FROM memory_snapshots s
            WHERE id = (SELECT id FROM memory_snapshots t WHERE t.account_uuid = s.account_uuid ORDER BY COALESCE(snapshot_at,'') DESC, file_id DESC LIMIT 1)
            """);
        while (r.Read())
        {
            using var doc = JsonDocument.Parse(r.GetString(2));
            list.Add((r.GetInt64(0), Mappers.Memory(doc.RootElement, r.GetString(1))));
        }
        return list;
    }

    public IReadOnlyList<ArtifactSummary> GetArtifacts()
    {
        using var c = db.Open();
        var list = new List<ArtifactSummary>();
        using var r = Reader(c, $"{ArtifactSelect} ORDER BY a.updated_at DESC");
        while (r.Read()) list.Add(ReadArtifact(r));
        return list;
    }

    private const string ArtifactSelect = """
        SELECT a.id, a.kind, a.visibility, a.owner_uuid, u.full_name, a.updated_at, a.title,
               (SELECT count(*) FROM artifact_versions v WHERE v.artifact_id = a.id),
               (SELECT count(*) FROM artifact_versions v WHERE v.artifact_id = a.id AND v.content_folder IS NOT NULL),
               a.page_markdown IS NOT NULL, a.active_version, a.raw, a.folder, a.page_markdown, a.comments_raw, a.artifact_comments_raw
        FROM artifacts a LEFT JOIN users u ON u.uuid = a.owner_uuid
        """;

    private static ArtifactSummary ReadArtifact(SqliteDataReader r) =>
        new(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), S(r, 6), r.GetInt32(7), r.GetInt32(8), r.GetBoolean(9));

    public ArtifactDetail? GetArtifact(string id)
    {
        using var c = db.Open();
        ArtifactDetail detail;
        using (var r = Reader(c, $"{ArtifactSelect} WHERE a.id = $1", id))
        {
            if (!r.Read()) return null;
            detail = new ArtifactDetail(ReadArtifact(r), S(r, 10), r.GetString(11), r.GetString(12), S(r, 13), S(r, 14), S(r, 15), [], []);
        }
        var versions = new List<ArtifactVersionRow>();
        using (var r = Reader(c, "SELECT version_id, title, created_at, description, content_folder FROM artifact_versions WHERE artifact_id=$1 ORDER BY created_at DESC", id))
            while (r.Read()) versions.Add(new ArtifactVersionRow(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), S(r, 4)));
        return detail with { Versions = versions, Sources = Sources(c, "artifact", id) };
    }

    public IReadOnlyList<CollectionInfo> GetCollections()
    {
        using var c = db.Open();
        var list = new List<CollectionInfo>();
        using var r = Reader(c, "SELECT collection, count(*) FROM generic_records GROUP BY collection ORDER BY collection");
        while (r.Read()) list.Add(new CollectionInfo(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    public (int Total, IReadOnlyList<GenericRow> Items) GetGenericRecords(string collection, string? contains, int limit, int offset)
    {
        using var c = db.Open();
        var filter = string.IsNullOrWhiteSpace(contains) ? "" : " AND raw LIKE $2 ESCAPE '\\'";
        var pattern = "%" + EscapeLike(contains?.Trim() ?? "") + "%";
        var total = Convert.ToInt32(Scalar(c, $"SELECT count(*) FROM generic_records WHERE collection=$1{filter}", collection, pattern));
        var list = new List<GenericRow>();
        using var r = Reader(c, $"SELECT id, record_key, label, raw FROM generic_records WHERE collection=$1{filter} ORDER BY id LIMIT $3 OFFSET $4",
            collection, pattern, limit, offset);
        while (r.Read()) list.Add(new GenericRow(r.GetInt64(0), S(r, 1), S(r, 2), r.GetString(3)));
        return (total, list);
    }

    public IReadOnlyList<SourceFileRow> GetSourceFiles()
    {
        using var c = db.Open();
        var list = new List<SourceFileRow>();
        using var r = Reader(c, "SELECT path, kind, collection, size, records, skipped, status, note FROM source_files ORDER BY path");
        while (r.Read()) list.Add(new SourceFileRow(r.GetString(0), r.GetString(1), S(r, 2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6), S(r, 7)));
        return list;
    }

    public IReadOnlyList<LoadIssueRow> GetLoadIssues()
    {
        using var c = db.Open();
        var list = new List<LoadIssueRow>();
        using var r = Reader(c, "SELECT path, severity, message FROM load_issues ORDER BY severity DESC, id");
        while (r.Read()) list.Add(new LoadIssueRow(S(r, 0), r.GetInt32(1), r.GetString(2)));
        return list;
    }

    private static List<string> Sources(SqliteConnection c, string entity, string id)
    {
        var list = new List<string>();
        using var r = Reader(c, "SELECT f.path FROM record_sources s JOIN source_files f ON f.id = s.file_id WHERE s.entity=$1 AND s.id=$2 ORDER BY f.path", entity, id);
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static object? Scalar(SqliteConnection c, string sql, params object?[] args)
    {
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteScalar();
    }

    private static SqliteDataReader Reader(SqliteConnection c, string sql, params object?[] args)
    {
        var cmd = Command(c, sql, args);
        return cmd.ExecuteReader(System.Data.CommandBehavior.Default);
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            if (sql.Contains("$" + (i + 1), StringComparison.Ordinal))
                cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
        return cmd;
    }
}
