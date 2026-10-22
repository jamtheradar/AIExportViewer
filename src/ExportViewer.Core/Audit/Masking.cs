using System.Text;

namespace ExportViewer.Core.Audit;

/// <summary>
/// Hides most of a sensitive value while leaving enough to tell matches apart (for example the last four digits of a
/// card). Masked values are what the list, reports and CSV files show unless the reviewer chooses to reveal one.
/// </summary>
public static class Masking
{
    public const char Dot = '•';

    public static string Mask(FindingType type, string value)
    {
        value = value.ReplaceLineEndings(" ");
        return type switch
        {
            FindingType.CardNumber => KeepLastDigits(value, 4),
            FindingType.TaxFileNumber or FindingType.BusinessNumber or FindingType.MedicareNumber or FindingType.BankAccount
                or FindingType.Phone => KeepLastDigits(value, 3),
            FindingType.Iban => value.Length > 6 ? value[..2] + HideAlphanumerics(value[2..^4]) + value[^4..] : new string(Dot, value.Length),
            FindingType.Email => MaskEmail(value),
            FindingType.IpAddress => MaskIp(value),
            FindingType.ApiKey or FindingType.Token => KeepPrefix(value),
            FindingType.PrivateKey => "-----BEGIN … PRIVATE KEY----- (key hidden)",
            FindingType.Password => new string(Dot, 8),
            _ => new string(Dot, Math.Min(value.Length, 12)),
        };
    }

    private static string KeepLastDigits(string value, int keep)
    {
        var total = value.Count(char.IsAsciiDigit);
        var sb = new StringBuilder(value.Length);
        var seen = 0;
        foreach (var ch in value)
        {
            if (char.IsAsciiDigit(ch)) sb.Append(++seen > total - keep ? ch : Dot);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string HideAlphanumerics(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? Dot : c).ToArray());

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0) return new string(Dot, 6);
        var domain = value[(at + 1)..];
        var lastDot = domain.LastIndexOf('.');
        var tld = lastDot >= 0 ? domain[lastDot..] : "";
        return $"{value[0]}{new string(Dot, 3)}@{new string(Dot, 3)}{tld}";
    }

    private static string MaskIp(string value)
    {
        var parts = value.Split('.');
        if (parts.Length == 4) return $"{parts[0]}.{Dot}.{Dot}.{Dot}";
        var colon = value.IndexOf(':');
        return colon > 0 ? $"{value[..colon]}:{new string(Dot, 3)}" : new string(Dot, 6);
    }

    private static readonly string[] KnownPrefixes =
        ["sk-ant-", "sk-proj-", "sk-svcacct-", "sk-", "github_pat_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "glpat-", "xoxb-", "xoxa-", "xoxp-", "xoxr-", "xoxs-",
         "sk_live_", "rk_live_", "npm_", "AKIA", "ASIA", "AIza", "eyJ"];

    /// <summary>Keeps a known prefix such as <c>ghp_</c> or <c>sk-ant-</c> so the kind of key is clear; otherwise at most 4 characters.</summary>
    private static string KeepPrefix(string value)
    {
        var prefix = KnownPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.Ordinal));
        var cut = prefix?.Length ?? Math.Min(4, value.Length / 4);
        return value[..cut] + new string(Dot, 8);
    }

    /// <summary>The text with every value the sensitive data rules recognise replaced by its masked form.</summary>
    public static string MaskAll(string text)
    {
        var found = SensitiveDetectors.Find(text);
        if (found.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var d in found)
        {
            if (d.Start < pos) continue;
            sb.Append(text, pos, d.Start - pos).Append(Mask(d.Type, text.Substring(d.Start, d.Length)));
            pos = d.Start + d.Length;
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    /// <summary>Text either side of a match, flattened to one line, so the match can be shown masked or revealed in context.</summary>
    public static (string Before, string After) Context(string text, int start, int length, int chars = 70)
    {
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + length, start, text.Length);
        var from = Math.Max(0, start - chars);
        var to = Math.Min(text.Length, end + chars);
        var before = (from > 0 ? "…" : "") + OneLine(text[from..start]);
        var after = OneLine(text[end..to]) + (to < text.Length ? "…" : "");
        return (before, after);
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Replace('\t', ' ').Replace("\u0000", "");
}
