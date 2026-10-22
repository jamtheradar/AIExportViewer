using System.Globalization;
using ExportViewer.App.Services;
using ExportViewer.Core.Conversations;

namespace ExportViewer.App.Components.Shared;

/// <summary>Display formatting. Basic shows friendly local times; Expert adds seconds and UTC.</summary>
public static class Fmt
{
    public static DateTimeOffset? Parse(string? utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string Date(string? utc, ViewMode mode)
    {
        if (Parse(utc) is not { } d) return "—";
        var local = d.ToLocalTime();
        return mode == ViewMode.Expert
            ? $"{local:yyyy-MM-dd HH:mm:ss} (UTC {d.UtcDateTime:yyyy-MM-dd HH:mm:ss})"
            : local.ToString("ddd d MMM yyyy, h:mm tt", CultureInfo.CurrentCulture);
    }

    public static string Day(string? utc)
    {
        if (Parse(utc) is not { } d) return "—";
        return d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} bytes",
        < 1024 * 1024 => $"{n / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:F1} MB",
        _ => $"{n / 1024.0 / 1024 / 1024:F2} GB",
    };

    public static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Plural(int n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";

    public static string SeverityClass(Severity s) => s switch
    {
        Severity.High => "high",
        Severity.Medium => "medium",
        Severity.Low => "low",
        _ => "",
    };

    public static string SeverityLabel(Severity s) => s switch
    {
        Severity.High => "Possibly incomplete",
        Severity.Medium => "Check",
        Severity.Low => "Minor note",
        _ => "",
    };

    public static string PrettyJson(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (System.Text.Json.JsonException)
        {
            return raw;
        }
    }
}
