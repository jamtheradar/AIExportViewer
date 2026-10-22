using System.Text.RegularExpressions;

namespace ExportViewer.Core.Audit;

/// <summary>
/// A reviewer's list of words to look for, such as client names or project codes. With <see cref="Patterns"/> on,
/// each line is a regular expression instead of plain text.
/// </summary>
public sealed class Watchlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Patterns { get; set; }
    public List<string> Terms { get; set; } = [];

    public IEnumerable<string> ActiveTerms => Terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed record WatchlistError(int Line, string Term, string Message);

public sealed record WatchMatch(string WatchlistId, string WatchlistName, string Term, int Start, int Length);

/// <summary>
/// Compiled watchlists. Patterns run on .NET's linear-time engine where they can, and always with a time limit, so a
/// badly written pattern can slow a check down but never hang it.
/// </summary>
public sealed class WatchlistMatcher
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>A term that times out this many times is dropped for the rest of the check.</summary>
    public const int MaxTimeouts = 5;

    private const int MaxMatchesPerItem = 200;

    private const string WordEdge = @"\b";

    private sealed class Term(string listId, string listName, string text, Regex regex)
    {
        public string ListId { get; } = listId;
        public string ListName { get; } = listName;
        public string Text { get; } = text;
        public Regex Regex { get; } = regex;
        public int Timeouts { get; set; }
    }

    private readonly List<Term> _terms;
    private readonly List<string> _problems = [];

    private WatchlistMatcher(List<Term> terms) => _terms = terms;

    public bool IsEmpty => _terms.Count == 0;

    /// <summary>Plain-English notes about terms that were too slow and were dropped.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Checks every term, returning the ones that can't be used and why. Valid terms are still compiled.</summary>
    public static WatchlistMatcher Compile(IEnumerable<Watchlist> lists, out IReadOnlyList<(Watchlist List, WatchlistError Error)> errors)
    {
        var terms = new List<Term>();
        var errs = new List<(Watchlist, WatchlistError)>();
        foreach (var list in lists)
        {
            var line = 0;
            foreach (var raw in list.Terms)
            {
                line++;
                var text = raw.Trim();
                if (text.Length == 0) continue;
                if (terms.Any(t => t.ListId == list.Id && string.Equals(t.Text, text, StringComparison.OrdinalIgnoreCase))) continue;
                if (TryBuild(text, list.Patterns, out var regex, out var error))
                    terms.Add(new Term(list.Id, list.Name, text, regex!));
                else
                    errs.Add((list, new WatchlistError(line, text, error!)));
            }
        }
        errors = errs;
        return new WatchlistMatcher(terms);
    }

    public static IReadOnlyList<WatchlistError> Validate(Watchlist list)
    {
        Compile([list], out var errors);
        return errors.Select(e => e.Error).ToList();
    }

    internal static bool TryBuild(string text, bool pattern, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        string source;
        if (pattern)
        {
            source = text;
        }
        else
        {
            // Whole words only, and any run of spaces in the term matches any run of white space in the text.
            var escaped = string.Join(@"\s+", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            source = (char.IsLetterOrDigit(text[0]) ? WordEdge : "") + escaped + (char.IsLetterOrDigit(text[^1]) ? WordEdge : "");
        }

        try
        {
            regex = new Regex(source, options | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Look-arounds and back-references need the backtracking engine; the time limit still applies.
            try { regex = new Regex(source, options, MatchTimeout); }
            catch (ArgumentException ex) { error = Describe(ex); return false; }
        }
        catch (ArgumentException ex)
        {
            error = Describe(ex);
            return false;
        }

        if (regex.IsMatch(""))
        {
            regex = null;
            error = "This pattern matches empty text, so it would match everywhere.";
            return false;
        }
        return true;
    }

    private static string Describe(ArgumentException ex) =>
        ex is RegexParseException p
            ? $"This isn't a valid pattern ({Regex.Replace(p.Error.ToString(), "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant()}, at character {p.Offset})."
            : "This isn't a valid pattern.";

    public IReadOnlyList<WatchMatch> Find(string text)
    {
        if (_terms.Count == 0 || string.IsNullOrEmpty(text)) return [];
        List<WatchMatch>? found = null;
        foreach (var t in _terms)
        {
            if (t.Timeouts >= MaxTimeouts) continue;
            try
            {
                var n = 0;
                foreach (Match m in t.Regex.Matches(text))
                {
                    if (m.Length == 0) continue;
                    (found ??= []).Add(new WatchMatch(t.ListId, t.ListName, t.Text, m.Index, m.Length));
                    if (++n >= MaxMatchesPerItem) break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                if (++t.Timeouts == MaxTimeouts)
                    _problems.Add($"“{t.Text}” in the “{t.ListName}” list was too slow to check and was skipped for the rest of the check.");
            }
        }
        return (IReadOnlyList<WatchMatch>?)found ?? [];
    }
}
