using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExportViewer.Core.Audit;

public enum FindingType
{
    TaxFileNumber,
    BusinessNumber,
    MedicareNumber,
    CardNumber,
    BankAccount,
    Iban,
    Email,
    Phone,
    IpAddress,
    ApiKey,
    Token,
    PrivateKey,
    Password,
}

public enum Confidence { Low = 1, Medium = 2, High = 3 }

/// <param name="Detector">Short stable ID of the rule that matched, shown in Expert view (for example <c>card</c> or <c>key:github</c>).</param>
public sealed record Detection(FindingType Type, int Start, int Length, Confidence Confidence, string Detector);

/// <summary>
/// Local pattern checks for personal and secret data. Numbers with a check digit (TFN, ABN, Medicare, cards, IBAN)
/// must pass it. Numbers whose check digit passes by chance too often (TFN, Medicare) also need a nearby keyword or
/// the usual grouping, so plain IDs in chat text are not reported.
/// </summary>
public static partial class SensitiveDetectors
{
    private const int ContextChars = 40;

    public static IReadOnlyList<Detection> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var all = new List<Detection>();
        var hasDigit = text.AsSpan().ContainsAnyInRange('0', '9');
        if (hasDigit)
        {
            Tfn(text, all);
            Abn(text, all);
            Medicare(text, all);
            Cards(text, all);
            Bank(text, all);
            Iban(text, all);
            Phones(text, all);
            Ips(text, all);
        }
        Emails(text, all);
        Keys(text, all);
        Jwts(text, all);
        PrivateKeys(text, all);
        Passwords(text, all);
        return ResolveOverlaps(all);
    }

    /// <summary>Where two rules match the same characters, keep the more specific one (a card number is not also a phone number).</summary>
    private static List<Detection> ResolveOverlaps(List<Detection> all)
    {
        if (all.Count < 2) return all;
        var kept = new List<Detection>();
        foreach (var d in all.OrderByDescending(d => Priority(d.Type)).ThenByDescending(d => d.Confidence).ThenByDescending(d => d.Length))
            if (!kept.Any(k => d.Start < k.Start + k.Length && k.Start < d.Start + d.Length)) kept.Add(d);
        kept.Sort((a, b) => a.Start.CompareTo(b.Start));
        return kept;
    }

    private static int Priority(FindingType t) => t switch
    {
        FindingType.PrivateKey => 20,
        FindingType.Token => 19,
        FindingType.ApiKey => 18,
        FindingType.Password => 17,
        FindingType.CardNumber => 16,
        FindingType.Iban => 15,
        FindingType.BusinessNumber => 14,
        FindingType.MedicareNumber => 13,
        FindingType.TaxFileNumber => 12,
        FindingType.BankAccount => 11,
        FindingType.Email => 10,
        FindingType.Phone => 9,
        _ => 0,
    };

    // ---- Australian identifiers ----

    private static void Tfn(string text, List<Detection> found)
    {
        foreach (Match m in TfnRegex().Matches(text))
        {
            var digits = Digits(m.Value);
            if (!IsValidTfn(digits)) continue;
            var context = HasKeyword(text, m.Index, TfnKeyword());
            var grouped = digits.Length == 9 && m.Value.Length == 11;
            if (context || grouped) found.Add(new(FindingType.TaxFileNumber, m.Index, m.Length, context ? Confidence.High : Confidence.Medium, "tfn"));
        }
    }

    private static void Abn(string text, List<Detection> found)
    {
        foreach (Match m in AbnRegex().Matches(text))
        {
            var digits = Digits(m.Value);
            if (!IsValidAbn(digits)) continue;
            var context = HasKeyword(text, m.Index, AbnKeyword());
            var grouped = m.Value.Length == 14;
            found.Add(new(FindingType.BusinessNumber, m.Index, m.Length, context ? Confidence.High : grouped ? Confidence.Medium : Confidence.Low, "abn"));
        }
    }

    private static void Medicare(string text, List<Detection> found)
    {
        foreach (Match m in MedicareRegex().Matches(text))
        {
            var digits = Digits(m.Value);
            if (!IsValidMedicare(digits)) continue;
            var context = HasKeyword(text, m.Index, MedicareKeyword());
            var grouped = m.Groups["g"].Success;
            if (context || grouped) found.Add(new(FindingType.MedicareNumber, m.Index, m.Length, context ? Confidence.High : Confidence.Medium, "medicare"));
        }
    }

    // ---- payment and bank ----

    private static void Cards(string text, List<Detection> found)
    {
        foreach (Match m in CardRegex().Matches(text))
        {
            var digits = Digits(m.Value);
            if (digits.Length is < 13 or > 19 || !HasCardPrefix(digits) || !Luhn(digits)) continue;
            var grouped = m.Value.Length > digits.Length;
            var context = HasKeyword(text, m.Index, CardKeyword());
            found.Add(new(FindingType.CardNumber, m.Index, m.Length, grouped || context ? Confidence.High : Confidence.Medium, "card"));
        }
    }

    private static void Bank(string text, List<Detection> found)
    {
        foreach (Match m in BsbAccountRegex().Matches(text))
            found.Add(new(FindingType.BankAccount, m.Index, m.Length, HasKeyword(text, m.Index, BankKeyword()) ? Confidence.High : Confidence.Medium, "bsb+account"));
        foreach (Match m in BsbRegex().Matches(text))
        {
            var g = m.Groups["v"];
            found.Add(new(FindingType.BankAccount, g.Index, g.Length, Confidence.High, "bsb"));
        }
        foreach (Match m in AccountNumberRegex().Matches(text))
        {
            var g = m.Groups["v"];
            found.Add(new(FindingType.BankAccount, g.Index, g.Length, Confidence.Medium, "account"));
        }
    }

    private static void Iban(string text, List<Detection> found)
    {
        foreach (Match m in IbanRegex().Matches(text))
        {
            var compact = m.Value.Replace(" ", "", StringComparison.Ordinal);
            if (compact.Length is >= 15 and <= 34 && IsValidIban(compact))
                found.Add(new(FindingType.Iban, m.Index, m.Length, Confidence.High, "iban"));
        }
    }

    // ---- contact details ----

    private static void Emails(string text, List<Detection> found)
    {
        if (!text.Contains('@')) return;
        foreach (Match m in EmailRegex().Matches(text))
            found.Add(new(FindingType.Email, m.Index, m.Length, Confidence.Medium, "email"));
    }

    private static void Phones(string text, List<Detection> found)
    {
        foreach (Match m in AuPhoneRegex().Matches(text))
            found.Add(new(FindingType.Phone, m.Index, m.Length, Confidence.Medium, "phone:au"));
        foreach (Match m in IntlPhoneRegex().Matches(text))
        {
            var n = Digits(m.Value).Length;
            if (n is >= 8 and <= 15) found.Add(new(FindingType.Phone, m.Index, m.Length, Confidence.Low, "phone:intl"));
        }
    }

    private static void Ips(string text, List<Detection> found)
    {
        if (!text.Contains('.')) return;
        foreach (Match m in Ipv4Regex().Matches(text))
        {
            if (m.Value is "0.0.0.0" or "127.0.0.1" or "255.255.255.255") continue;
            // "version 1.2.3.4" is a version number, not an address.
            if (HasKeyword(text, m.Index, VersionKeyword(), 12)) continue;
            found.Add(new(FindingType.IpAddress, m.Index, m.Length, Confidence.Low, "ipv4"));
        }
    }

    // ---- secrets ----

    private static readonly (string Id, Func<Regex> Pattern)[] KeyRules =
    [
        ("key:anthropic", AnthropicKeyRegex),
        ("key:openai", OpenAiKeyRegex),
        ("key:github", GitHubTokenRegex),
        ("key:gitlab", GitLabTokenRegex),
        ("key:aws", AwsKeyRegex),
        ("key:slack", SlackTokenRegex),
        ("key:google", GoogleKeyRegex),
        ("key:stripe", StripeKeyRegex),
        ("key:npm", NpmTokenRegex),
        ("key:azure-storage", AzureStorageKeyRegex),
    ];

    private static void Keys(string text, List<Detection> found)
    {
        foreach (var (id, pattern) in KeyRules)
            foreach (Match m in pattern().Matches(text))
            {
                var g = m.Groups["v"].Success ? m.Groups["v"] : m.Groups[0];
                found.Add(new(FindingType.ApiKey, g.Index, g.Length, Confidence.High, id));
            }
        foreach (Match m in GenericSecretRegex().Matches(text))
        {
            var g = m.Groups["v"];
            if (LooksLikePlaceholder(g.Value) || !g.Value.Any(char.IsDigit)) continue;
            found.Add(new(FindingType.ApiKey, g.Index, g.Length, Confidence.Medium, "key:assignment"));
        }
    }

    private static void Jwts(string text, List<Detection> found)
    {
        if (!text.Contains("eyJ", StringComparison.Ordinal)) return;
        foreach (Match m in JwtRegex().Matches(text))
            if (IsJwtHeader(m.Value[..m.Value.IndexOf('.')]))
                found.Add(new(FindingType.Token, m.Index, m.Length, Confidence.High, "jwt"));
    }

    private static void PrivateKeys(string text, List<Detection> found)
    {
        if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal)) return;
        foreach (Match m in PemRegex().Matches(text))
            found.Add(new(FindingType.PrivateKey, m.Index, m.Length, Confidence.High, "pem"));
    }

    private static void Passwords(string text, List<Detection> found)
    {
        foreach (Match m in PasswordRegex().Matches(text))
        {
            var g = m.Groups["v"];
            if (LooksLikePlaceholder(g.Value)) continue;
            // "password is incorrect" is prose; a real password said this way almost always has a digit or symbol.
            if (m.Groups["is"].Success && g.Value.All(char.IsLetter)) continue;
            found.Add(new(FindingType.Password, g.Index, g.Length, Confidence.Medium, "password"));
        }
        if (!text.Contains("://", StringComparison.Ordinal)) return;
        foreach (Match m in UrlCredentialRegex().Matches(text))
        {
            var g = m.Groups["v"];
            if (LooksLikePlaceholder(g.Value)) continue;
            found.Add(new(FindingType.Password, g.Index, g.Length, Confidence.High, "password:url"));
        }
    }

    // ---- checksums ----

    internal static bool IsValidTfn(string d)
    {
        int[] w = d.Length switch
        {
            9 => [1, 4, 3, 7, 5, 8, 6, 9, 10],
            8 => [10, 7, 8, 4, 6, 3, 5, 1],
            _ => [],
        };
        if (w.Length == 0 || d.Distinct().Count() == 1) return false;
        var sum = 0;
        for (var i = 0; i < w.Length; i++) sum += (d[i] - '0') * w[i];
        return sum % 11 == 0;
    }

    internal static bool IsValidAbn(string d)
    {
        if (d.Length != 11 || d[0] == '0') return false;
        int[] w = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];
        var sum = (d[0] - '0' - 1) * w[0];
        for (var i = 1; i < 11; i++) sum += (d[i] - '0') * w[i];
        return sum % 89 == 0;
    }

    internal static bool IsValidMedicare(string d)
    {
        if (d.Length is not (10 or 11) || d[0] is < '2' or > '6') return false;
        int[] w = [1, 3, 7, 9, 1, 3, 7, 9];
        var sum = 0;
        for (var i = 0; i < 8; i++) sum += (d[i] - '0') * w[i];
        return sum % 10 == d[8] - '0';
    }

    internal static bool Luhn(string d)
    {
        var sum = 0;
        var dbl = false;
        for (var i = d.Length - 1; i >= 0; i--)
        {
            var n = d[i] - '0';
            if (dbl && (n *= 2) > 9) n -= 9;
            sum += n;
            dbl = !dbl;
        }
        return sum % 10 == 0 && d.Distinct().Count() > 1;
    }

    private static bool HasCardPrefix(string d) =>
        d[0] == '4'                                                                     // Visa
        || (d[0] == '5' && d[1] is >= '1' and <= '5')                                   // Mastercard
        || (int.Parse(d.AsSpan(0, 4)) is >= 2221 and <= 2720)                           // Mastercard 2-series
        || d.StartsWith("34", StringComparison.Ordinal) || d.StartsWith("37", StringComparison.Ordinal) // Amex
        || d.StartsWith("6011", StringComparison.Ordinal) || d.StartsWith("65", StringComparison.Ordinal) // Discover
        || d.StartsWith("35", StringComparison.Ordinal)                                 // JCB
        || d.StartsWith("36", StringComparison.Ordinal) || d.StartsWith("38", StringComparison.Ordinal) // Diners
        || d.StartsWith("62", StringComparison.Ordinal);                                // UnionPay

    internal static bool IsValidIban(string iban)
    {
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var v = char.IsDigit(ch) ? ch - '0' : char.IsAsciiLetterUpper(ch) ? ch - 'A' + 10 : -1;
            if (v < 0) return false;
            remainder = v >= 10 ? (remainder * 100 + v) % 97 : (remainder * 10 + v) % 97;
        }
        return remainder == 1;
    }

    private static bool IsJwtHeader(string part)
    {
        try
        {
            var s = part.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("alg", out _);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikePlaceholder(string v)
    {
        var lower = v.ToLowerInvariant().Trim('"', '\'', '<', '>', '{', '}', '[', ']', '(', ')', '$', '%');
        if (lower.Length < 4) return true;
        if (lower.Distinct().Count() <= 2) return true; // ****, xxxx, ....
        string[] words = ["password", "passwd", "your", "example", "changeme", "redacted", "hidden", "secret", "null", "none", "undefined",
            "placeholder", "token", "here", "env", "getenv", "process.env", "config", "input", "value", "string", "required", "masked"];
        return words.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static string Digits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsAsciiDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static bool HasKeyword(string text, int index, Regex keyword, int window = ContextChars)
    {
        var start = Math.Max(0, index - window);
        return keyword.IsMatch(text.AsSpan(start, index - start));
    }

    // Digit runs must not be part of a longer number, word or dotted value.
    private const string NotNumberBefore = @"(?<![\w.\-/+])";
    private const string NotNumberAfter = @"(?![\w\-/]|\.\d)";

    [GeneratedRegex(NotNumberBefore + @"\d{3}[ \-]?\d{3}[ \-]?\d{2,3}" + NotNumberAfter)]
    private static partial Regex TfnRegex();

    [GeneratedRegex(@"\bTFN\b|tax\s*file", RegexOptions.IgnoreCase)]
    private static partial Regex TfnKeyword();

    [GeneratedRegex(NotNumberBefore + @"\d{2} ?\d{3} ?\d{3} ?\d{3}" + NotNumberAfter)]
    private static partial Regex AbnRegex();

    [GeneratedRegex(@"\bABN\b|business\s*number", RegexOptions.IgnoreCase)]
    private static partial Regex AbnKeyword();

    [GeneratedRegex(NotNumberBefore + @"[2-6]\d{3}(?:(?<g> )\d{5} \d|\d{6})(?:[ \-/]?[1-9])?" + NotNumberAfter)]
    private static partial Regex MedicareRegex();

    [GeneratedRegex(@"medicare", RegexOptions.IgnoreCase)]
    private static partial Regex MedicareKeyword();

    [GeneratedRegex(NotNumberBefore + @"\d(?:[ \-]?\d){12,18}" + NotNumberAfter)]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"card|visa|mastercard|amex|credit|debit|\bcc\b", RegexOptions.IgnoreCase)]
    private static partial Regex CardKeyword();

    [GeneratedRegex(NotNumberBefore + @"\d{3}-\d{3}[ ,/]+\d{6,10}" + NotNumberAfter)]
    private static partial Regex BsbAccountRegex();

    [GeneratedRegex(@"\bBSB\b[\s:#.\-]*(?<v>\d{3}[ \-]?\d{3})" + NotNumberAfter, RegexOptions.IgnoreCase)]
    private static partial Regex BsbRegex();

    [GeneratedRegex(@"\b(?:account|acct|acc)\.?\s*(?:no\.?|num(?:ber)?|#)\s*[:#]?\s*(?<v>\d{6,10})" + NotNumberAfter, RegexOptions.IgnoreCase)]
    private static partial Regex AccountNumberRegex();

    [GeneratedRegex(@"bank|account|acct|bsb|transfer|deposit", RegexOptions.IgnoreCase)]
    private static partial Regex BankKeyword();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"(?<![\w.%+\-])[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*\.[A-Za-z]{2,24}\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\w+])(?:(?:\+61[ \-]?|0)4\d{2}[ \-]?\d{3}[ \-]?\d{3}|(?:\+61[ \-]?|0)[2378][ \-]?\d{4}[ \-]?\d{4}|\(0[2378]\)[ \-]?\d{4}[ \-]?\d{4}|1[38]00[ \-]?\d{3}[ \-]?\d{3})(?![\w\-]|\.\d)")]
    private static partial Regex AuPhoneRegex();

    [GeneratedRegex(@"(?<![\w+])\+(?!61)[1-9]\d{0,2}[ \-]?(?:\(?\d{1,4}\)?[ \-]?){1,4}\d{2,4}(?![\w\-]|\.\d)")]
    private static partial Regex IntlPhoneRegex();

    [GeneratedRegex(@"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\w|\.\d)")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"version|\bv(?:er)?\.?\s*$|release|build", RegexOptions.IgnoreCase)]
    private static partial Regex VersionKeyword();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}")]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"\bsk-(?!ant-)(?:proj-|svcacct-)?[A-Za-z0-9_\-]{20,}")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bglpat-[A-Za-z0-9_\-]{20,}")]
    private static partial Regex GitLabTokenRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}")]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}")]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"\b(?:sk|rk)_live_[0-9A-Za-z]{16,}")]
    private static partial Regex StripeKeyRegex();

    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b")]
    private static partial Regex NpmTokenRegex();

    [GeneratedRegex(@"AccountKey=(?<v>[A-Za-z0-9+/]{40,}={0,2})")]
    private static partial Regex AzureStorageKeyRegex();

    [GeneratedRegex(@"\b(?:api[_\-]?key|secret[_\-]?key|access[_\-]?token|auth[_\-]?token|client[_\-]?secret|private[_\-]?token)\b[""']?\s*[:=]\s*[""']?(?<v>[A-Za-z0-9_\-./+=]{16,200})", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSecretRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----(?:[\s\S]{0,12000}?-----END (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----)?")]
    private static partial Regex PemRegex();

    [GeneratedRegex(@"\b(?:password|passwd|pwd|passphrase|passcode)\b(?:\s*[:=]|(?<is>\s+(?:is|was)\b:?))\s*[""']?(?<v>[^\s""',;<>]{4,64})", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]{1,20}://[^\s/:@]{1,64}:(?<v>[^\s/@]{3,64})@[\w.\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialRegex();
}
