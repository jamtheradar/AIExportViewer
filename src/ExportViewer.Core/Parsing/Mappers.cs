using System.Text;
using System.Text.Json;
using ExportViewer.Core.Json;
using ExportViewer.Core.Model;

namespace ExportViewer.Core.Parsing;

/// <summary>Turns export JSON into records. Missing fields become null; a record without an ID is rejected.</summary>
public static class Mappers
{
    private const int PreviewLength = 240;
    private const int MaxToolTextPerMessage = 200_000;

    public static ConversationRecord Conversation(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) throw new FormatException("Conversation is not an object.");
        var uuid = c.Str("uuid") ?? throw new FormatException("Conversation has no uuid.");
        var messages = new List<MessageRecord>();
        var seq = 0;
        foreach (var m in c.Arr("chat_messages"))
        {
            if (m.ValueKind != JsonValueKind.Object || m.Str("uuid") is null) { seq++; continue; }
            messages.Add(Message(m, uuid, seq++));
        }
        return new ConversationRecord(
            uuid, c.Str("name"), c.Str("summary"), c.UtcStr("created_at"), c.UtcStr("updated_at"),
            c.Path("account", "uuid"), c.RawWithout("chat_messages"), messages);
    }

    /// <summary>Number of message entries that were skipped because they had no uuid.</summary>
    public static int SkippedMessages(JsonElement c) =>
        c.Arr("chat_messages").Count(m => m.ValueKind != JsonValueKind.Object || m.Str("uuid") is null);

    public static MessageRecord Message(JsonElement m, string conversationUuid, int seq)
    {
        var traits = MessageTraits.None;
        var text = new StringBuilder();
        var search = new StringBuilder();
        var tool = new StringBuilder();
        var blocks = 0;
        var thinkingBlocks = 0;

        foreach (var b in m.Arr("content"))
        {
            blocks++;
            switch (b.Str("type"))
            {
                case "text":
                    text.Append(b.Str("text"));
                    break;
                case "thinking":
                    thinkingBlocks++;
                    traits |= MessageTraits.HasThinking;
                    search.Append(b.Str("thinking")).Append('\n');
                    foreach (var s in b.Arr("summaries")) search.Append(s.Str("summary")).Append('\n');
                    break;
                case "tool_use":
                    traits |= MessageTraits.HasToolUse;
                    tool.Append(b.Str("name")).Append(' ').Append(b.Str("message")).Append('\n');
                    if (b.Prop("input") is { } input) Strings(input, tool, MaxToolTextPerMessage);
                    break;
                case "tool_result":
                    if (b.Bool("is_error") == true) traits |= MessageTraits.HasToolError;
                    foreach (var item in b.Arr("content")) Strings(item, tool, MaxToolTextPerMessage, skipKeys: ["metadata", "prompt_context_metadata", "telemetry"]);
                    break;
                case "injected_prompt_block":
                    traits |= MessageTraits.HasInjectedContext;
                    break;
                default:
                    Strings(b, tool, MaxToolTextPerMessage);
                    break;
            }
        }

        var sender = m.Str("sender") ?? "unknown";
        if (blocks == 0) traits |= MessageTraits.EmptyContent;
        else if (sender == "assistant" && thinkingBlocks == blocks) traits |= MessageTraits.ThinkingOnly;

        var attachments = new List<AttachmentText>();
        foreach (var a in m.Arr("attachments"))
        {
            traits |= MessageTraits.HasAttachments;
            attachments.Add(new AttachmentText(a.Str("file_name"), a.Str("extracted_content") ?? ""));
        }
        if (m.HasArray("files") && m.Arr("files").Any()) traits |= MessageTraits.HasFiles;

        var joined = text.ToString();
        var flat = m.Str("text");
        if (flat is not null && m.HasArray("content") && !TextCompare.Agrees(flat, joined)) traits |= MessageTraits.TextMismatch;

        // Older exports may only carry `text`; search and preview fall back to it.
        var visible = joined.Length > 0 || blocks > 0 ? joined : TextCompare.RemovePlaceholders(flat ?? "");
        search.Insert(0, visible + "\n");

        return new MessageRecord(
            m.Str("uuid")!, conversationUuid, m.Str("parent_message_uuid"), sender,
            m.UtcStr("created_at"), m.UtcStr("updated_at"), seq, traits,
            TextCompare.CollapseWhitespace(visible, PreviewLength), search.ToString().Trim(), tool.ToString().Trim(),
            attachments, m.GetRawText());
    }

    public static UserRecord? User(JsonElement u) =>
        u.Str("uuid") is { } id
            ? new UserRecord(id, u.Str("full_name"), u.Str("email_address"), u.Str("verified_phone_number"), u.GetRawText())
            : null;

    public static LoginRecord Login(JsonElement e)
    {
        var ua = e.Prop("user_agent");
        string? Join(string a, string b)
        {
            var x = ua?.Str(a);
            var y = ua?.Str(b);
            return x is null ? null : y is null ? x : $"{x} {y}";
        }
        return new LoginRecord(e.Str("account_uuid"), e.UtcStr("timestamp"), e.Str("ip_address"),
            Join("browser_family", "browser_version"), Join("os_family", "os_version"), ua?.Str("device_family"), e.Str("method"), e.GetRawText());
    }

    public static ProjectRecord Project(JsonElement p)
    {
        var uuid = p.Str("uuid") ?? throw new FormatException("Project has no uuid.");
        var docs = new List<ProjectDocRecord>();
        foreach (var d in p.Arr("docs"))
            if (d.Str("uuid") is { } docId)
                docs.Add(new ProjectDocRecord(docId, d.Str("filename"), d.Str("content") ?? "", d.UtcStr("created_at")));
        return new ProjectRecord(uuid, p.Str("name"), p.Str("description"), p.Bool("is_private") ?? false, p.Bool("is_starter_project") ?? false,
            p.Str("prompt_template"), p.UtcStr("created_at"), p.UtcStr("updated_at"), p.Path("creator", "uuid"), p.Path("creator", "full_name"),
            p.RawWithout("docs"), docs);
    }

    public static MemoryRecord Memory(JsonElement m, string? fallbackAccount)
    {
        var account = m.Str("account_uuid") ?? fallbackAccount ?? throw new FormatException("Memory has no account_uuid.");
        var files = new List<MemoryFileRecord>();
        string? latest = null;
        foreach (var f in m.Arr("memory_files"))
        {
            var updated = f.UtcStr("updated_at");
            if (updated is not null && string.CompareOrdinal(updated, latest) > 0) latest = updated;
            files.Add(new MemoryFileRecord(f.Str("path") ?? "", f.Str("content") ?? "", updated));
        }
        var projectMemories = new List<ProjectMemoryRecord>();
        if (m.Prop("project_memories") is { ValueKind: JsonValueKind.Object } pm)
            foreach (var kv in pm.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String)
                    projectMemories.Add(new ProjectMemoryRecord(kv.Name, kv.Value.GetString() ?? ""));
        return new MemoryRecord(account, latest, m.Str("conversations_memory"), files, projectMemories, m.GetRawText());
    }

    /// <param name="folderRelative">Artifact folder relative to the library root.</param>
    /// <param name="folderFull">Artifact folder on disk, used to find version content and pages.</param>
    public static ArtifactRecord Artifact(JsonElement a, string folderRelative, string folderFull)
    {
        var id = a.Str("id") ?? throw new FormatException("Artifact has no id.");
        var active = a.Str("active_version");
        var versions = new List<ArtifactVersionRecord>();
        string? title = null;
        foreach (var v in a.Arr("versions"))
        {
            var vid = v.Str("id");
            if (vid is null) continue;
            var dir = System.IO.Path.Combine(folderFull, "versions", vid);
            var content = Directory.Exists(dir) ? folderRelative + "/versions/" + vid : null;
            versions.Add(new ArtifactVersionRecord(vid, v.Str("title"), v.UtcStr("created_at"), v.Str("description"), content));
            if (vid == active || title is null) title = v.Str("title") ?? title;
        }
        return new ArtifactRecord(id, a.Str("kind"), a.Str("visibility"), a.Str("owner_account"), a.UtcStr("updated_at"), active, title,
            a.GetRawText(), folderRelative,
            ReadOptional(folderFull, "page.md"), ReadOptional(folderFull, "comments.json"), ReadOptional(folderFull, "artifact_comments.json"),
            versions);
    }

    public static ManifestRecord Manifest(JsonElement m) =>
        new(m.UtcStr("created_at"), m.Long("total_files") is { } n ? (int)n : null, m.GetRawText());

    public static GenericRecord Generic(string collection, JsonElement r)
    {
        string? key = null;
        foreach (var k in (string[])["uuid", "id", "account_uuid"])
            if (r.Str(k) is { } v) { key = v; break; }
        string? label = null;
        foreach (var k in (string[])["name", "title", "full_name", "filename", "path"])
            if (r.Str(k) is { } v) { label = v; break; }
        return new GenericRecord(collection, key, label, r.GetRawText());
    }

    private static string? ReadOptional(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Appends every string value (and property names) under an element, for search.</summary>
    internal static void Strings(JsonElement e, StringBuilder sb, int max, string[]? skipKeys = null)
    {
        if (sb.Length >= max) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString();
                if (string.IsNullOrEmpty(s)) return;
                sb.Append(s.Length > max - sb.Length ? s[..Math.Max(0, max - sb.Length)] : s).Append('\n');
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (skipKeys is not null && Array.IndexOf(skipKeys, p.Name) >= 0) continue;
                    Strings(p.Value, sb, max, skipKeys);
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Strings(x, sb, max, skipKeys);
                break;
            case JsonValueKind.Number:
                sb.Append(e.GetRawText()).Append(' ');
                break;
        }
    }
}
