using System.Text.Json;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Store;

namespace ExportViewer.Core.Tests.Fixtures;

/// <summary>A throwaway library folder on disk plus an in-memory database to load it into.</summary>
internal sealed class TempLibrary : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "aiev-tests", Guid.NewGuid().ToString("N"));

    public LibraryDatabase Db { get; } = LibraryDatabase.OpenInMemory();

    public LibraryQueries Queries => new(Db);

    public TempLibrary() => Directory.CreateDirectory(Root);

    public string Json(string relativePath, object value) => Write(relativePath, JsonSerializer.Serialize(value, Options));

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public Task<LoadSummary> LoadAsync(bool rebuild = true) => new LibraryLoader(Db).LoadAsync(Root, rebuild);

    /// <summary>A small but complete export: two people, conversations with every block type, a project, memory and artifacts.</summary>
    public void WriteStandardExport(string folder = "export-a")
    {
        Json($"{folder}/manifest-test.json", Fake.Manifest(0, "light_metadata-000.zip", "conversations-000.zip"));
        Json($"{folder}/light_metadata-000/users.json", new[]
        {
            Fake.User(Fake.Id(1), "Alex Example", "alex@example.test", "+61400000000"),
            Fake.User(Fake.Id(2), "Sam Sample", "sam@example.test"),
        });
        Json($"{folder}/light_metadata-000/login_history.json", Fake.Logins(Fake.Login(Fake.Id(1), 1), Fake.Login(Fake.Id(2), 2, "198.51.100.7")));

        var toolConv = Fake.Conversation(Fake.Id(100), Fake.Id(1), "Research quokkas", 10, 20,
            Fake.Message(Fake.Id(101), Fake.Root, "human", 10, [Fake.Injected("Memory: likes marsupials"), Fake.Text("Tell me about quokkas")],
                attachments: [Fake.Attachment("Attached notes mention Rottnest Island", "notes.txt")]),
            Fake.Message(Fake.Id(102), Fake.Id(101), "assistant", 11,
            [
                Fake.Thinking("Planning a web search"),
                Fake.ToolUse("toolu_1", "web_search", new { query = "quokka habitat" }, "Searching the web"),
                Fake.ToolResult("toolu_1", "web_search", [Fake.Knowledge("Quokka facts", "https://example.org/quokka", "Quokkas live on islands")]),
                Fake.Text("Quokkas are small marsupials."),
            ], files: [Fake.FileRef(Fake.Id(150), "quokka.png")]));

        var plainConv = Fake.Conversation(Fake.Id(200), Fake.Id(2), "Budget planning", 30, 31,
            Fake.Turn(201, 202, Fake.Root, 30, "Help me plan a budget", "Here is a budget outline."));

        Json($"{folder}/conversations-000/conversations.json", new[] { toolConv, plainConv });
        Json($"{folder}/projects-000/projects/{Fake.Id(900)}.json", Fake.Project(Fake.Id(900), "Wombat project", Fake.Id(1), 5,
            Fake.Doc(Fake.Id(901), "brief.md", "Wombats dig burrows")));
        Json($"{folder}/memories-000/memories/{Fake.Id(1)}.json", Fake.Memory(Fake.Id(1), 3, "Alex works on marsupial research", ("/profile.md", "Prefers short answers")));

        Json($"{folder}/frames-000/artifacts/{Fake.Id(800)}/artifact.json", Fake.Artifact(Fake.Id(800), "page", Fake.Id(1), 4, ("1790000000-aaaa", "Platypus page")));
        Write($"{folder}/frames-000/artifacts/{Fake.Id(800)}/page.md", "# Platypus\n\nEchidna notes");
        Write($"{folder}/frames-000/artifacts/{Fake.Id(800)}/comments.json", "[]");
        Json($"{folder}/frames-000/artifacts/{Fake.Id(801)}/artifact.json", Fake.Artifact(Fake.Id(801), "artifact", Fake.Id(2), 4,
            ("1790000001-bbbb", "Deck v2"), ("1790000000-cccc", "Deck v1")));
        Write($"{folder}/frames-000/artifacts/{Fake.Id(801)}/versions/1790000001-bbbb/index.html", "<html><script>alert(1)</script></html>");
        Json($"{folder}/frames-000/artifacts/{Fake.Id(801)}/versions/1790000001-bbbb.files.json", new { files = Array.Empty<object>() });
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
