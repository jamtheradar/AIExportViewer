using System.Net;
using System.Text;
using ExportViewer.Core.Audit;
using ExportViewer.Core.Conversations;
using ExportViewer.Core.Parsing;
using ExportViewer.Core.Review;
using ExportViewer.Core.Search;
using ExportViewer.Core.Store;

namespace ExportViewer.Core.Reports;

/// <summary>Builds the reports a reviewer can save: a conversation, a set of search results, the flag list and check results.</summary>
public static class ReportBuilder
{
    private const int MaxAttachmentText = 5_000;

    private const string MaskedNote =
        "Sensitive values the app recognises, such as card and tax file numbers, passwords and keys, are masked. To include them, tick “Include full values” before saving.";

    private const string UnmaskedNote = "This report includes full sensitive values. Store and share it with care.";

    private static Func<string, string> Masker(bool mask) => mask ? Masking.MaskAll : s => s;

    /// <param name="shown">The messages in the order shown on screen (normally the version of the conversation being viewed).</param>
    /// <param name="allVersions">True when <paramref name="shown"/> includes every version, not only one branch.</param>
    /// <param name="mask">Mask sensitive values in the title and messages (the default). Reviewer notes are left as written.</param>
    public static Report Conversation(ReportContext ctx, ConversationDetail d, IReadOnlyList<MessageRow> shown, IReadOnlyList<ReviewFlag> flags, bool allVersions,
        bool mask = true)
    {
        var safe = Masker(mask);
        var h = d.Header;
        var title = safe(h.Title);
        var person = h.UserName ?? "Unknown person";
        var byMessage = flags.Where(f => f.MessageUuid is not null).ToDictionary(f => f.MessageUuid!, StringComparer.Ordinal);
        var details = new List<(string, string)>
        {
            ("Conversation", title),
            ("Person", person),
            ("First message", ctx.Date(h.FirstMessageAt)),
            ("Last message", ctx.Date(h.LastMessageAt)),
            ("Messages in this report", allVersions
                ? $"{shown.Count} (every version)"
                : shown.Count == d.Messages.Count ? $"{shown.Count}" : $"{shown.Count} of {d.Messages.Count} (the version shown on screen; other versions left out)"),
            ("Conversation ID", h.Uuid),
        };

        var notes = new List<string> { mask ? MaskedNote : UnmaskedNote };
        foreach (var f in d.Flags.OrderByDescending(f => f.Severity))
            notes.Add($"{SeverityText(f.Severity)}: {f.Detail}");
        if (flags.FirstOrDefault(f => f.IsConversation) is { } cf)
            notes.Add(cf.Note.Length > 0 ? $"Reviewer flag on this conversation: {cf.Note}" : "Flagged by the reviewer.");

        var rows = shown.Select(m =>
        {
            var content = MessageContent.Parse(m.Raw);
            var from = m.Sender switch { "human" => person, "assistant" => "Claude", var s => s };
            var note = byMessage.TryGetValue(m.Uuid, out var f) ? (f.Note.Length > 0 ? $"Reviewer flag: {f.Note}" : "Flagged by the reviewer.") : "";
            return new[] { ctx.Date(m.CreatedAt), from, safe(MessageText(content)), note };
        }).ToList();

        return new Report
        {
            Title = $"Conversation: {title}",
            Context = ctx,
            Details = details,
            Notes = notes,
            Columns = [new("Time", 1.1), new("From", 1.2), new("Message", 6), new("Reviewer note", 2)],
            Rows = rows,
            Layout = ReportLayout.Transcript,
            EmptyText = "This conversation has no messages in the export.",
        };
    }

    /// <summary>Plain text of a message for a report: written text, one line per tool step, attachments and file names.</summary>
    public static string MessageText(MessageContent m)
    {
        var sb = new StringBuilder();
        void Para(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(s);
        }

        if (m.Blocks.Count == 0) Para(TextCompare.RemovePlaceholders(m.FlatText));
        var results = m.Blocks.OfType<ToolResultBlock>().Where(r => r.ToolUseId is not null).GroupBy(r => r.ToolUseId!).ToDictionary(g => g.Key, g => g.First());
        var used = m.Blocks.OfType<ToolUseBlock>().Select(u => u.Id ?? "").ToHashSet();
        var hasText = false;
        foreach (var b in m.Blocks)
        {
            switch (b)
            {
                case TextBlock t:
                    hasText |= t.Text.Trim().Length > 0;
                    Para(t.Text);
                    break;
                case ToolUseBlock u:
                    var result = u.Id is not null ? results.GetValueOrDefault(u.Id) : null;
                    Para($"[Claude's step: {ToolStepDescriber.Describe(u)}. Result: {ToolStepDescriber.Summarise(result)}]");
                    break;
                case ToolResultBlock r when r.ToolUseId is null || !used.Contains(r.ToolUseId):
                    Para($"[Tool result: {ToolStepDescriber.Summarise(r)}]");
                    break;
                case UnknownBlock x:
                    Para($"[A “{x.TypeName}” item, not shown in this report]");
                    break;
            }
        }
        if (m.Sender == "assistant" && !hasText && m.Blocks.Count > 0) Para("[No written answer from Claude is in the export for this message.]");
        foreach (var a in m.Attachments)
        {
            var name = string.IsNullOrEmpty(a.FileName) ? "Pasted text" : a.FileName;
            var body = a.Content.Length > MaxAttachmentText ? a.Content[..MaxAttachmentText] + " …[attachment cut short in this report]" : a.Content;
            Para($"[Attachment: {name}]\n{body}");
        }
        foreach (var f in m.Files) Para($"[File: {(string.IsNullOrEmpty(f.FileName) ? "unnamed" : f.FileName)} (the file itself is not in the export)]");
        if (sb.Length == 0) Para("[This message is empty in the export.]");
        return sb.ToString();
    }

    /// <param name="filters">What the search was limited to, as shown on screen (person, dates, where it looked).</param>
    public static Report Search(ReportContext ctx, string query, IReadOnlyList<(string Label, string Value)> filters, int total, IReadOnlyList<SearchHit> hits,
        Func<string, string> kindLabel, bool mask = true)
    {
        var safe = Masker(mask);
        var details = new List<(string, string)> { ("Search words", query) };
        details.AddRange(filters);
        details.Add(("Results", hits.Count < total ? $"{total:N0} (the first {hits.Count:N0} are in this report)" : $"{total:N0}"));
        return new Report
        {
            Title = $"Search: {query}",
            Context = ctx,
            Details = details,
            Notes = ["Matching words are marked «like this».", mask ? MaskedNote : UnmaskedNote],
            Columns = [new("Found in", 1.3), new("Title", 2.2), new("Person", 1.4), new("Date", 1.2), new("Match", 5)],
            Rows = hits.Select(h => new[] { kindLabel(h.Kind), safe(h.Title ?? "(untitled)"), h.UserName ?? "", ctx.Date(h.Timestamp), safe(SnippetText(h.SnippetHtml)) })
                .ToList(),
            EmptyText = "No results.",
        };
    }

    internal static string SnippetText(string snippetHtml) =>
        WebUtility.HtmlDecode(snippetHtml.Replace("<mark>", "«", StringComparison.Ordinal).Replace("</mark>", "»", StringComparison.Ordinal));

    public static Report Flags(ReportContext ctx, IReadOnlyList<FlagListItem> items, IReadOnlyList<(string Label, string Value)> filters, bool mask = true)
    {
        var safe = Masker(mask);
        var details = new List<(string, string)>(filters) { ("Flags", $"{items.Count:N0}") };
        return new Report
        {
            Title = "Reviewer flags and notes",
            Context = ctx,
            Details = details,
            Notes = [mask ? MaskedNote : UnmaskedNote],
            Columns = [new("Flagged", 1.1), new("What", 0.9), new("Conversation", 2.2), new("Person", 1.4), new("Message", 3), new("Note", 3)],
            Rows = items.Select(i => new[]
            {
                ctx.Date(i.Flag.UpdatedAt),
                i.Flag.IsConversation ? "Conversation" : "Message",
                i.ConversationTitle is { } t ? safe(t) : "(not in this library)",
                i.UserName ?? "",
                i.Flag.IsConversation ? "" : i.MessagePreview is null ? "(message not in this library)"
                    : $"{(i.MessageSender == "assistant" ? "Claude" : i.MessageSender == "human" ? i.UserName ?? "Person" : i.MessageSender)}, {ctx.Date(i.MessageAt)}: {safe(i.MessagePreview)}",
                i.Flag.Note,
            }).ToList(),
            EmptyText = "No conversations or messages have been flagged.",
        };
    }

    public static Report Findings(ReportContext ctx, string source, IReadOnlyList<FindingRow> rows, int total, ScanInfo scan,
        IReadOnlyList<(string Label, string Value)> filters)
    {
        var watch = source == FindingSource.Watchlist;
        var details = new List<(string, string)> { ("Checked", scan.At is { } at ? ctx.Date(at.UtcDateTime.ToString("O")) : "Not yet") };
        details.AddRange(filters);
        details.Add(("Matches", rows.Count < total ? $"{total:N0} (the first {rows.Count:N0} are in this report)" : $"{total:N0}"));
        static string Context(FindingRow r) => r.ContextBefore.Length + r.ContextAfter.Length == 0 ? "" : $"{r.ContextBefore}[{r.Masked}]{r.ContextAfter}";
        return new Report
        {
            Title = watch ? "Watchlist matches" : "Possible sensitive data",
            Context = ctx,
            Details = details,
            Notes = watch
                ? ["Any other sensitive values in the surrounding text are masked."]
                : ["Values are masked so this report doesn't spread them further. Open the app to see a value. These are pattern matches and need checking by a person."],
            Columns = watch
                ? [new("Watchlist", 1.2), new("Matched", 1.4), new("Where", 1.3), new("Title", 2), new("Person", 1.3), new("Date", 1.1), new("Context", 4)]
                : [new("Type", 1.3), new("Value (masked)", 1.5), new("How sure", 0.8), new("Where", 1.3), new("Title", 1.9), new("Person", 1.3), new("Date", 1.1), new("Context", 3.6)],
            Rows = rows.Select(r => watch
                ? new[] { r.Type, r.Masked, AuditLabels.Location(r.Location), r.Title ?? "", r.UserName ?? "", ctx.Date(r.Timestamp), Context(r) }
                : [AuditLabels.Type(r.Type), r.Masked, AuditLabels.Confidence(r.Confidence), AuditLabels.Location(r.Location), r.Title ?? "", r.UserName ?? "",
                    ctx.Date(r.Timestamp), Context(r)]).ToList(),
            EmptyText = "Nothing was found.",
        };
    }

    private static string SeverityText(Severity s) => s switch
    {
        Severity.High => "Possibly incomplete",
        Severity.Medium => "Check",
        _ => "Note",
    };
}
