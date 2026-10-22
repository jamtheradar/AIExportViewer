using System.Globalization;
using System.Text.Json;

namespace ExportViewer.Core.Json;

/// <summary>Tolerant accessors: a missing or wrongly typed field returns null instead of throwing.</summary>
public static class JsonExt
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.Prop(name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static long? Long(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    public static JsonElement.ArrayEnumerator Arr(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : default;

    public static bool HasArray(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array };

    /// <summary>Nested string such as <c>account.uuid</c>.</summary>
    public static string? Path(this JsonElement e, params string[] names)
    {
        JsonElement? cur = e;
        foreach (var n in names)
        {
            cur = cur?.Prop(n);
            if (cur is null) return null;
        }
        return cur is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    /// <summary>
    /// Normalises any ISO 8601 timestamp to UTC with microseconds and a trailing Z, so stored values sort as text.
    /// Returns null when the value is missing or unparseable.
    /// </summary>
    public static string? Utc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return null;
        return FormatUtc(dto);
    }

    public static string FormatUtc(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static string? UtcStr(this JsonElement e, string name) => Utc(e.Str(name));

    /// <summary>Serialises an object without the named properties (used to avoid storing big child arrays twice).</summary>
    public static string RawWithout(this JsonElement e, params string[] exclude)
    {
        if (e.ValueKind != JsonValueKind.Object) return e.GetRawText();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in e.EnumerateObject())
            {
                if (Array.IndexOf(exclude, p.Name) >= 0) continue;
                p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
