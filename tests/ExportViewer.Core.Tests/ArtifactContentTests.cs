using ExportViewer.Core.Artifacts;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class ArtifactContentTests
{
    [Fact]
    public void Lists_files_from_the_export_listing_and_reads_text()
    {
        using var lib = new TempLibrary();
        lib.Write("a/versions/v1/index.html", "<p>hi</p>");
        lib.Json("a/versions/v1.files.json", new { files = new[] { new { original_path = "index.html", exported_as = "index.html", sha256 = "x", size_bytes = 9, mime_type = "text/html" } } });

        var files = ArtifactContent.ListFiles(lib.Root, "a/versions/v1");

        Assert.Equal("index.html", Assert.Single(files).Path);
        Assert.Equal("<p>hi</p>", ArtifactContent.ReadText(lib.Root, "a/versions/v1", "index.html")!.Value.Text);
        Assert.False(ArtifactContent.VerifyHash(lib.Root, "a/versions/v1", files[0]));
    }

    [Theory]
    [InlineData("../../../outside.txt")]
    [InlineData("..\\..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void Paths_cannot_escape_the_version_folder(string path)
    {
        using var lib = new TempLibrary();
        lib.Write("outside.txt", "secret");
        lib.Write("a/versions/v1/index.html", "x");

        Assert.Null(ArtifactContent.ReadText(lib.Root, "a/versions/v1", path));
        Assert.Null(ArtifactContent.Resolve(lib.Root, "../..", "x"));
    }

    [Fact]
    public void Binary_files_are_not_shown_as_text()
    {
        using var lib = new TempLibrary();
        var path = Path.Combine(lib.Root, "a/v/font.woff2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x77, 0x4F, 0x46, 0x32, 0x00, 0x01]);

        Assert.Null(ArtifactContent.ReadText(lib.Root, "a/v", "font.woff2"));
    }

    [Fact]
    public void Preview_document_starts_with_a_blocking_csp()
    {
        var doc = ArtifactContent.PreviewDocument("<script>alert(1)</script>");

        Assert.StartsWith("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'", doc);
    }

    [Fact]
    public void Page_markdown_tab_markers_become_headings()
    {
        var md = ArtifactContent.CleanPageMarkdown("<!-- tab: Notes -->\n# Title\n<!-- hidden -->text");

        Assert.Contains("**Tab: Notes**", md);
        Assert.DoesNotContain("<!--", md);
    }

    [Fact]
    public void Parses_both_comment_formats()
    {
        var a = ArtifactContent.ParseComments("""[{"resolved":true,"tab":"T","comments":[{"body":"Looks good","author":{"uuid":"u","full_name":"Pat"},"posted_by_agent":true,"created_at":"2026-01-01T00:00:00.000Z"}]}]""");
        var b = ArtifactContent.ParseComments("""{"threads":[{"resolved":false,"comments":[{"author_role":"page","text":"Fix this","created_at":"2026-01-01T00:00:00+00:00"}]}]}""");

        Assert.Equal(("Pat", "Looks good", true), (a[0].Comments[0].Author, a[0].Comments[0].Body, a[0].Comments[0].ByClaude));
        Assert.Equal("Fix this", b[0].Comments[0].Body);
        Assert.Empty(ArtifactContent.ParseComments("not json"));
    }
}
