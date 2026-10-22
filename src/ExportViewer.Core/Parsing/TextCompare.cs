using System.Text;
using System.Text.RegularExpressions;

namespace ExportViewer.Core.Parsing;

/// <summary>
/// Compares a message's flattened <c>text</c> field with its <c>content</c> text blocks. The export swaps every
/// non-text block in <c>text</c> for a placeholder and adds whitespace, so both are normalised first.
/// </summary>
public static partial class TextCompare
{
    public const string Placeholder = "This block is not supported on your current device yet.";

    public static bool Agrees(string flatText, string joinedTextBlocks) =>
        Strip(RemovePlaceholders(flatText)) == Strip(joinedTextBlocks);

    public static string RemovePlaceholders(string text) =>
        text.Contains(Placeholder, StringComparison.Ordinal) ? PlaceholderBlock().Replace(text, "") : text;

    private static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string CollapseWhitespace(string s, int max)
    {
        var sb = new StringBuilder(Math.Min(s.Length, max));
        var space = false;
        foreach (var c in s)
        {
            if (sb.Length >= max) break;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"`{3}[^\n`]*\n?\s*This block is not supported on your current device yet\.\s*\n?`{3}")]
    private static partial Regex PlaceholderBlock();
}
