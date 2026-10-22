using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using ExportViewer.Core.Sources;
using ExportViewer.Core.Tests.Fixtures;
using ExportViewer.Download;

namespace ExportViewer.Core.Tests;

/// <summary>The downloader against a fake handler: no request in these tests leaves the computer.</summary>
public class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiev-tests", "download-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private readonly ExportDownloader _downloader;

    public DownloaderTests()
    {
        Directory.CreateDirectory(_dir);
        _downloader = new ExportDownloader(_handler, TimeSpan.FromMilliseconds(300));
    }

    public void Dispose()
    {
        _downloader.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Uri Link = new($"https://claude.ai/export/{Fake.Id(1)}/00000000/{new string('a', 32)}");

    private static ManifestFile File0(Uri? url = null, string name = "light_metadata-000.zip") => new(0, "light_metadata", 0, name, url ?? Link, null);

    private static byte[] ZipBytes()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("users.json").Open()))
            w.Write("[" + string.Join(",", Enumerable.Repeat("{\"uuid\":\"x\"}", 2000)) + "]");
        return ms.ToArray();
    }

    private static HttpResponseMessage Ok(byte[] body, string type = "application/zip")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return r;
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private Task<DownloadResult> Run(ManifestFile? file = null, CancellationToken ct = default) =>
        _downloader.DownloadOneAsync(file ?? File0(), _dir, null, ct);

    private string[] FilesInFolder() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public async Task Saves_a_zip_under_its_own_name_without_cookies_or_sign_in()
    {
        var body = ZipBytes();
        _handler.Reply = _ => Ok(body);

        var result = await Run();

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(Path.Combine(_dir, "light_metadata-000.zip"), result.Path);
        Assert.Equal(body, System.IO.File.ReadAllBytes(result.Path!));
        Assert.Equal(["light_metadata-000.zip"], FilesInFolder());
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Link, request.Url);
        Assert.DoesNotContain("Cookie", request.Headers);
        Assert.DoesNotContain("Authorization", request.Headers);
        Assert.Contains("AIExportViewer", request.UserAgent);
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "light_metadata-000.zip"), "the user's own file");
        _handler.Reply = _ => Ok(ZipBytes());

        var result = await Run();

        Assert.Equal(Path.Combine(_dir, "light_metadata-000 (1).zip"), result.Path);
        Assert.Equal("the user's own file", System.IO.File.ReadAllText(Path.Combine(_dir, "light_metadata-000.zip")));
        // The renamed copy is still recognised as this manifest's file.
        Assert.True(DownloadedFiles.IsCopyOf("light_metadata-000 (1)", "light_metadata-000"));
    }

    [Fact]
    public async Task Redirects_on_claude_are_followed()
    {
        _handler.Reply = r => r.RequestUri!.AbsolutePath == "/export/final" ? Ok(ZipBytes()) : Redirect("/export/final");

        var result = await Run();

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(["https://claude.ai/export/final"], _handler.Requests.Skip(1).Select(r => r.Url.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://storage.example.test/bucket/file.zip", "storage.example.test")]
    [InlineData("http://claude.ai/export/x", "claude.ai")]
    [InlineData("https://claude.ai:8443/export/x", "claude.ai")]
    public async Task Redirects_anywhere_else_are_refused_before_contacting_them(string to, string host)
    {
        _handler.Reply = _ => Redirect(to);

        var result = await Run();

        Assert.False(result.Ok);
        Assert.Contains(host, result.Problem);
        Assert.Contains("Download in browser", result.Problem);
        Assert.Single(_handler.Requests);
        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task A_redirect_to_sign_in_is_explained()
    {
        _handler.Reply = _ => Redirect("https://claude.ai/login?returnTo=%2Fexport");
        var result = await Run();
        Assert.Contains("sign-in", result.Problem);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Endless_redirects_stop()
    {
        var n = 0;
        _handler.Reply = _ => Redirect($"/export/{n++}");
        var result = await Run();
        Assert.Contains("too many times", result.Problem);
        Assert.Equal(ExportDownloader.MaxRedirects + 1, _handler.Requests.Count);
    }

    [Fact]
    public async Task A_web_page_instead_of_the_zip_is_not_kept()
    {
        _handler.Reply = _ => Ok("<html>Sign in</html>"u8.ToArray(), "text/html");
        Assert.Contains("web page instead of the zip", (await Run()).Problem);

        // Same when the server claims it is a file.
        _handler.Reply = _ => Ok("<html>Sign in</html>"u8.ToArray(), "application/octet-stream");
        Assert.Contains("web page instead of the zip", (await Run()).Problem);
        Assert.Empty(FilesInFolder());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "didn't accept the link")]
    [InlineData(HttpStatusCode.Unauthorized, "didn't accept the link")]
    [InlineData(HttpStatusCode.NotFound, "no longer works")]
    [InlineData(HttpStatusCode.Gone, "no longer works")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "busy or had a problem")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy or had a problem")]
    [InlineData(HttpStatusCode.NoContent, "unexpected reply")]
    public async Task Refusals_are_explained_in_plain_words(HttpStatusCode code, string expected)
    {
        _handler.Reply = _ => new HttpResponseMessage(code) { Content = new StringContent("") };
        var result = await Run();
        Assert.Contains(expected, result.Problem);
        Assert.Equal((int)code, result.HttpStatus);
        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task A_browser_check_is_explained()
    {
        _handler.Reply = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html></html>") };
            r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return r;
        };
        Assert.Contains("browser check", (await Run()).Problem);
    }

    [Fact]
    public async Task A_download_that_stops_part_way_is_not_kept()
    {
        var body = ZipBytes();
        _handler.Reply = _ => Ok(body[..(body.Length / 2)]);

        var result = await Run();

        Assert.Contains("stopped before the file was complete", result.Problem);
        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task A_link_that_is_not_a_claude_export_is_never_requested()
    {
        var result = await Run(File0(new Uri("https://evil.example.test/export/x")));
        Assert.False(result.Ok);
        Assert.Empty(_handler.Requests);

        result = await Run(File0(name: "../outside.zip"));
        Assert.False(result.Ok);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task No_connection_is_explained()
    {
        _handler.Reply = _ => throw new HttpRequestException("no route");
        Assert.Contains("could not reach claude.ai", (await Run()).Problem);
    }

    [Fact]
    public async Task A_stalled_download_gives_up_and_leaves_nothing_behind()
    {
        _handler.Reply = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("PK"u8.ToArray())) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return r;
        };

        var result = await Run();

        Assert.Contains("stopped responding", result.Problem);
        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task Cancelling_removes_the_partly_written_file()
    {
        using var cts = new CancellationTokenSource();
        _handler.Reply = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("PK"u8.ToArray(), cts)) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return r;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(ct: cts.Token));
        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task Expired_links_are_not_requested()
    {
        var manifest = new ExportManifest(DateTimeOffset.UtcNow.AddHours(-25), "1.0", 1, [File0()]);
        var results = await _downloader.DownloadAsync(manifest, manifest.Files, _dir);
        Assert.Contains("expired", Assert.Single(results).Problem);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Downloads_every_file_in_turn_with_progress()
    {
        var other = new ManifestFile(1, "projects", 0, "projects-000.zip", new Uri("https://claude.ai/export/b"), null);
        var manifest = new ExportManifest(DateTimeOffset.UtcNow, "1.0", 2, [File0(), other]);
        _handler.Reply = _ => Ok(ZipBytes());
        var reports = new List<DownloadProgress>();

        var results = await _downloader.DownloadAsync(manifest, manifest.Files, _dir, new SyncProgress<DownloadProgress>(reports.Add));

        Assert.All(results, r => Assert.True(r.Ok, r.Problem));
        Assert.Equal(["light_metadata-000.zip", "projects-000.zip"], FilesInFolder());
        Assert.Contains(reports, p => p.FileName == "projects-000.zip" && p.FileNumber == 2 && p.Bytes > 0);
    }

    [Theory]
    [InlineData("https://claude.ai/export/x", true)]
    [InlineData("https://claude.ai/anything", true)]
    [InlineData("https://claude.ai.example.test/x", false)]
    [InlineData("https://anthropic.com/x", false)]
    [InlineData("http://claude.ai/x", false)]
    public void Only_claude_over_https_is_contacted(string url, bool allowed) =>
        Assert.Equal(allowed, ExportDownloader.IsAllowedAddress(new Uri(url)));

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record SeenRequest(Uri Url, IReadOnlyList<string> Headers, string UserAgent);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SeenRequest(request.RequestUri!, request.Headers.Select(h => h.Key).ToList(), request.Headers.UserAgent.ToString()));
            var response = Reply(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Returns its first bytes, then never sends more (optionally cancelling once they are read).</summary>
    private sealed class StallingStream(byte[] first, CancellationTokenSource? cancelAfterFirst = null) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            cancelAfterFirst?.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
