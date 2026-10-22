using System.Globalization;
using ExportViewer.Core.Model;

namespace ExportViewer.Core.Conversations;

public enum Severity
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public sealed record IntegrityFlag(Severity Severity, string Code, string Detail, string? MessageUuid = null);

public sealed record MessageInfo(string Uuid, string? ParentUuid, string Sender, string? CreatedAt, int Seq, MessageTraits Traits);

/// <summary>
/// Flags conversations that may be incomplete, based on what the sample exports actually show
/// (see docs/export-schema.md section 5.5). Wording is for a non-technical reader.
/// </summary>
public static class IntegrityChecker
{
    public static readonly TimeSpan UpdatedGapThreshold = TimeSpan.FromHours(1);

    public static IReadOnlyList<IntegrityFlag> Check(string? conversationUpdatedAt, IReadOnlyList<MessageInfo> messages, MessageTree tree)
    {
        var flags = new List<IntegrityFlag>();
        if (messages.Count == 0)
        {
            flags.Add(new(Severity.High, "no_messages", "This conversation has no messages in the export."));
            return flags;
        }

        var byId = messages.ToDictionary(m => m.Uuid, StringComparer.Ordinal);
        foreach (var orphan in tree.OrphanUuids)
            flags.Add(new(Severity.High, "missing_parent", "A message refers to an earlier message that is not in the export.", orphan));

        // Seen in real exports: every message present with timestamps, but no content on either side. File references
        // don't count as content (the files are never in the export); pasted attachments do, as their text is included.
        if (messages.All(m => m.Traits.HasFlag(MessageTraits.EmptyContent) && !m.Traits.HasFlag(MessageTraits.HasAttachments)))
        {
            flags.Add(new(Severity.High, "no_content",
                $"None of the {messages.Count} messages have any content in the export. The conversation took place, but what was written is not included."));
            return flags;
        }

        var path = tree.LatestPath().Select(n => byId[n.Uuid]).ToList();
        var last = path[^1];
        if (last.Sender == "human")
            flags.Add(new(Severity.High, "ends_without_reply", "The conversation ends with the user's message. There is no reply from Claude in the export.", last.Uuid));
        else if (last.Traits.HasFlag(MessageTraits.EmptyContent))
            flags.Add(new(Severity.High, "ends_with_empty_reply", "Claude's last reply is empty in the export. It may have failed, been stopped, or not been exported.", last.Uuid));
        else if (last.Traits.HasFlag(MessageTraits.ThinkingOnly))
            flags.Add(new(Severity.High, "ends_with_thinking_only", "Claude's last reply stops after thinking, with no answer in the export.", last.Uuid));

        foreach (var m in path.Where(m => m.Sender == "human"))
            if (m.Traits.HasFlag(MessageTraits.EmptyContent) && !m.Traits.HasFlag(MessageTraits.HasFiles) && !m.Traits.HasFlag(MessageTraits.HasAttachments))
                flags.Add(new(Severity.Medium, "empty_user_message", "One of the user's messages is empty in the export.", m.Uuid));

        foreach (var m in path.Take(path.Count - 1).Where(m => m.Sender == "assistant"))
        {
            if (m.Traits.HasFlag(MessageTraits.EmptyContent))
                flags.Add(new(Severity.Medium, "empty_reply", "One of Claude's replies is empty in the export.", m.Uuid));
            else if (m.Traits.HasFlag(MessageTraits.ThinkingOnly))
                flags.Add(new(Severity.Medium, "thinking_only_reply", "One of Claude's replies stops after thinking, with no answer.", m.Uuid));
        }

        foreach (var m in messages.Where(m => m.Traits.HasFlag(MessageTraits.TextMismatch)))
            flags.Add(new(Severity.Medium, "text_mismatch", "A message's plain-text copy does not match its full content.", m.Uuid));

        var lastAt = Parse(messages.Max(m => m.CreatedAt));
        var updated = Parse(conversationUpdatedAt);
        if (lastAt is { } la && updated is { } up && up - la > UpdatedGapThreshold)
            flags.Add(new(Severity.Low, "updated_after_last_message",
                $"The conversation was changed {Describe(up - la)} after its last message. This can just mean it was renamed, but messages may be missing."));

        return flags;
    }

    private static DateTimeOffset? Parse(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days"
        : t.TotalHours >= 2 ? $"{(int)t.TotalHours} hours"
        : $"{(int)t.TotalMinutes} minutes";
}
