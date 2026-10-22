using System.Text;

namespace ExportViewer.Core.Reports;

/// <summary>
/// Writes a report as CSV (UTF-8 with a byte order mark so Excel reads it correctly). The header lines come first,
/// then a blank line, then the table. Cells that a spreadsheet would treat as a formula are prefixed with an
/// apostrophe, because export content is untrusted.
/// </summary>
public static class CsvReportWriter
{
    /// <summary>Excel's limit for one cell is 32,767 characters.</summary>
    public const int MaxCell = 32_000;

    public static void Write(Report report, Stream output)
    {
        using var w = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true) { NewLine = "\r\n" };
        Line(w, ["Report", report.Title]);
        foreach (var (label, value) in report.HeaderLines()) Line(w, [label, value]);
        foreach (var note in report.Notes) Line(w, ["Note", note]);
        w.WriteLine();
        Line(w, report.Columns.Select(c => c.Name));
        foreach (var row in report.Rows) Line(w, row);
    }

    public static byte[] ToBytes(Report report)
    {
        using var ms = new MemoryStream();
        Write(report, ms);
        return ms.ToArray();
    }

    private static void Line(StreamWriter w, IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) w.Write(',');
            first = false;
            w.Write(Escape(cell));
        }
        w.WriteLine();
    }

    internal static string Escape(string? value)
    {
        var s = value ?? "";
        if (s.Length > MaxCell) s = s[..MaxCell] + " …[cut short]";
        if (s.Length > 0 && IsFormulaStart(s[0])) s = "'" + s;
        return s.AsSpan().IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // Includes the full-width forms, which some spreadsheet apps also treat as formula starters.
    private static bool IsFormulaStart(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '|' or '＝' or '＋' or '－' or '＠';
}
