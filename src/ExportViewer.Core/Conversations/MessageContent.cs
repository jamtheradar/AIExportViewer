using System.Text.Json;
using ExportViewer.Core.Json;

namespace ExportViewer.Core.Conversations;

public abstract record Block(string Type, string? Start, string? Stop, string Raw);

public sealed record TextBlock(string Text, IReadOnlyList<Citation> Citations, string? Start, string? Stop, string Raw) : Block("text", Start, Stop, Raw);

public sealed record Citation(int StartIndex, int EndIndex, string? Url, string? Type);

public sealed record ThinkingBlock(string Thinking, IReadOnlyList<string> Summaries, bool Hidden, bool CutOff, string? Start, string? Stop, string Raw)
    : Block("thinking", Start, Stop, Raw);

public sealed record ToolUseBlock(
    string? Id, string? Name, string? Message, string? Integration, JsonElement? Input, DisplayContent? Display, string? Start, string? Stop, string Raw)
    : Block("tool_use", Start, Stop, Raw);

public sealed record ToolResultBlock(
    string? ToolUseId, string? Name, bool IsError, IReadOnlyList<ResultItem> Items, DisplayContent? Display, string? Message,
    string? StructuredJson, string? MetaJson, string? Start, string? Stop, string Raw)
    : Block("tool_result", Start, Stop, Raw);

/// <param name="Type">text, knowledge (web result), image, local_resource (generated file), image_gallery, or anything new.</param>
public sealed record ResultItem(string Type, string? Title, string? Url, string? Text, string? FileName, string? FileUuid, string? MimeType, string? Site, string Raw);

/// <param name="Table">Rows of cells for <c>table</c> display content.</param>
public sealed record DisplayContent(string Type, string? Text, string? Code, string? Language, string? Filename, string? LinkTitle, string? LinkUrl,
    IReadOnlyList<IReadOnlyList<string>>? Table, string Raw);

public sealed record InjectedBlock(string? Source, string Prompt, string? Start, string? Stop, string Raw) : Block("injected_prompt_block", Start, Stop, Raw);

public sealed record UnknownBlock(string TypeName, string? Start, string? Stop, string Raw) : Block(TypeName, Start, Stop, Raw);

public sealed record Attachment(string? FileName, long? FileSize, string? FileType, string Content);

public sealed record FileRef(string? FileUuid, string? FileName);

public sealed record MessageContent(
    string Uuid, string? ParentUuid, string Sender, string? CreatedAt, string? UpdatedAt, string FlatText,
    IReadOnlyList<Block> Blocks, IReadOnlyList<Attachment> Attachments, IReadOnlyList<FileRef> Files, string Raw)
{
    public static MessageContent Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var m = doc.RootElement;
        var blocks = new List<Block>();
        foreach (var b in m.Arr("content")) blocks.Add(ParseBlock(b));
        var attachments = m.Arr("attachments")
            .Select(a => new Attachment(a.Str("file_name"), a.Long("file_size"), a.Str("file_type"), a.Str("extracted_content") ?? "")).ToList();
        var files = m.Arr("files").Select(f => new FileRef(f.Str("file_uuid"), f.Str("file_name"))).ToList();
        return new MessageContent(m.Str("uuid") ?? "", m.Str("parent_message_uuid"), m.Str("sender") ?? "unknown",
            m.UtcStr("created_at"), m.UtcStr("updated_at"), m.Str("text") ?? "", blocks, attachments, files, raw);
    }

    private static Block ParseBlock(JsonElement b)
    {
        var raw = b.GetRawText();
        var start = b.UtcStr("start_timestamp");
        var stop = b.UtcStr("stop_timestamp");
        switch (b.Str("type"))
        {
            case "text":
                var cites = b.Arr("citations").Select(c => new Citation(
                    (int)(c.Long("start_index") ?? 0), (int)(c.Long("end_index") ?? 0), c.Prop("details")?.Str("url"), c.Prop("details")?.Str("type"))).ToList();
                return new TextBlock(b.Str("text") ?? "", cites, start, stop, raw);
            case "thinking":
                return new ThinkingBlock(b.Str("thinking") ?? "", b.Arr("summaries").Select(s => s.Str("summary") ?? "").Where(s => s.Length > 0).ToList(),
                    b.Bool("thinking_hidden") ?? b.Bool("hidden") ?? false, b.Bool("cut_off") ?? false, start, stop, raw);
            case "tool_use":
                return new ToolUseBlock(b.Str("id"), b.Str("name"), b.Str("message"), b.Str("integration_name"), b.Prop("input")?.Clone(),
                    Display(b), start, stop, raw);
            case "tool_result":
                var items = b.Arr("content").Select(ParseItem).ToList();
                return new ToolResultBlock(b.Str("tool_use_id"), b.Str("name"), b.Bool("is_error") ?? false, items, Display(b), b.Str("message"),
                    b.Prop("structured_content")?.GetRawText(), b.Prop("meta")?.GetRawText(), start, stop, raw);
            case "injected_prompt_block":
                return new InjectedBlock(b.Str("injection_source"), b.Str("prompt") ?? "", start, stop, raw);
            default:
                return new UnknownBlock(b.Str("type") ?? "unknown", start, stop, raw);
        }
    }

    private static ResultItem ParseItem(JsonElement i) =>
        new(i.Str("type") ?? "unknown", i.Str("title") ?? i.Str("name"), i.Str("url"), i.Str("text"),
            i.Str("file_path") ?? i.Str("file_name"), i.Str("file_uuid"), i.Str("mime_type"),
            i.Prop("metadata")?.Str("site_name") ?? i.Prop("metadata")?.Str("site_domain"), i.GetRawText());

    private static DisplayContent? Display(JsonElement b)
    {
        if (b.Prop("display_content") is not { ValueKind: JsonValueKind.Object } d) return null;
        IReadOnlyList<IReadOnlyList<string>>? table = null;
        if (d.Prop("table") is { ValueKind: JsonValueKind.Array } t)
            table = t.EnumerateArray().Select(row => (IReadOnlyList<string>)(row.ValueKind == JsonValueKind.Array
                ? row.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText()).ToList()
                : [row.GetRawText()])).ToList();
        var text = d.Str("text") ?? (d.Prop("json_block") is { } jb ? (jb.ValueKind == JsonValueKind.String ? jb.GetString() : jb.GetRawText()) : null);
        return new DisplayContent(d.Str("type") ?? "unknown", text, d.Str("code"), d.Str("language"), d.Str("filename"),
            d.Prop("link")?.Str("title") ?? d.Str("title"), d.Prop("link")?.Str("url") ?? d.Str("published_url"), table, d.GetRawText());
    }
}
