using ExportViewer.Core.Discovery;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class ScannerTests
{
    [Fact]
    public void Recognises_every_collection_in_the_standard_layout()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();

        var kinds = LibraryScanner.Scan(lib.Root).Files.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(1, kinds[FileKind.Manifest]);
        Assert.Equal(1, kinds[FileKind.Users]);
        Assert.Equal(1, kinds[FileKind.Logins]);
        Assert.Equal(1, kinds[FileKind.Conversations]);
        Assert.Equal(1, kinds[FileKind.Project]);
        Assert.Equal(1, kinds[FileKind.Memory]);
        Assert.Equal(2, kinds[FileKind.Artifact]);
        Assert.False(kinds.ContainsKey(FileKind.Generic), "artifact version files must not become generic records");
    }

    [Fact]
    public void Recognises_files_by_shape_when_folders_and_names_are_changed()
    {
        using var lib = new TempLibrary();
        lib.Json("people/everyone.json", new[] { Fake.User(Fake.Id(1), "A", "a@example.test") });
        lib.Json("chats/october.json", new[] { Fake.Conversation(Fake.Id(10), Fake.Id(1), "x", 0, 1) });
        lib.Json("misc/proj.json", Fake.Project(Fake.Id(20), "P", Fake.Id(1), 1));
        lib.Json("misc/mem.json", Fake.Memory(Fake.Id(1), 1, "summary"));
        lib.Json("misc/signins.json", Fake.Logins());

        var byName = LibraryScanner.Scan(lib.Root).Files.ToDictionary(f => f.RelativePath, f => f.Kind);

        Assert.Equal(FileKind.Users, byName["people/everyone.json"]);
        Assert.Equal(FileKind.Conversations, byName["chats/october.json"]);
        Assert.Equal(FileKind.Project, byName["misc/proj.json"]);
        Assert.Equal(FileKind.Memory, byName["misc/mem.json"]);
        Assert.Equal(FileKind.Logins, byName["misc/signins.json"]);
    }

    [Fact]
    public void Unknown_json_becomes_a_generic_collection_named_after_its_folder()
    {
        using var lib = new TempLibrary();
        lib.Json("widgets-000/things.json", new[] { new { id = "w1", name = "Widget" } });
        lib.Json("loose.json", new { something = 1 });

        var files = LibraryScanner.Scan(lib.Root).Files.ToDictionary(f => f.RelativePath);

        Assert.Equal(FileKind.Generic, files["widgets-000/things.json"].Kind);
        Assert.Equal("widgets", files["widgets-000/things.json"].Collection);
        Assert.Equal("loose", files["loose.json"].Collection);
    }

    [Fact]
    public void Empty_and_invalid_files_are_skipped_with_a_reason_and_zips_are_counted()
    {
        using var lib = new TempLibrary();
        lib.Write("a/empty.json", "");
        lib.Write("a/broken.json", "this is not json");
        lib.Write("a/conversations-000.zip", "PK");
        lib.Write("a/readme.txt", "hello");

        var scan = LibraryScanner.Scan(lib.Root);

        Assert.Equal("The file is empty.", scan.Files.Single(f => f.RelativePath == "a/empty.json").SkipReason);
        Assert.Equal("The file is not valid JSON.", scan.Files.Single(f => f.RelativePath == "a/broken.json").SkipReason);
        Assert.Equal(1, scan.ZipFiles);
        Assert.Equal(1, scan.OtherFiles);
    }

    [Fact]
    public void Truncated_conversations_file_is_still_recognised_by_shape()
    {
        using var lib = new TempLibrary();
        lib.Write("x/data.json", """[{"uuid":"a","name":"n","chat_messages":[{"uuid":"m" """);

        var f = LibraryScanner.Scan(lib.Root).Files.Single();

        Assert.Equal(FileKind.Conversations, f.Kind);
    }
}
