using ExportViewer.Core.Conversations;
using ExportViewer.Core.Discovery;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Model;
using ExportViewer.Core.Store;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class LoaderTests
{
    [Fact]
    public async Task Loads_a_complete_export()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();

        var summary = await lib.LoadAsync();
        var o = lib.Queries.GetOverview();

        Assert.Empty(summary.SkippedFiles);
        Assert.All(summary.Files, f => Assert.Null(f.Note));
        Assert.Equal(2, o.Users);
        Assert.Equal(2, o.Conversations);
        Assert.Equal(4, o.Messages);
        Assert.Equal(1, o.Projects);
        Assert.Equal(1, o.Memories);
        Assert.Equal(2, o.Artifacts);
        Assert.Single(o.Exports);
        Assert.Equal(2, lib.Queries.GetLogins(Fake.Id(1)).Count + lib.Queries.GetLogins(Fake.Id(2)).Count);
    }

    [Fact]
    public async Task Derives_conversation_traits_and_titles()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        await lib.LoadAsync();

        var c = lib.Queries.GetConversation(Fake.Id(100))!;

        Assert.Equal("Research quokkas", c.Header.Title);
        Assert.True(c.Header.Traits.HasFlag(MessageTraits.HasToolUse));
        Assert.True(c.Header.Traits.HasFlag(MessageTraits.HasAttachments));
        Assert.True(c.Header.Traits.HasFlag(MessageTraits.HasFiles));
        Assert.True(c.Header.Traits.HasFlag(MessageTraits.HasThinking));
        Assert.True(c.Header.Traits.HasFlag(MessageTraits.HasInjectedContext));
        Assert.False(c.Header.Traits.HasFlag(MessageTraits.TextMismatch));
        Assert.Equal(Severity.None, c.Header.Integrity);
        Assert.Equal("Alex Example", c.Header.UserName);
        Assert.Contains("export-a/conversations-000/conversations.json", c.Sources);
    }

    [Fact]
    public async Task Untitled_conversation_falls_back_to_first_question()
    {
        using var lib = new TempLibrary();
        lib.Json("c/conversations.json", new[] { Fake.Conversation(Fake.Id(1), Fake.Id(9), "", 0, 1, Fake.Turn(2, 3, Fake.Root, 0, "Where do koalas sleep?", "In trees.")) });
        await lib.LoadAsync();

        Assert.Equal("Where do koalas sleep?", lib.Queries.GetConversation(Fake.Id(1))!.Header.Title);
    }

    [Fact]
    public async Task Unknown_fields_are_kept_in_raw_json()
    {
        using var lib = new TempLibrary();
        lib.Write("c/conversations.json", $$"""
            [{"uuid":"{{Fake.Id(1)}}","name":"n","future_field":{"x":1},"chat_messages":[
              {"uuid":"{{Fake.Id(2)}}","sender":"human","parent_message_uuid":"{{Fake.Root}}","content":[{"type":"brand_new_block","payload":"hello"}],"new_message_field":true}
            ]}]
            """);
        await lib.LoadAsync();

        var c = lib.Queries.GetConversation(Fake.Id(1))!;
        Assert.Contains("future_field", c.Raw);
        Assert.Contains("new_message_field", c.Messages.Single().Raw);
        var content = MessageContent.Parse(c.Messages.Single().Raw);
        Assert.IsType<UnknownBlock>(content.Blocks.Single());
    }

    [Fact]
    public async Task Missing_fields_do_not_stop_the_load()
    {
        using var lib = new TempLibrary();
        lib.Write("c/conversations.json", $$"""
            [{"uuid":"{{Fake.Id(1)}}","chat_messages":[{"uuid":"{{Fake.Id(2)}}"}]},
             {"uuid":"{{Fake.Id(3)}}"}]
            """);
        var summary = await lib.LoadAsync();

        Assert.Equal(2, summary.Files.Single().Records);
        Assert.Equal(2, lib.Queries.GetOverview().Conversations);
    }

    [Fact]
    public async Task Records_without_an_id_are_skipped_and_reported()
    {
        using var lib = new TempLibrary();
        lib.Write("c/conversations.json", $$"""
            [{"name":"no id"},{"uuid":"{{Fake.Id(1)}}","chat_messages":[{"text":"no id"},{"uuid":"{{Fake.Id(2)}}","sender":"human"}]}]
            """);
        var summary = await lib.LoadAsync();

        var file = summary.Files.Single();
        Assert.Equal(1, file.Records);
        Assert.Equal(2, file.Skipped);
        Assert.Equal(2, lib.Queries.GetLoadIssues().Count);
    }

    [Fact]
    public async Task Empty_conversations_file_is_reported_not_fatal()
    {
        using var lib = new TempLibrary();
        lib.Write("c/conversations.json", "");
        lib.WriteStandardExport();
        var summary = await lib.LoadAsync();

        Assert.Contains(summary.SkippedFiles, f => f.RelativePath == "c/conversations.json");
        Assert.Contains(lib.Queries.GetLoadIssues(), i => i.Path == "c/conversations.json" && i.Message.Contains("empty"));
        Assert.Equal(2, lib.Queries.GetOverview().Conversations);
    }

    [Fact]
    public async Task Truncated_file_keeps_everything_before_the_break()
    {
        using var lib = new TempLibrary();
        var full = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            Fake.Conversation(Fake.Id(1), Fake.Id(9), "first", 0, 1, Fake.Turn(2, 3, Fake.Root, 0, "q", "a")),
            Fake.Conversation(Fake.Id(4), Fake.Id(9), "second", 0, 1, Fake.Turn(5, 6, Fake.Root, 0, "q", "a")),
        });
        lib.Write("c/conversations.json", full[..(full.IndexOf("second", StringComparison.Ordinal) + 3)]);

        var summary = await lib.LoadAsync();

        var file = summary.Files.Single();
        Assert.Equal(1, file.Records);
        Assert.Contains("cut short", file.Note);
        Assert.Equal(1, lib.Queries.GetOverview().Conversations);
    }

    [Fact]
    public async Task Split_collections_across_files_are_merged()
    {
        using var lib = new TempLibrary();
        lib.Json("a/conversations-000/conversations.json", new[] { Fake.Conversation(Fake.Id(1), Fake.Id(9), "one", 0, 1, Fake.Turn(2, 3, Fake.Root, 0, "q", "a")) });
        lib.Json("a/conversations-001/conversations.json", new[] { Fake.Conversation(Fake.Id(4), Fake.Id(9), "two", 0, 1, Fake.Turn(5, 6, Fake.Root, 0, "q", "a")) });
        lib.Json("a/projects/p1.json", Fake.Project(Fake.Id(10), "P1", Fake.Id(9), 1));
        lib.Json("a/projects-000/projects/p2.json", Fake.Project(Fake.Id(11), "P2", Fake.Id(9), 1));

        await lib.LoadAsync();
        var o = lib.Queries.GetOverview();

        Assert.Equal(2, o.Conversations);
        Assert.Equal(2, o.Projects);
    }

    [Fact]
    public async Task Overlapping_exports_merge_messages_and_keep_newest_metadata()
    {
        using var lib = new TempLibrary();
        // September export: conversation with one turn.
        lib.Json("2026-09/conversations-000/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(1), Fake.Id(9), "Old title", 0, 10, Fake.Turn(2, 3, Fake.Root, 0, "first question", "first answer")),
        });
        // October export: same conversation renamed with a second turn, but the first turn is missing (e.g. retention).
        lib.Json("2026-10/conversations-000/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(1), Fake.Id(9), "New title", 0, 100, Fake.Turn(4, 5, Fake.Id(3), 50, "second question", "second answer")),
        });
        lib.Json("2026-09/light_metadata-000/users.json", new[] { Fake.User(Fake.Id(9), "Pat", "pat@example.test", "+61400000001") });
        lib.Json("2026-10/light_metadata-000/users.json", new[] { Fake.User(Fake.Id(9), "Pat Person", "pat@example.test") });
        lib.Json("2026-09/light_metadata-000/login_history.json", Fake.Logins(Fake.Login(Fake.Id(9), 1)));
        lib.Json("2026-10/light_metadata-000/login_history.json", Fake.Logins(Fake.Login(Fake.Id(9), 1), Fake.Login(Fake.Id(9), 60)));

        await lib.LoadAsync();

        var c = lib.Queries.GetConversation(Fake.Id(1))!;
        Assert.Equal("New title", c.Header.Title);
        Assert.Equal(4, c.Messages.Count);
        Assert.Equal(4, c.Header.MessageCount);
        Assert.Equal(2, c.Sources.Count);
        var user = lib.Queries.GetUser(Fake.Id(9))!;
        Assert.Equal("Pat Person", user.FullName);
        Assert.Equal("+61400000001", user.Phone);
        Assert.Equal(2, lib.Queries.GetLogins(Fake.Id(9)).Count);
    }

    [Fact]
    public async Task Load_order_does_not_change_the_merge_result()
    {
        using var lib = new TempLibrary();
        // Newer copy sits in a folder that sorts first.
        lib.Json("a-newer/conversations.json", new[] { Fake.Conversation(Fake.Id(1), Fake.Id(9), "Newest", 0, 100, Fake.Turn(2, 3, Fake.Root, 0, "q", "a")) });
        lib.Json("b-older/conversations.json", new[] { Fake.Conversation(Fake.Id(1), Fake.Id(9), "Oldest", 0, 10, Fake.Turn(2, 3, Fake.Root, 0, "q", "a")) });

        await lib.LoadAsync();

        Assert.Equal("Newest", lib.Queries.GetConversation(Fake.Id(1))!.Header.Title);
    }

    [Fact]
    public async Task Reopening_an_unchanged_library_loads_nothing_and_new_exports_load_incrementally()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport("export-a");
        await lib.LoadAsync();
        var loader = new LibraryLoader(lib.Db);

        Assert.True(loader.Compare(LibraryScanner.Scan(lib.Root)).IsUpToDate);
        Assert.False(loader.NeedsReanalysis);

        lib.Json("export-b/conversations-000/conversations.json", new[] { Fake.Conversation(Fake.Id(300), Fake.Id(2), "Later chat", 100, 101, Fake.Turn(301, 302, Fake.Root, 100, "q", "a")) });
        var changes = loader.Compare(LibraryScanner.Scan(lib.Root));
        Assert.Single(changes.New);
        Assert.False(changes.NeedsRebuild);

        var summary = await lib.LoadAsync(rebuild: false);
        Assert.Single(summary.Files);
        Assert.Equal(3, lib.Queries.GetOverview().Conversations);
    }

    [Fact]
    public async Task Changed_files_require_a_rebuild()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        await lib.LoadAsync();
        var path = Path.Combine(lib.Root, "export-a/light_metadata-000/users.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { Fake.User(Fake.Id(1), "Renamed", "alex@example.test") }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var changes = new LibraryLoader(lib.Db).Compare(LibraryScanner.Scan(lib.Root));
        Assert.True(changes.NeedsRebuild);

        var summary = await lib.LoadAsync(rebuild: true);
        Assert.True(summary.Rebuilt);
        Assert.Equal("Renamed", lib.Queries.GetUser(Fake.Id(1))!.FullName);
        Assert.Equal(2, lib.Queries.GetOverview().Conversations);
    }

    [Fact]
    public async Task Memory_snapshots_from_several_exports_are_all_kept_newest_first()
    {
        using var lib = new TempLibrary();
        lib.Json("old/memories/m.json", Fake.Memory(Fake.Id(1), 1, "old summary", ("/profile.md", "old")));
        lib.Json("new/memories/m.json", Fake.Memory(Fake.Id(1), 50, "new summary", ("/profile.md", "new")));
        await lib.LoadAsync();

        var snapshots = lib.Queries.GetMemorySnapshots(Fake.Id(1));
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("new summary", lib.Queries.GetMemorySnapshot(snapshots[0].Id)!.ConversationsMemory);
        var hits = new Search.SearchService(lib.Db).Search(new Search.SearchRequest("summary"));
        Assert.Single(hits.Hits);
    }

    [Fact]
    public async Task Artifacts_record_which_versions_have_content_and_load_pages()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        await lib.LoadAsync();

        var page = lib.Queries.GetArtifact(Fake.Id(800))!;
        Assert.Contains("Echidna", page.PageMarkdown);
        Assert.True(page.Summary.HasPage);
        var deck = lib.Queries.GetArtifact(Fake.Id(801))!;
        Assert.Equal(2, deck.Summary.Versions);
        Assert.Equal(1, deck.Summary.VersionsWithContent);
        Assert.Equal("Deck v2", deck.Summary.Title);
    }

    [Fact]
    public async Task Generic_collections_are_loaded_as_records()
    {
        using var lib = new TempLibrary();
        lib.Json("widgets/w.json", new { items = new[] { new { id = "w1", name = "Widget one" }, new { id = "w2", name = "Widget two" } } });
        await lib.LoadAsync();

        var collection = Assert.Single(lib.Queries.GetCollections());
        Assert.Equal("widgets", collection.Name);
        var (total, rows) = lib.Queries.GetGenericRecords("widgets", "two", 10, 0);
        Assert.Equal(1, total);
        Assert.Equal("Widget two", rows.Single().Label);
    }

    [Fact]
    public async Task Cancelling_stops_the_load()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LibraryLoader(lib.Db).LoadAsync(lib.Root, true, null, cts.Token));
    }
}
