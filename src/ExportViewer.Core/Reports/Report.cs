using System.Globalization;
using System.Text;

namespace ExportViewer.Core.Reports;

public enum ReportLayout
{
    /// <summary>Rows and columns.</summary>
    Table,

    /// <summary>One block per row, for conversations. Columns are: time, sender, text, reviewer note.</summary>
    Transcript,
}

/// <param name="Width">Relative width in the PDF.</param>
public sealed record ReportColumn(string Name, double Width = 1);

/// <summary>What every report says about where it came from.</summary>
/// <param name="ExportName">The library or zip files the report was made from.</param>
public sealed record ReportContext(string ExportName, string? CoversFrom, string? CoversTo, TimeZoneInfo TimeZone, DateTimeOffset Generated)
{
    public string Date(string? utc)
    {
        if (!DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return "";
        return TimeZoneInfo.ConvertTime(d, TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public string Covers =>
        CoversFrom is null && CoversTo is null ? "Unknown" : $"{Day(CoversFrom)} to {Day(CoversTo)}";

    public string GeneratedText =>
        $"{TimeZoneInfo.ConvertTime(Generated, TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({TimeZoneText})";

    public string TimeZoneText
    {
        get
        {
            var offset = TimeZone.GetUtcOffset(Generated);
            return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        }
    }

    private string Day(string? utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? TimeZoneInfo.ConvertTime(d, TimeZone).ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "?";
}

/// <summary>A report ready to be written as PDF or CSV. Every value is plain text; nothing is interpreted as markup.</summary>
public sealed class Report
{
    public required string Title { get; init; }
    public required ReportContext Context { get; init; }

    /// <summary>Label and value pairs shown under the title, such as the search words or the person.</summary>
    public IReadOnlyList<(string Label, string Value)> Details { get; init; } = [];

    /// <summary>Short paragraphs shown before the rows, such as integrity warnings or "values are masked".</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public required IReadOnlyList<ReportColumn> Columns { get; init; }
    public required IReadOnlyList<string[]> Rows { get; init; }
    public ReportLayout Layout { get; init; } = ReportLayout.Table;
    public string EmptyText { get; init; } = "Nothing to show.";

    /// <summary>Every line of the header block, in order, for both writers.</summary>
    public IEnumerable<(string Label, string Value)> HeaderLines()
    {
        yield return ("Export", Context.ExportName);
        yield return ("Export covers", Context.Covers);
        yield return ("Generated", Context.GeneratedText);
        yield return ("Times shown in", Context.TimeZoneText);
        foreach (var d in Details) yield return d;
    }

    public string FileName(string extension)
    {
        var sb = new StringBuilder();
        foreach (var ch in Title)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (name.Length == 0) name = "Report";
        return $"{name} {Context.Generated.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.{extension}";
    }
}
