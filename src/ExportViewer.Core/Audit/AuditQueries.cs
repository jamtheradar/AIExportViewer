using System.Globalization;
using System.Text;
using System.Text.Json;
using ExportViewer.Core.Json;
using ExportViewer.Core.Store;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Audit;

/// <param name="LibraryChanged">More files were loaded after the check ran.</param>
/// <param name="RulesChanged">The app's detection rules are newer than the ones the check used.</param>
public sealed record ScanInfo(DateTimeOffset? At, int Items, IReadOnlyList<string> Problems, bool LibraryChanged, bool RulesChanged, string? WatchlistFingerprint)
{
    public bool HasRun => At is not null;
}

public sealed record FindingFilter(
    string Source = FindingSource.Sensitive,
    string? Type = null,
    string? ListId = null,
    string? AccountUuid = null,
    string? Location = null,
    string? ConversationUuid = null,
    Confidence MinConfidence = Confidence.Low,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 50,
    int Offset = 0);

/// <param name="TargetId">What to open: conversation, project or artifact ID, or the person's account ID for memories, user details and sign-ins.</param>
/// <param name="ContextBefore">Text just before the match, with any other sensitive values in it masked.</param>
public sealed record FindingRow(
    long Id, string Source, string Type, string? ListId, string Detector, Confidence Confidence, string Location, string? RefId, string? TargetId,
    string? ConversationUuid, string? Title, string? AccountUuid, string? UserName, string? Timestamp, string Masked, string ContextBefore, string ContextAfter);

public sealed record FindingCount(string Type, string? ListId, int Count, int Conversations, int People);

public sealed class AuditQueries(LibraryDatabase db)
{
    public ScanInfo GetScanInfo()
    {
        var at = db.GetMeta("scan_at");
        if (at is null) return new ScanInfo(null, 0, [], false, false, null);
        IReadOnlyList<string> problems = [];
        try { problems = JsonSerializer.Deserialize<List<string>>(db.GetMeta("scan_problems") ?? "[]") ?? []; }
        catch (JsonException) { }
        return new ScanInfo(
            DateTimeOffset.Parse(at, CultureInfo.InvariantCulture),
            int.TryParse(db.GetMeta("scan_items"), out var n) ? n : 0,
            problems,
            db.GetMeta("scan_loaded") != db.GetMeta("last_loaded"),
            db.GetMeta("scan_rules") != AuditScanner.RulesVersion,
            db.GetMeta("scan_watchlists"));
    }

    public IReadOnlyList<FindingCount> GetCounts(string source, Confidence minConfidence = Confidence.Low)
    {
        using var c = db.Open();
        var list = new List<FindingCount>();
        using var r = Reader(c, """
            SELECT type, list_id, count(*), count(DISTINCT conversation_uuid), count(DISTINCT account_uuid)
            FROM scan_findings WHERE source=$1 AND confidence >= $2 GROUP BY type, list_id ORDER BY count(*) DESC
            """, source, (int)minConfidence);
        while (r.Read()) list.Add(new FindingCount(r.GetString(0), S(r, 1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>Number of findings per source in one conversation, for the banner on the conversation screen. Unsure matches are left out, as on the list.</summary>
    public IReadOnlyDictionary<string, int> CountForConversation(string conversationUuid, Confidence minConfidence = Confidence.Medium)
    {
        using var c = db.Open();
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        using var r = Reader(c, "SELECT source, count(*) FROM scan_findings WHERE conversation_uuid=$1 AND confidence >= $2 GROUP BY source",
            conversationUuid, (int)minConfidence);
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    public (int Total, IReadOnlyList<FindingRow> Items) GetFindings(FindingFilter f)
    {
        var where = new StringBuilder("s.source = $1");
        var args = new List<object?> { f.Source };
        string Arg(object? v) { args.Add(v); return "$" + args.Count; }
        if (f.Type is not null) where.Append($" AND s.type = {Arg(f.Type)}");
        if (f.ListId is not null) where.Append($" AND s.list_id = {Arg(f.ListId)}");
        if (f.AccountUuid is not null) where.Append($" AND s.account_uuid = {Arg(f.AccountUuid)}");
        if (f.Location is not null) where.Append($" AND s.location = {Arg(f.Location)}");
        if (f.ConversationUuid is not null) where.Append($" AND s.conversation_uuid = {Arg(f.ConversationUuid)}");
        if (f.MinConfidence > Confidence.Low) where.Append($" AND s.confidence >= {(int)f.MinConfidence}");
        if (f.From is { } from) where.Append($" AND s.ts >= {Arg(JsonExt.FormatUtc(from))}");
        if (f.To is { } to) where.Append($" AND s.ts < {Arg(JsonExt.FormatUtc(to))}");

        using var c = db.Open();
        var total = Convert.ToInt32(Scalar(c, $"SELECT count(*) FROM scan_findings s WHERE {where}", args.ToArray()));
        var rows = new List<(long Id, string Source, string Type, string? List, string Detector, Confidence Conf, long? Doc, bool InTitle, int? Start, int? Length,
            string Location, string? RefId, string? Conv, string? Account, string? Ts, string Masked, string? ConvTitle, string? UserName, string? ProjectUuid,
            string? ProjectName, string? ArtifactTitle)>();
        var all = args.Append(f.Limit).Append(f.Offset).ToArray();
        using (var r = Reader(c, $"""
            SELECT s.id, s.source, s.type, s.list_id, s.detector, s.confidence, s.doc_id, s.in_title, s.start, s.length, s.location, s.ref_id,
                   s.conversation_uuid, s.account_uuid, s.ts, s.masked, cv.title, COALESCE(u.full_name, u.email),
                   COALESCE(p.uuid, pd.project_uuid), COALESCE(p.name, pp.name), a.title
            FROM scan_findings s
            LEFT JOIN conversations cv ON cv.uuid = s.conversation_uuid
            LEFT JOIN users u ON u.uuid = s.account_uuid
            LEFT JOIN projects p ON s.location = 'project' AND p.uuid = s.ref_id
            LEFT JOIN project_docs pd ON s.location = 'project_doc' AND pd.uuid = s.ref_id
            LEFT JOIN projects pp ON pp.uuid = pd.project_uuid
            LEFT JOIN artifacts a ON s.location = 'artifact' AND a.id = s.ref_id
            WHERE {where}
            ORDER BY s.ts DESC, s.id
            LIMIT ${args.Count + 1} OFFSET ${args.Count + 2}
            """, all))
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), S(r, 3), r.GetString(4), (Confidence)r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                    r.GetInt32(7) != 0, r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.GetString(10), S(r, 11), S(r, 12), S(r, 13),
                    S(r, 14), r.GetString(15), S(r, 16), S(r, 17), S(r, 18), S(r, 19), S(r, 20)));

        var texts = new Dictionary<(long, bool), MaskedText?>();
        var items = new List<FindingRow>(rows.Count);
        foreach (var x in rows)
        {
            string before = "", after = "";
            if (x.Doc is { } doc && x.Start is { } start && x.Length is { } length)
            {
                if (!texts.TryGetValue((doc, x.InTitle), out var text)) texts[(doc, x.InTitle)] = text = LoadMasked(c, doc, x.InTitle);
                if (text is not null) (before, after) = text.Context(start, length);
            }
            var (target, title) = x.Location switch
            {
                SearchKind.Project or SearchKind.ProjectDoc => (x.ProjectUuid, x.ProjectName ?? "(unnamed project)"),
                SearchKind.Artifact => (x.RefId, x.ArtifactTitle ?? "(untitled artifact)"),
                SearchKind.Memory => (x.Account, "Memory"),
                FindingLocation.UserDetails => (x.Account, "User details"),
                FindingLocation.SignIn => (x.Account, "Sign-in history"),
                _ => (x.Conv, x.ConvTitle ?? "(untitled conversation)"),
            };
            // Titles are scanned too, so the title shown next to a finding is masked like its context.
            items.Add(new FindingRow(x.Id, x.Source, x.Type, x.List, x.Detector, x.Conf, x.Location, x.RefId, target, x.Conv, Masking.MaskAll(title), x.Account, x.UserName, x.Ts,
                x.Masked, before, after));
        }
        return (total, items);
    }

    /// <summary>The full, unmasked value, read back from the library only when a reviewer asks to see it.</summary>
    public string? Reveal(long findingId)
    {
        using var c = db.Open();
        using var r = Reader(c, "SELECT doc_id, in_title, start, length, location, ref_id FROM scan_findings WHERE id=$1", findingId);
        if (!r.Read()) return null;
        var location = r.GetString(4);
        var refId = S(r, 5);
        if (location == FindingLocation.UserDetails)
            return Scalar(c, "SELECT phone FROM users WHERE uuid=$1", refId) as string;
        if (location == FindingLocation.SignIn)
            return Scalar(c, "SELECT ip FROM logins WHERE id=$1", long.TryParse(refId, out var id) ? id : -1) as string;
        if (r.IsDBNull(0) || r.IsDBNull(2) || r.IsDBNull(3)) return null;
        var text = LoadText(c, r.GetInt64(0), r.GetInt32(1) != 0);
        var start = r.GetInt32(2);
        var length = r.GetInt32(3);
        return text is not null && start + length <= text.Length ? text.Substring(start, length).ReplaceLineEndings(" ") : null;
    }

    public void Clear()
    {
        using var c = db.Open();
        LibraryDatabase.Exec(c, "DELETE FROM scan_findings; DELETE FROM meta WHERE key LIKE 'scan_%';");
    }

    private static string? LoadText(SqliteConnection c, long doc, bool inTitle) =>
        Scalar(c, $"SELECT {(inTitle ? "title" : "body")} FROM search_fts WHERE rowid=$1", doc) as string;

    /// <summary>
    /// Indexed text with every sensitive value in it masked, so context never shows a neighbouring value. The rules are
    /// run again on the text rather than trusting the stored rows, which stop at the per-item and total limits.
    /// </summary>
    private static MaskedText? LoadMasked(SqliteConnection c, long doc, bool inTitle)
    {
        var text = LoadText(c, doc, inTitle);
        if (text is null) return null;
        var spans = SensitiveDetectors.Find(text).Select(d => (d.Start, d.Length, Masking.Mask(d.Type, text.Substring(d.Start, d.Length)))).ToList();
        return new MaskedText(text, spans);
    }

    private sealed class MaskedText(string text, List<(int Start, int Length, string Masked)> spans)
    {
        public (string Before, string After) Context(int start, int length, int chars = 70)
        {
            var from = Math.Max(0, start - chars);
            var to = Math.Min(text.Length, start + length + chars);
            return ($"{(from > 0 ? "…" : "")}{Apply(from, start)}", $"{Apply(Math.Min(start + length, text.Length), to)}{(to < text.Length ? "…" : "")}");
        }

        private string Apply(int from, int to)
        {
            var sb = new StringBuilder();
            var pos = from;
            foreach (var (s, l, masked) in spans)
            {
                if (s + l <= pos || s >= to) continue;
                if (s > pos) sb.Append(text, pos, s - pos);
                sb.Append(masked);
                pos = Math.Min(to, s + l);
                if (s + l > to) break;
            }
            if (pos < to) sb.Append(text, pos, to - pos);
            return sb.ToString().ReplaceLineEndings(" ").Replace('\t', ' ');
        }
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static object? Scalar(SqliteConnection c, string sql, params object?[] args)
    {
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteScalar();
    }

    private static SqliteDataReader Reader(SqliteConnection c, string sql, params object?[] args) => Command(c, sql, args).ExecuteReader();

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
