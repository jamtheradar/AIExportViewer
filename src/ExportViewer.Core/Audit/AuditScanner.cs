using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExportViewer.Core.Store;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Audit;

public sealed record ScanProgress(int ItemsDone, int ItemsTotal, int Findings);

public sealed record ScanSummary(int Items, int Findings, int WatchlistHits, IReadOnlyList<string> Problems, TimeSpan Elapsed);

/// <summary>
/// Runs the sensitive data rules and the reviewer's watchlists over everything in the search index (messages,
/// attachments, tool activity, titles, projects, newest memories, artifacts and pages), plus phone numbers in user
/// details and IP addresses in sign-in records. Everything happens on this computer. Results replace the previous check.
/// </summary>
public sealed class AuditScanner(LibraryDatabase db)
{
    /// <summary>Bump when detection rules change so the screen can suggest checking again.</summary>
    public const string RulesVersion = "1";

    public const int MaxFindings = 250_000;
    private const int MaxPerItem = 200;

    public ScanSummary Run(IReadOnlyList<Watchlist> watchlists, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var matcher = WatchlistMatcher.Compile(watchlists, out var errors);
        var problems = new List<string>();
        if (errors.Count > 0)
            problems.Add($"{errors.Count} watchlist {(errors.Count == 1 ? "entry was" : "entries were")} skipped because {(errors.Count == 1 ? "it isn't" : "they aren't")} valid. See Watchlists.");

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, "DELETE FROM scan_findings");

        using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO scan_findings(source, type, list_id, detector, confidence, doc_id, in_title, start, length, location, ref_id,
                                      conversation_uuid, account_uuid, ts, masked)
            VALUES($source, $type, $list, $detector, $confidence, $doc, $title, $start, $length, $location, $ref, $conv, $account, $ts, $masked)
            """;
        string[] names = ["$source", "$type", "$list", "$detector", "$confidence", "$doc", "$title", "$start", "$length", "$location", "$ref", "$conv", "$account", "$ts", "$masked"];
        foreach (var n in names) insert.Parameters.Add(new SqliteParameter(n, null));

        int findings = 0, hits = 0, items = 0;
        var capped = false;
        void Add(string source, string type, string? list, string detector, Confidence confidence, long? doc, bool inTitle, int? start, int? length,
            string location, string? refId, string? conv, string? account, string? ts, string masked)
        {
            if (findings >= MaxFindings) { capped = true; return; }
            object?[] values = [source, type, list, detector, (int)confidence, doc, inTitle ? 1 : 0, start, length, location, refId, conv, account, ts, masked];
            for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
            findings++;
            if (source == FindingSource.Watchlist) hits++;
        }

        var total = Convert.ToInt32(Scalar(c, tx, "SELECT count(*) FROM search_docs"));
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT d.id, d.kind, d.ref_id, d.conversation_uuid, d.account_uuid, d.ts, f.title, f.body
                FROM search_docs d JOIN search_fts f ON f.rowid = d.id ORDER BY d.id
                """;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var doc = r.GetInt64(0);
                var kind = r.GetString(1);
                var refId = r.GetString(2);
                var conv = r.IsDBNull(3) ? null : r.GetString(3);
                var account = r.IsDBNull(4) ? null : r.GetString(4);
                var ts = r.IsDBNull(5) ? null : r.GetString(5);
                foreach (var (text, inTitle) in new[] { (r.IsDBNull(6) ? "" : r.GetString(6), true), (r.IsDBNull(7) ? "" : r.GetString(7), false) })
                {
                    if (text.Length == 0) continue;
                    var n = 0;
                    foreach (var d in SensitiveDetectors.Find(text))
                    {
                        if (++n > MaxPerItem) break;
                        Add(FindingSource.Sensitive, d.Type.ToString(), null, d.Detector, Adjust(d, kind), doc, inTitle, d.Start, d.Length, kind, refId, conv, account, ts,
                            Masking.Mask(d.Type, text.Substring(d.Start, d.Length)));
                    }
                    foreach (var m in matcher.Find(text))
                        Add(FindingSource.Watchlist, m.WatchlistName, m.WatchlistId, m.Term, Confidence.High, doc, inTitle, m.Start, m.Length, kind, refId, conv,
                            account, ts, Masking.MaskAll(OneLine(text.Substring(m.Start, m.Length))));
                }
                if (++items % 250 == 0) progress?.Report(new ScanProgress(items, total, findings));
            }
        }

        // Structured fields: phone numbers people gave, and the IP addresses they signed in from.
        using (var r = Reader(c, tx, "SELECT uuid, phone FROM users WHERE phone IS NOT NULL AND trim(phone) <> ''"))
            while (r.Read())
                Add(FindingSource.Sensitive, nameof(FindingType.Phone), null, "field:phone", Confidence.High, null, false, null, null, FindingLocation.UserDetails,
                    r.GetString(0), null, r.GetString(0), null, Masking.Mask(FindingType.Phone, r.GetString(1)));
        using (var r = Reader(c, tx, """
            SELECT account_uuid, ip, max(ts), max(id) FROM logins WHERE ip IS NOT NULL AND trim(ip) <> '' GROUP BY account_uuid, ip
            """))
            while (r.Read())
                Add(FindingSource.Sensitive, nameof(FindingType.IpAddress), null, "field:ip", Confidence.High, null, false, null, null, FindingLocation.SignIn,
                    r.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture), null, r.IsDBNull(0) ? null : r.GetString(0),
                    r.IsDBNull(2) ? null : r.GetString(2), Masking.Mask(FindingType.IpAddress, r.GetString(1)));

        problems.AddRange(matcher.Problems);
        if (capped) problems.Add($"The check stopped recording after {MaxFindings:N0} matches. Narrow the watchlists or review the most common types first.");

        SetMeta(c, tx, "scan_at", DateTimeOffset.UtcNow.ToString("O"));
        SetMeta(c, tx, "scan_rules", RulesVersion);
        SetMeta(c, tx, "scan_loaded", Scalar(c, tx, "SELECT value FROM meta WHERE key='last_loaded'") as string);
        SetMeta(c, tx, "scan_watchlists", Fingerprint(watchlists));
        SetMeta(c, tx, "scan_items", items.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(c, tx, "scan_problems", JsonSerializer.Serialize(problems));
        tx.Commit();
        progress?.Report(new ScanProgress(items, total, findings));
        return new ScanSummary(items, findings, hits, problems, sw.Elapsed);
    }

    /// <summary>
    /// Contact details in Claude's tool activity are mostly from web pages and search results Claude fetched (a
    /// business's phone number, a support address), not something a person shared, so they are marked as unsure.
    /// </summary>
    internal static Confidence Adjust(Detection d, string location) =>
        location == SearchKind.Tool && d.Type is FindingType.Email or FindingType.Phone or FindingType.IpAddress ? Confidence.Low : d.Confidence;

    /// <summary>Changes when any watchlist is added, removed or edited, so the screen can say results are out of date.</summary>
    public static string Fingerprint(IEnumerable<Watchlist> lists)
    {
        var sb = new StringBuilder();
        foreach (var l in lists.OrderBy(l => l.Id, StringComparer.Ordinal))
            sb.Append(l.Id).Append('\u0001').Append(l.Name).Append('\u0001').Append(l.Patterns).Append('\u0001').AppendJoin('\u0002', l.ActiveTerms).Append('\u0003');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ");

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static SqliteDataReader Reader(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteReader();
    }

    private static void SetMeta(SqliteConnection c, SqliteTransaction tx, string key, string? value)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
