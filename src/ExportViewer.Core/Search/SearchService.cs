using System.Net;
using System.Text;
using ExportViewer.Core.Store;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Search;

public enum SearchSort { Relevance, Newest, Oldest }

public sealed record SearchRequest(
    string Query,
    string? AccountUuid = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyCollection<string>? Kinds = null,
    SearchSort Sort = SearchSort.Relevance,
    int Limit = 50,
    int Offset = 0);

/// <param name="SnippetHtml">HTML-encoded excerpt with matches wrapped in &lt;mark&gt;. Safe to render.</param>
/// <param name="TargetId">What to open: conversation uuid, project uuid, memory account uuid or artifact id.</param>
public sealed record SearchHit(
    string Kind,
    string RefId,
    string TargetId,
    string? ConversationUuid,
    string? Title,
    string? AccountUuid,
    string? UserName,
    string? Timestamp,
    string SnippetHtml);

public sealed record SearchResults(string? FtsQuery, int Total, IReadOnlyList<SearchHit> Hits, string? Error = null);

public sealed class SearchService(LibraryDatabase db)
{
    private const char MarkStart = '\u0002';
    private const char MarkEnd = '\u0003';

    public SearchResults Search(SearchRequest req)
    {
        var fts = SearchQuery.ToFts(req.Query);
        if (fts is null) return new SearchResults(null, 0, []);

        var where = new StringBuilder("search_fts MATCH $q");
        var parameters = new List<(string, object)> { ("$q", fts) };
        if (req.AccountUuid is not null) { where.Append(" AND d.account_uuid = $a"); parameters.Add(("$a", req.AccountUuid)); }
        if (req.From is { } from) { where.Append(" AND d.ts >= $from"); parameters.Add(("$from", Json.JsonExt.FormatUtc(from))); }
        if (req.To is { } to) { where.Append(" AND d.ts < $to"); parameters.Add(("$to", Json.JsonExt.FormatUtc(to))); }
        if (req.Kinds is { Count: > 0 } kinds)
        {
            var names = kinds.Select((k, i) => (Name: "$k" + i, Kind: k)).ToList();
            where.Append(" AND d.kind IN (").Append(string.Join(",", names.Select(n => n.Name))).Append(')');
            parameters.AddRange(names.Select(n => (n.Name, (object)n.Kind)));
        }
        var order = req.Sort switch
        {
            SearchSort.Newest => "d.ts DESC",
            SearchSort.Oldest => "d.ts ASC",
            _ => "rank",
        };

        try
        {
            using var c = db.Open();
            int total;
            using (var count = c.CreateCommand())
            {
                count.CommandText = $"SELECT count(*) FROM search_fts JOIN search_docs d ON d.id = search_fts.rowid WHERE {where}";
                foreach (var (n, v) in parameters) count.Parameters.AddWithValue(n, v);
                total = Convert.ToInt32(count.ExecuteScalar());
            }

            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT d.kind, d.ref_id, d.conversation_uuid, d.account_uuid, d.ts,
                       snippet(search_fts, -1, char(2), char(3), '…', 24),
                       search_fts.title, c.title, u.full_name, pd.project_uuid
                FROM search_fts
                JOIN search_docs d ON d.id = search_fts.rowid
                LEFT JOIN conversations c ON c.uuid = d.conversation_uuid
                LEFT JOIN users u ON u.uuid = d.account_uuid
                LEFT JOIN project_docs pd ON d.kind = 'project_doc' AND pd.uuid = d.ref_id
                WHERE {where}
                ORDER BY {order}
                LIMIT $limit OFFSET $offset
                """;
            foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
            cmd.Parameters.AddWithValue("$limit", req.Limit);
            cmd.Parameters.AddWithValue("$offset", req.Offset);

            var hits = new List<SearchHit>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(0);
                var refId = r.GetString(1);
                var conv = Str(r, 2);
                var account = Str(r, 3);
                var docTitle = Str(r, 6);
                var target = kind switch
                {
                    SearchKind.Message or SearchKind.Tool or SearchKind.Attachment or SearchKind.Conversation => conv ?? refId,
                    SearchKind.ProjectDoc => Str(r, 9) ?? refId,
                    SearchKind.Memory => account ?? refId,
                    _ => refId,
                };
                var title = conv is not null ? Str(r, 7) ?? "(untitled conversation)" : docTitle;
                hits.Add(new SearchHit(kind, refId, target, conv, title, account, Str(r, 8), Str(r, 4), SnippetToHtml(Str(r, 5) ?? "")));
            }
            return new SearchResults(fts, total, hits);
        }
        catch (SqliteException)
        {
            return new SearchResults(fts, 0, [], "That search could not be run. Try fewer symbols or put the words in quotes.");
        }
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    internal static string SnippetToHtml(string snippet) =>
        WebUtility.HtmlEncode(snippet).Replace(MarkStart.ToString(), "<mark>").Replace(MarkEnd.ToString(), "</mark>");
}
