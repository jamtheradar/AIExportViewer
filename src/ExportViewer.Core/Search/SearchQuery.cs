using System.Text;

namespace ExportViewer.Core.Search;

/// <summary>
/// Translates what a person types into an FTS5 MATCH expression. Supported: plain words (all must match),
/// "exact phrases", AND / OR, a trailing * for prefix search, and -word or NOT word to exclude.
/// Everything is quoted, so other FTS5 syntax in the input is treated as text and can never cause a query error.
/// </summary>
public static class SearchQuery
{
    private enum Op { And, Or }

    private sealed record Term(string Text, bool Prefix, bool Negated);

    public static string? ToFts(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var positives = new List<(Op Join, Term Term)>();
        var negatives = new List<Term>();
        var pendingOp = Op.And;
        var negateNext = false;

        foreach (var (raw, quoted) in Tokenise(input))
        {
            if (!quoted)
            {
                var upper = raw.ToUpperInvariant();
                if (upper == "AND") { pendingOp = Op.And; continue; }
                if (upper == "OR") { pendingOp = Op.Or; continue; }
                if (upper == "NOT") { negateNext = true; continue; }
            }

            var text = raw;
            var negated = negateNext;
            negateNext = false;
            if (!quoted && text.Length > 1 && text[0] == '-') { negated = true; text = text[1..]; }
            var prefix = false;
            if (text.EndsWith('*')) { prefix = true; text = text.TrimEnd('*'); }
            if (!text.Any(char.IsLetterOrDigit)) continue;

            var term = new Term(text, prefix, negated);
            if (negated) negatives.Add(term);
            else positives.Add((positives.Count == 0 ? Op.And : pendingOp, term));
            pendingOp = Op.And;
        }

        // FTS5 cannot run a query that only excludes.
        if (positives.Count == 0) return null;

        var sb = new StringBuilder();
        var grouped = negatives.Count > 0 && positives.Count > 1;
        if (grouped) sb.Append('(');
        for (var i = 0; i < positives.Count; i++)
        {
            if (i > 0) sb.Append(positives[i].Join == Op.Or ? " OR " : " AND ");
            sb.Append(Quote(positives[i].Term));
        }
        if (grouped) sb.Append(')');
        foreach (var n in negatives) sb.Append(" NOT ").Append(Quote(n));
        return sb.ToString();
    }

    private static string Quote(Term t) =>
        "\"" + t.Text.Replace("\"", "\"\"") + "\"" + (t.Prefix ? "*" : "");

    private static IEnumerable<(string Text, bool Quoted)> Tokenise(string input)
    {
        var i = 0;
        while (i < input.Length)
        {
            var ch = input[i];
            if (char.IsWhiteSpace(ch) || ch is '(' or ')') { i++; continue; }
            if (ch is '"' or '“' or '”')
            {
                var end = input.IndexOfAny(['"', '“', '”'], i + 1);
                var phrase = end < 0 ? input[(i + 1)..] : input[(i + 1)..end];
                i = end < 0 ? input.Length : end + 1;
                var prefix = i < input.Length && input[i] == '*';
                if (prefix) i++;
                if (phrase.Trim().Length > 0) yield return (phrase.Trim() + (prefix ? "*" : ""), true);
                continue;
            }
            var start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] is not ('"' or '“' or '”' or '(' or ')')) i++;
            yield return (input[start..i], false);
        }
    }
}
