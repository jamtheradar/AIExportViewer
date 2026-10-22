using ExportViewer.Core.Model;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Store;

public static class SearchKind
{
    public const string Conversation = "conversation";
    public const string Message = "message";
    public const string Tool = "tool";
    public const string Attachment = "attachment";
    public const string Project = "project";
    public const string ProjectDoc = "project_doc";
    public const string Memory = "memory";
    public const string Artifact = "artifact";
}

/// <summary>
/// Writes records into the library inside one transaction. Every write is an upsert, so files from overlapping
/// exports can be loaded in any order.
/// </summary>
internal sealed class LibraryWriter : IDisposable
{
    private readonly SqliteConnection _c;
    private SqliteTransaction _tx;
    private readonly Dictionary<string, SqliteCommand> _cmds = new();

    public LibraryWriter(SqliteConnection connection)
    {
        _c = connection;
        _tx = _c.BeginTransaction();
    }

    public void Commit()
    {
        _tx.Commit();
        _tx.Dispose();
        _tx = _c.BeginTransaction();
        foreach (var cmd in _cmds.Values) cmd.Transaction = _tx;
    }

    public void Dispose()
    {
        foreach (var cmd in _cmds.Values) cmd.Dispose();
        _tx.Dispose();
    }

    private SqliteCommand Cmd(string sql)
    {
        if (!_cmds.TryGetValue(sql, out var cmd))
        {
            cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _tx;
            _cmds[sql] = cmd;
        }
        cmd.Parameters.Clear();
        return cmd;
    }

    private static SqliteCommand P(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), values[i] ?? DBNull.Value);
        return cmd;
    }

    // ---- files and issues ----

    public long BeginFile(string path, string kind, string? collection, long size, long mtime)
    {
        P(Cmd("""
            INSERT INTO source_files(path, kind, collection, size, mtime, status) VALUES($1,$2,$3,$4,$5,'loading')
            ON CONFLICT(path) DO UPDATE SET kind=excluded.kind, collection=excluded.collection, size=excluded.size, mtime=excluded.mtime, status='loading'
            """), path, kind, collection, size, mtime).ExecuteNonQuery();
        return (long)P(Cmd("SELECT id FROM source_files WHERE path=$1"), path).ExecuteScalar()!;
    }

    public void EndFile(long fileId, string status, int records, int skipped, string? note) =>
        P(Cmd("UPDATE source_files SET status=$2, records=$3, skipped=$4, note=$5, loaded_at=$6 WHERE id=$1"),
            fileId, status, records, skipped, note, DateTimeOffset.UtcNow.ToString("O")).ExecuteNonQuery();

    public void Issue(string? path, int severity, string message) =>
        P(Cmd("INSERT INTO load_issues(path, severity, message) VALUES($1,$2,$3)"), path, severity, message).ExecuteNonQuery();

    private void Source(string entity, string id, long fileId) =>
        P(Cmd("INSERT OR IGNORE INTO record_sources(entity, id, file_id) VALUES($1,$2,$3)"), entity, id, fileId).ExecuteNonQuery();

    // ---- search index ----

    private void Index(string kind, string refId, string? conversation, string? account, string? ts, string? title, string? body)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
        P(Cmd("INSERT INTO search_docs(kind, ref_id, conversation_uuid, account_uuid, ts) VALUES($1,$2,$3,$4,$5)"),
            kind, refId, conversation, account, ts).ExecuteNonQuery();
        var id = (long)Cmd("SELECT last_insert_rowid()").ExecuteScalar()!;
        P(Cmd("INSERT INTO search_fts(rowid, title, body) VALUES($1,$2,$3)"), id, title ?? "", body ?? "").ExecuteNonQuery();
    }

    private void Unindex(string refId, string? kind = null)
    {
        var ids = new List<long>();
        var select = kind is null
            ? P(Cmd("SELECT id FROM search_docs WHERE ref_id=$1"), refId)
            : P(Cmd("SELECT id FROM search_docs WHERE ref_id=$1 AND kind=$2"), refId, kind);
        using (var r = select.ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (var id in ids)
        {
            P(Cmd("DELETE FROM search_fts WHERE rowid=$1"), id).ExecuteNonQuery();
            P(Cmd("DELETE FROM search_docs WHERE id=$1"), id).ExecuteNonQuery();
        }
    }

    // ---- entities ----

    public void Manifest(string folder, ManifestRecord m) =>
        P(Cmd("INSERT OR REPLACE INTO exports(folder, created_at, total_files, raw) VALUES($1,$2,$3,$4)"),
            folder, m.CreatedAt, m.TotalFiles, m.Raw).ExecuteNonQuery();

    public void User(UserRecord u, long fileId)
    {
        P(Cmd("""
            INSERT INTO users(uuid, full_name, email, phone, raw) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(uuid) DO UPDATE SET
              full_name=COALESCE(NULLIF(excluded.full_name,''), users.full_name),
              email=COALESCE(NULLIF(excluded.email,''), users.email),
              phone=COALESCE(NULLIF(excluded.phone,''), users.phone),
              raw=excluded.raw
            """), u.Uuid, u.FullName, u.Email, u.Phone, u.Raw).ExecuteNonQuery();
        Source("user", u.Uuid, fileId);
    }

    public void Login(LoginRecord l) =>
        P(Cmd("""
            INSERT OR IGNORE INTO logins(account_uuid, ts, ip, browser, os, device, method, raw) VALUES($1,$2,$3,$4,$5,$6,$7,$8)
            """), l.AccountUuid, l.Timestamp, l.Ip, l.Browser, l.Os, l.Device, l.Method, l.Raw).ExecuteNonQuery();

    /// <summary>Returns true when anything in the library changed.</summary>
    public bool Conversation(ConversationRecord c, long fileId)
    {
        Source("conversation", c.Uuid, fileId);
        var existingUpdated = P(Cmd("SELECT updated_at FROM conversations WHERE uuid=$1"), c.Uuid).ExecuteScalar();
        var isNew = existingUpdated is null;
        var changed = false;

        if (isNew || string.CompareOrdinal(c.UpdatedAt, existingUpdated as string) > 0)
        {
            P(Cmd("""
                INSERT INTO conversations(uuid, name, summary, created_at, updated_at, account_uuid, raw, dirty) VALUES($1,$2,$3,$4,$5,$6,$7,1)
                ON CONFLICT(uuid) DO UPDATE SET name=excluded.name, summary=excluded.summary,
                  created_at=COALESCE(conversations.created_at, excluded.created_at), updated_at=excluded.updated_at,
                  account_uuid=COALESCE(excluded.account_uuid, conversations.account_uuid), raw=excluded.raw, dirty=1
                """), c.Uuid, c.Name, c.Summary, c.CreatedAt, c.UpdatedAt, c.AccountUuid, c.Raw).ExecuteNonQuery();
            if (!isNew) Unindex(c.Uuid, SearchKind.Conversation);
            Index(SearchKind.Conversation, c.Uuid, c.Uuid, c.AccountUuid, c.CreatedAt, c.Name, c.Summary);
            changed = true;
        }

        var existing = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!isNew)
        {
            using var r = P(Cmd("SELECT uuid, updated_at FROM messages WHERE conversation_uuid=$1"), c.Uuid).ExecuteReader();
            while (r.Read()) existing[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        }

        var account = c.AccountUuid;
        if (account is null && !isNew)
            account = P(Cmd("SELECT account_uuid FROM conversations WHERE uuid=$1"), c.Uuid).ExecuteScalar() as string;

        foreach (var m in c.Messages)
        {
            var seen = existing.TryGetValue(m.Uuid, out var prevUpdated);
            if (seen && string.CompareOrdinal(m.UpdatedAt, prevUpdated) <= 0) continue;
            P(Cmd("""
                INSERT OR REPLACE INTO messages(uuid, conversation_uuid, parent_uuid, sender, created_at, updated_at, seq, traits, preview, raw)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """), m.Uuid, c.Uuid, m.ParentUuid, m.Sender, m.CreatedAt, m.UpdatedAt, m.Seq, (int)m.Traits, m.Preview, m.Raw).ExecuteNonQuery();
            if (seen) Unindex(m.Uuid);
            Index(SearchKind.Message, m.Uuid, c.Uuid, account, m.CreatedAt, null, m.SearchText);
            Index(SearchKind.Tool, m.Uuid, c.Uuid, account, m.CreatedAt, null, m.ToolText);
            foreach (var a in m.Attachments) Index(SearchKind.Attachment, m.Uuid, c.Uuid, account, m.CreatedAt, a.FileName, a.Content);
            changed = true;
        }

        if (changed) P(Cmd("UPDATE conversations SET dirty=1 WHERE uuid=$1"), c.Uuid).ExecuteNonQuery();
        return changed;
    }

    public void Project(ProjectRecord p, long fileId)
    {
        Source("project", p.Uuid, fileId);
        var existingUpdated = P(Cmd("SELECT updated_at FROM projects WHERE uuid=$1"), p.Uuid).ExecuteScalar();
        var newer = existingUpdated is null || string.CompareOrdinal(p.UpdatedAt, existingUpdated as string) > 0;
        if (newer)
        {
            P(Cmd("""
                INSERT OR REPLACE INTO projects(uuid, name, description, is_private, is_starter, prompt_template, created_at, updated_at, creator_uuid, creator_name, raw)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                """), p.Uuid, p.Name, p.Description, p.IsPrivate ? 1 : 0, p.IsStarter ? 1 : 0, p.PromptTemplate, p.CreatedAt, p.UpdatedAt,
                p.CreatorUuid, p.CreatorName, p.Raw).ExecuteNonQuery();
            Unindex(p.Uuid, SearchKind.Project);
            Index(SearchKind.Project, p.Uuid, null, p.CreatorUuid, p.CreatedAt, p.Name, $"{p.Description}\n{p.PromptTemplate}");
        }
        foreach (var d in p.Docs)
        {
            var exists = P(Cmd("SELECT 1 FROM project_docs WHERE uuid=$1"), d.Uuid).ExecuteScalar() is not null;
            if (exists && !newer) continue;
            P(Cmd("INSERT OR REPLACE INTO project_docs(uuid, project_uuid, filename, content, created_at) VALUES($1,$2,$3,$4,$5)"),
                d.Uuid, p.Uuid, d.Filename, d.Content, d.CreatedAt).ExecuteNonQuery();
            if (exists) Unindex(d.Uuid);
            Index(SearchKind.ProjectDoc, d.Uuid, null, p.CreatorUuid, d.CreatedAt, d.Filename, d.Content);
        }
    }

    /// <summary>Creation date of the export (from its manifest) that contains this file, if known.</summary>
    public string? ExportDateFor(string relativePath) =>
        P(Cmd("""
            SELECT created_at FROM exports WHERE folder = '' OR $1 LIKE folder || '/%'
            ORDER BY length(folder) DESC LIMIT 1
            """), relativePath).ExecuteScalar() as string;

    public void Memory(MemoryRecord m, long fileId, string? fallbackDate)
    {
        P(Cmd("INSERT OR REPLACE INTO memory_snapshots(account_uuid, file_id, snapshot_at, raw) VALUES($1,$2,$3,$4)"),
            m.AccountUuid, fileId, m.SnapshotAt ?? fallbackDate, m.Raw).ExecuteNonQuery();
        Source("memory", m.AccountUuid, fileId);
    }

    public void Artifact(ArtifactRecord a, long fileId)
    {
        Source("artifact", a.Id, fileId);
        var existingUpdated = P(Cmd("SELECT updated_at FROM artifacts WHERE id=$1"), a.Id).ExecuteScalar();
        if (existingUpdated is null || string.CompareOrdinal(a.UpdatedAt, existingUpdated as string) > 0)
        {
            P(Cmd("""
                INSERT OR REPLACE INTO artifacts(id, kind, visibility, owner_uuid, updated_at, active_version, title, raw, folder, page_markdown, comments_raw, artifact_comments_raw)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """), a.Id, a.Kind, a.Visibility, a.OwnerUuid, a.UpdatedAt, a.ActiveVersion, a.Title, a.Raw, a.Folder,
                a.PageMarkdown, a.CommentsRaw, a.ArtifactCommentsRaw).ExecuteNonQuery();
            Unindex(a.Id, SearchKind.Artifact);
            var versionText = string.Join("\n", a.Versions.Select(v => $"{v.Title} {v.Description}"));
            Index(SearchKind.Artifact, a.Id, null, a.OwnerUuid, a.UpdatedAt, a.Title, $"{versionText}\n{a.PageMarkdown}");
        }
        foreach (var v in a.Versions)
        {
            P(Cmd("""
                INSERT INTO artifact_versions(artifact_id, version_id, title, created_at, description, content_folder) VALUES($1,$2,$3,$4,$5,$6)
                ON CONFLICT(artifact_id, version_id) DO UPDATE SET
                  title=COALESCE(excluded.title, artifact_versions.title),
                  description=COALESCE(excluded.description, artifact_versions.description),
                  content_folder=COALESCE(excluded.content_folder, artifact_versions.content_folder)
                """), a.Id, v.VersionId, v.Title, v.CreatedAt, v.Description, v.ContentFolder).ExecuteNonQuery();
        }
    }

    public void Generic(GenericRecord g, long fileId)
    {
        P(Cmd("""
            INSERT INTO generic_records(collection, record_key, label, raw) VALUES($1,$2,$3,$4)
            ON CONFLICT(collection, record_key) DO UPDATE SET label=excluded.label, raw=excluded.raw
            """), g.Collection, g.Key, g.Label, g.Raw).ExecuteNonQuery();
        if (g.Key is not null) Source("generic:" + g.Collection, g.Key, fileId);
    }

    // ---- finalise ----

    internal SqliteCommand Raw(string sql, params object?[] values) => P(Cmd(sql), values);

    internal void IndexMemory(string refId, string account, string? ts, string title, string body) =>
        Index(SearchKind.Memory, refId, null, account, ts, title, body);

    internal void UnindexKind(string kind)
    {
        var ids = new List<long>();
        using (var r = P(Cmd("SELECT id FROM search_docs WHERE kind=$1"), kind).ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (var id in ids) P(Cmd("DELETE FROM search_fts WHERE rowid=$1"), id).ExecuteNonQuery();
        P(Cmd("DELETE FROM search_docs WHERE kind=$1"), kind).ExecuteNonQuery();
    }
}
