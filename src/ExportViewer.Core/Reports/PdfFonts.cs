using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace ExportViewer.Core.Reports;

/// <summary>
/// Supplies PDFsharp with fonts already installed on the computer (nothing is downloaded or bundled). Text is split
/// into runs so characters the main font lacks, such as Chinese or symbols, use a fallback font that has them.
/// </summary>
internal sealed class PdfFonts : IFontResolver
{
    public const string Sans = "EV Sans";
    public const string Mono = "EV Mono";
    private const string FallbackPrefix = "EV Fallback ";

    private sealed record FontFile(string Path);

    private static readonly Lazy<PdfFonts> Shared = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object Gate = new();

    private readonly Dictionary<string, FontFile> _faces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly List<string> _fallbackFamilies = [];
    private readonly Dictionary<int, string?> _familyForCodePoint = [];
    private XFont? _sansProbe;
    private readonly List<(string Family, XFont Probe)> _fallbackProbes = [];
    private bool _probesReady;

    public static PdfFonts Instance => Shared.Value;

    /// <summary>Serialises PDF rendering: PDFsharp's font settings are process-wide.</summary>
    public static object RenderLock => Gate;

    private static PdfFonts Create()
    {
        var fonts = new PdfFonts();
        fonts.Discover();
        if (!fonts._faces.ContainsKey("sans-r"))
            throw new ReportException("No suitable font was found on this computer, so the PDF can't be made. The CSV option still works.");
        lock (Gate)
        {
            GlobalFontSettings.FontResolver ??= fonts;
        }
        return fonts;
    }

    private void Discover()
    {
        var dirs = FontDirectories().Where(Directory.Exists).ToList();
        FontFile? Find(params string[] names)
        {
            foreach (var n in names)
                foreach (var d in dirs)
                {
                    var p = System.IO.Path.Combine(d, n);
                    if (File.Exists(p)) return new FontFile(p);
                }
            return null;
        }

        // Regular, bold, italic, bold italic: the first family with a regular face wins.
        string[][] sansFamilies =
        [
            ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"],
            ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"],
            ["Arial.ttf", "Arial Bold.ttf", "Arial Italic.ttf", "Arial Bold Italic.ttf"],
            ["DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf"],
            ["LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf"],
        ];
        string[][] monoFamilies =
        [
            ["consola.ttf", "consolab.ttf"],
            ["cour.ttf", "courbd.ttf"],
            ["Courier New.ttf", "Courier New Bold.ttf"],
            ["DejaVuSansMono.ttf", "DejaVuSansMono-Bold.ttf"],
            ["LiberationMono-Regular.ttf", "LiberationMono-Bold.ttf"],
        ];
        foreach (var fam in sansFamilies)
        {
            if (Find(fam[0]) is not { } regular) continue;
            _faces["sans-r"] = regular;
            if (Find(fam[1]) is { } b) _faces["sans-b"] = b;
            if (Find(fam[2]) is { } i) _faces["sans-i"] = i;
            if (Find(fam[3]) is { } bi) _faces["sans-bi"] = bi;
            break;
        }
        foreach (var fam in monoFamilies)
        {
            if (Find(fam[0]) is not { } regular) continue;
            _faces["mono-r"] = regular;
            if (Find(fam[1]) is { } b) _faces["mono-b"] = b;
            break;
        }

        string[] fallbacks =
        [
            // Windows
            "seguisym.ttf", "msyh.ttc", "YuGothR.ttc", "malgun.ttf", "Nirmala.ttc", "Nirmala.ttf", "ebrima.ttf", "seguihis.ttf", "simsun.ttc",
            // macOS
            "Arial Unicode.ttf", "Apple Symbols.ttf", "AppleSDGothicNeo.ttc", "Hiragino Sans GB.ttc",
            // Linux
            "DejaVuSans.ttf", "NotoSansCJK-Regular.ttc", "DroidSansFallbackFull.ttf",
        ];
        foreach (var name in fallbacks)
            if (Find(name) is { } f && _faces.Values.All(v => v.Path != f.Path))
            {
                var family = FallbackPrefix + _fallbackFamilies.Count;
                _faces[family] = f;
                _fallbackFamilies.Add(family);
            }
    }

    private static IEnumerable<string> FontDirectories()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (win.Length > 0) yield return win;
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (windir is not null) yield return System.IO.Path.Combine(windir, "Fonts");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return System.IO.Path.Combine(local, "Microsoft", "Windows", "Fonts");
        yield return "/System/Library/Fonts/Supplemental";
        yield return "/System/Library/Fonts";
        yield return "/Library/Fonts";
        yield return "/usr/share/fonts/truetype/dejavu";
        yield return "/usr/share/fonts/dejavu";
        yield return "/usr/share/fonts/truetype/liberation";
        yield return "/usr/share/fonts/opentype/noto";
        yield return "/usr/share/fonts/truetype/droid";
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (familyName == Mono && _faces.ContainsKey("mono-r"))
            return Face("mono", bold, false, italic);
        if (familyName.StartsWith(FallbackPrefix, StringComparison.Ordinal) && _faces.ContainsKey(familyName))
            return new FontResolverInfo(familyName, bold, italic);
        return Face("sans", bold, italic, italic);
    }

    private FontResolverInfo Face(string prefix, bool bold, bool italic, bool simulateItalic)
    {
        var key = $"{prefix}-{(bold && italic ? "bi" : bold ? "b" : italic ? "i" : "r")}";
        if (_faces.ContainsKey(key)) return new FontResolverInfo(key);
        // No dedicated file for this style: draw the regular face with simulated bold or italic.
        if (bold && italic && _faces.ContainsKey($"{prefix}-b")) return new FontResolverInfo($"{prefix}-b", false, simulateItalic);
        return new FontResolverInfo($"{prefix}-r", bold, simulateItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_bytes)
        {
            if (_bytes.TryGetValue(faceName, out var b)) return b;
            if (!_faces.TryGetValue(faceName, out var f)) return null;
            using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return _bytes[faceName] = ms.ToArray();
        }
    }

    /// <summary>
    /// Splits text into runs, each with the font family that can draw it. Characters no installed font has become "?".
    /// Call only while holding <see cref="RenderLock"/>.
    /// </summary>
    public IEnumerable<(string Text, string? Family)> Runs(string text)
    {
        EnsureProbes();
        var sb = new StringBuilder();
        string? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var (family, value) = FamilyFor(rune);
            if (family != current && sb.Length > 0)
            {
                yield return (sb.ToString(), current);
                sb.Clear();
            }
            current = family;
            sb.Append(value);
        }
        if (sb.Length > 0) yield return (sb.ToString(), current);
    }

    private (string? Family, string Value) FamilyFor(Rune rune)
    {
        var cp = rune.Value;
        if (Rune.IsControl(rune)) return (null, "");
        // Latin text and white space stay in the main font without a lookup.
        if (cp < 0x250 || Rune.IsWhiteSpace(rune)) return (null, rune.ToString());
        if (cp is 0x200D or >= 0xFE00 and <= 0xFE0F) return (null, ""); // joiners and emoji style selectors
        if (!_familyForCodePoint.TryGetValue(cp, out var family))
        {
            family = Has(_sansProbe!, cp) ? null : _fallbackProbes.FirstOrDefault(p => Has(p.Probe, cp)).Family ?? "?";
            _familyForCodePoint[cp] = family;
        }
        return family == "?" ? (null, "?") : (family, rune.ToString());
    }

    private void EnsureProbes()
    {
        if (_probesReady) return;
        _sansProbe = new XFont(Sans, 10);
        foreach (var family in _fallbackFamilies)
        {
            try
            {
                var probe = new XFont(family, 10);
                Has(probe, 'A');
                _fallbackProbes.Add((family, probe));
            }
            catch (Exception)
            {
                // A font PDFsharp can't read (for example one with PostScript outlines) is simply not used.
            }
        }
        _probesReady = true;
    }

    private static bool Has(XFont font, int codePoint) => GlyphHelper.GlyphIndexFromCodePoint(codePoint, font) != 0;
}

/// <summary>A report problem with a message that can be shown to the user as is.</summary>
public sealed class ReportException(string message) : Exception(message);
