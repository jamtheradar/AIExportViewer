using ExportViewer.Core.Store;

namespace ExportViewer.Core.Review;

/// <param name="Missing">The flagged conversation or message is not in the library (for example the export it came from was removed).</param>
public sealed record FlagListItem(
    ReviewFlag Flag, string? ConversationTitle, string? AccountUuid, string? UserName, string? ConversationAt,
    string? MessageSender, string? MessageAt, string? MessagePreview, bool Missing);

public static class FlagList
{
    /// <summary>Every flag, newest change first, with the titles, people and message previews from the library.</summary>
    public static IReadOnlyList<FlagListItem> Build(IReadOnlyList<ReviewFlag> flags, LibraryQueries q)
    {
        var convs = q.GetConversationHeaders(flags.Select(f => f.ConversationUuid));
        var msgs = q.GetMessageSummaries(flags.Where(f => f.MessageUuid is not null).Select(f => f.MessageUuid!));
        return flags.Select(f =>
        {
            convs.TryGetValue(f.ConversationUuid, out var c);
            MessageSummary? m = null;
            if (f.MessageUuid is { } id) msgs.TryGetValue(id, out m);
            var missing = c is null || (f.MessageUuid is not null && m is null);
            return new FlagListItem(f, c?.Title, c?.AccountUuid, c?.UserName, c?.LastMessageAt, m?.Sender, m?.CreatedAt, m?.Preview, missing);
        }).ToList();
    }
}
