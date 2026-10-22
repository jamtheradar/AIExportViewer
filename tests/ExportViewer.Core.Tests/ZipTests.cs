using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Sources;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class ZipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiev-tests", "zip-" + Guid.NewGuid().ToString("N"));

    public ZipTests() => Directory.CreateDirectory(Path.Combine(_dir, "downloads"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, "downloads", name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(content);
        }
        return path;
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private IReadOnlyList<string> StandardZips()
    {
        Zip("light_metadata-000.zip", ("users.json", J(new[] { Fake.User(Fake.Id(1), "Alex Example", "alex@example.test") })));
        Zip("conversations-000.zip", ("conversations.json", J(new[]
        {
            Fake.Conversation(Fake.Id(100), Fake.Id(1), "Zipped chat", 10, 11, Fake.Turn(101, 102, Fake.Root, 10, "Hello wombat", "Hi")),
        })));
        Zip("projects-000.zip", ($"projects/{Fake.Id(900)}.json", J(Fake.Project(Fake.Id(900), "Zipped project", Fake.Id(1), 5))));
        File.WriteAllText(Path.Combine(_dir, "downloads", "manifest.json"), J(Fake.Manifest(0, "light_metadata-000.zip")));
        File.WriteAllText(Path.Combine(_dir, "downloads", "holiday-photos.zip"), "not an export");
        return ZipExtractor.FindExportFiles(Path.Combine(_dir, "downloads"));
    }

    [Fact]
    public async Task Export_zips_unzip_into_a_library_that_loads_and_reopens_unchanged()
    {
        var sources = StandardZips();
        Assert.Equal(["conversations-000.zip", "light_metadata-000.zip", "manifest.json", "projects-000.zip"], sources.Select(Path.GetFileName));
        var hashes = sources.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

        var target = Path.Combine(_dir, "work");
        var result = ZipExtractor.Extract(sources, target);
        Assert.Empty(result.Problems);
        Assert.True(File.Exists(Path.Combine(target, "conversations-000", "conversations.json")));
        Assert.True(File.Exists(Path.Combine(target, "projects-000", "projects", Fake.Id(900) + ".json")));
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));

        using var db = Store.LibraryDatabase.OpenInMemory();
        await new LibraryLoader(db).LoadAsync(target, rebuildIfChanged: true);
        var q = new Store.LibraryQueries(db);
        Assert.Equal(1, q.GetOverview().Conversations);
        Assert.Equal("Zipped project", Assert.Single(q.GetProjects()).Name);

        // Unzipping the same zips again must look unchanged, so reopening is instant.
        ZipExtractor.Extract(sources, target);
        Assert.True(new LibraryLoader(db).Compare(Discovery.LibraryScanner.Scan(target)).IsUpToDate);

        // The zip files themselves are never changed.
        Assert.All(sources, p => Assert.Equal(hashes[p], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
    }

    [Fact]
    public void Entries_that_point_outside_the_folder_are_skipped()
    {
        var zip = Zip("conversations-000.zip", ("../escape.json", "[]"), ("ok/fine.json", "[]"), ("C:/abs.json", "[]"), ("a/../../b.json", "[]"));
        var target = Path.Combine(_dir, "work");
        var result = ZipExtractor.Extract([zip], target);

        Assert.Equal(1, result.Files);
        Assert.Contains(result.Problems, p => p.Contains("pointed outside the zip"));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "b.json")));
        Assert.Null(ZipExtractor.SafeDestination(target, "..\\x.json"));
        Assert.NotNull(ZipExtractor.SafeDestination(target, "projects/x.json"));
    }

    [Fact]
    public void A_damaged_zip_is_reported_and_the_rest_still_unzip()
    {
        var good = Zip("projects-000.zip", ("projects/p.json", "{}"));
        var bad = Path.Combine(_dir, "downloads", "conversations-000.zip");
        File.WriteAllText(bad, "this is not a zip");
        var result = ZipExtractor.Extract([good, bad], Path.Combine(_dir, "work"));

        Assert.Equal(1, result.Files);
        Assert.Contains(result.Problems, p => p.StartsWith("conversations-000.zip is not a zip file"));
        Assert.Throws<UnzipException>(() => ZipExtractor.Extract([bad], Path.Combine(_dir, "work2")));
    }

    [Fact]
    public void Cancelling_removes_the_partly_unzipped_folder()
    {
        var zip = Zip("conversations-000.zip", ("conversations.json", "[]"));
        var target = Path.Combine(_dir, "work");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ZipExtractor.Extract([zip], target, null, cts.Token));
        Assert.False(Directory.Exists(target));
    }
}
