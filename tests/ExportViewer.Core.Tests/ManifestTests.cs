using System.IO.Compression;
using System.Text.Json;
using ExportViewer.Core.Sources;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class ManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiev-tests", "manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Link(int n) => $"https://claude.ai/export/{Fake.Id(1)}/{n:D8}/{new string('a', 32)}";

    private static object File(int index, string category, string filename, string? url = null, int part = 0) =>
        new { batch_index = index, export_url = url ?? Link(index), category, part, filename };

    private static ExportManifest Parse(params object[] files) =>
        ExportManifest.Parse(JsonSerializer.SerializeToElement(new
        {
            instructions = "Download each file using the export_url.",
            created_at = "2026-01-05T09:00:00.000000+00:00",
            total_files = files.Length,
            version = "1.0",
            data_files = files,
        }));

    private string Zip(string name, bool complete = true)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("users.json").Open()))
            w.Write(new string('x', 5000));
        if (!complete)
        {
            // A download that stopped part-way: the zip's index at the end is missing.
            var bytes = System.IO.File.ReadAllBytes(path);
            System.IO.File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        }
        return path;
    }

    [Fact]
    public void Reads_files_in_batch_order_with_the_link_expiry()
    {
        var m = Parse(File(1, "conversations", "conversations-000.zip"), File(0, "light_metadata", "light_metadata-000.zip"));

        Assert.Equal(["light_metadata-000.zip", "conversations-000.zip"], m.Files.Select(f => f.FileName));
        Assert.All(m.Files, f => { Assert.Null(f.Problem); Assert.NotNull(f.Url); });
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero), m.LinksExpireAt);
        Assert.False(m.LinksExpired(new DateTimeOffset(2026, 1, 6, 8, 59, 0, TimeSpan.Zero)));
        Assert.True(m.LinksExpired(new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal("1.0", m.Version);
        Assert.Equal(2, m.TotalFiles);
    }

    [Theory]
    [InlineData("https://claude.ai/export/abc/def", true)]
    [InlineData("https://CLAUDE.AI/export/abc", true)]
    [InlineData("http://claude.ai/export/abc", false)]
    [InlineData("https://claude.ai.example.test/export/abc", false)]
    [InlineData("https://evil.example.test/export/abc?x=claude.ai", false)]
    [InlineData("https://sub.claude.ai/export/abc", false)]
    [InlineData("https://claude.ai:8443/export/abc", false)]
    [InlineData("https://user:pw@claude.ai/export/abc", false)]
    [InlineData("https://claude.ai/login?next=/export/abc", false)]
    [InlineData("file:///C:/export/abc", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_https_links_to_claude_exports_are_trusted(string url, bool trusted)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        Assert.Equal(trusted, ExportManifest.IsTrustedDownloadUrl(uri));
    }

    [Fact]
    public void Untrusted_links_and_unsafe_names_are_not_offered()
    {
        var m = Parse(
            File(0, "light_metadata", "light_metadata-000.zip", url: "https://downloads.example.test/export/x"),
            File(1, "projects", "../../evil.zip"),
            File(2, "memories", "memories-000.zip", url: "not a link"),
            File(3, "../x", "..\\..\\x.exe"),
            File(4, "frames", "frames-000.zip"),
            File(5, "frames", "frames-000.zip"));

        Assert.Contains("does not go to claude.ai", m.Files[0].Problem);
        Assert.Null(m.Files[0].Url);
        // A bad file name falls back to one built from the category and part.
        Assert.Equal("projects-000.zip", m.Files[1].FileName);
        Assert.Null(m.Files[1].Problem);
        Assert.Contains("no download link", m.Files[2].Problem);
        Assert.Contains("unusual name", m.Files[3].Problem);
        Assert.False(ExportManifest.IsSafeFileName(m.Files[3].FileName));
        Assert.Null(m.Files[4].Problem);
        Assert.Contains("twice", m.Files[5].Problem);
        Assert.Null(m.Files[5].Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"data_files\": 3}")]
    public void Json_that_is_not_a_manifest_is_rejected_in_plain_words(string json)
    {
        var ex = Assert.Throws<ManifestException>(() => ExportManifest.Parse(JsonDocument.Parse(json).RootElement));
        Assert.Contains("not an export manifest", ex.Message);
    }

    [Fact]
    public void Reading_a_file_that_is_not_json_gives_a_friendly_message()
    {
        var path = Path.Combine(_dir, "manifest.json");
        System.IO.File.WriteAllText(path, "<html>sign in</html>");
        Assert.Contains("not an export manifest", Assert.Throws<ManifestException>(() => ExportManifest.Read(path)).Message);
        Assert.Contains("could not be found", Assert.Throws<ManifestException>(() => ExportManifest.Read(Path.Combine(_dir, "none.json"))).Message);
    }

    [Fact]
    public void Missing_fields_are_tolerated()
    {
        var m = ExportManifest.Parse(JsonDocument.Parse("""{"data_files":[{"category":"projects","part":2,"export_url":"https://claude.ai/export/a"}]}""").RootElement);
        Assert.Null(m.CreatedAt);
        Assert.Null(m.LinksExpireAt);
        Assert.False(m.LinksExpired(DateTimeOffset.MaxValue));
        Assert.Equal("projects-002.zip", Assert.Single(m.Files).FileName);
    }

    [Fact]
    public void Finds_downloaded_zips_including_renamed_repeats_and_spots_unfinished_ones()
    {
        var m = Parse(
            File(0, "light_metadata", "light_metadata-000.zip"),
            File(1, "projects", "projects-000.zip"),
            File(2, "memories", "memories-000.zip"),
            File(3, "conversations", "conversations-000.zip"));
        Zip("light_metadata-000.zip");
        Zip("projects-000 (1).zip");
        Zip("conversations-000.zip", complete: false);
        Zip("conversations-0001.zip");
        System.IO.File.WriteAllText(Path.Combine(_dir, "memories-000.zip.crdownload"), "partial");

        var found = DownloadedFiles.Check(m, _dir);

        Assert.Equal([DownloadedState.Ready, DownloadedState.Ready, DownloadedState.InProgress, DownloadedState.Damaged], found.Select(f => f.State));
        Assert.EndsWith("projects-000 (1).zip", found[1].Path);
        Assert.EndsWith("conversations-000.zip", found[3].Path);
    }

    [Fact]
    public void Firefox_placeholder_with_a_part_file_counts_as_in_progress()
    {
        var m = Parse(File(0, "projects", "projects-000.zip"));
        System.IO.File.WriteAllBytes(Path.Combine(_dir, "projects-000.zip"), []);
        System.IO.File.WriteAllText(Path.Combine(_dir, "projects-000.zip.part"), "partial");

        Assert.Equal(DownloadedState.InProgress, Assert.Single(DownloadedFiles.Check(m, _dir)).State);
    }

    [Fact]
    public void The_newest_copy_that_opens_is_used()
    {
        var m = Parse(File(0, "light_metadata", "light_metadata-000.zip"));
        var good = Zip("light_metadata-000.zip");
        var newerBroken = Zip("light_metadata-000 (1).zip", complete: false);
        System.IO.File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(-5));
        System.IO.File.SetLastWriteTimeUtc(newerBroken, DateTime.UtcNow);

        Assert.Equal(good, Assert.Single(DownloadedFiles.Check(m, _dir)).Path);
    }

    [Theory]
    [InlineData("projects-000", true)]
    [InlineData("projects-000 (2)", true)]
    [InlineData("projects-000(2)", true)]
    [InlineData("projects-000-1", true)]
    [InlineData("projects-0001", false)]
    [InlineData("projects-000 copy", false)]
    [InlineData("projects-001", false)]
    public void Repeat_download_names_are_recognised(string name, bool match) =>
        Assert.Equal(match, DownloadedFiles.IsCopyOf(name, "projects-000"));

    [Fact]
    public void A_missing_folder_means_nothing_is_downloaded()
    {
        var m = Parse(File(0, "projects", "projects-000.zip"));
        Assert.Equal(DownloadedState.Missing, Assert.Single(DownloadedFiles.Check(m, Path.Combine(_dir, "nope"))).State);
    }
}
