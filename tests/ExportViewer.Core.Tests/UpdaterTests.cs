using System.Net;
using ExportViewer.Download;

namespace ExportViewer.Core.Tests;

/// <summary>The app updater's downloader against a fake handler: no request in these tests leaves the computer.</summary>
public class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aiev-tests", "update-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private readonly UpdateDownloader _downloader;

    private const string Feed = AppUpdater.ReleasesUrl + "releases.win.json";
    private const string AssetHost = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r&sig=abc";

    public UpdaterTests()
    {
        Directory.CreateDirectory(_dir);
        _downloader = new UpdateDownloader(_handler);
    }

    public void Dispose()
    {
        _downloader.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    [Fact]
    public void Updates_come_only_from_the_projects_github_releases()
    {
        var url = new Uri(AppUpdater.ReleasesUrl);
        Assert.True(UpdateDownloader.IsAllowedAddress(url));
        Assert.Equal("/jamtheradar/AIExportViewer/releases/latest/download/", url.AbsolutePath);
        Assert.Equal(["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"], AppUpdater.AllowedHosts);
        // Export downloads and app updates keep separate lists: neither can reach the other's hosts.
        Assert.Empty(AppUpdater.AllowedHosts.Intersect(ExportDownloader.AllowedHosts));
    }

    [Fact]
    public async Task Sends_no_query_string_cookies_or_sign_in_and_follows_github_redirects()
    {
        _handler.Reply = r => r.RequestUri!.Host == "github.com"
            ? Redirect(AssetHost)
            : Ok("{\"Assets\":[]}");

        var text = await _downloader.DownloadString(Feed + "?arch=x64&os=win&id=AIExportViewer&localVersion=0.1.0",
            new Dictionary<string, string> { ["Authorization"] = "Bearer should-not-be-sent" });

        Assert.Equal("{\"Assets\":[]}", text);
        Assert.Equal([Feed, AssetHost], _handler.Requests.Select(r => r.Url.AbsoluteUri));
        foreach (var request in _handler.Requests)
        {
            Assert.DoesNotContain("Cookie", request.Headers);
            Assert.DoesNotContain("Authorization", request.Headers);
            Assert.Contains("AIExportViewer", request.UserAgent);
        }
    }

    [Theory]
    [InlineData("https://updates.example.test/AIExportViewer-full.nupkg", "updates.example.test")]
    [InlineData("http://github.com/jamtheradar/AIExportViewer/releases/download/v0.2.0/x.nupkg", "github.com")]
    [InlineData("https://github.com:8443/x.nupkg", "github.com")]
    [InlineData("https://claude.ai/x.nupkg", "claude.ai")]
    public async Task Redirects_anywhere_else_are_refused_before_contacting_them(string to, string host)
    {
        _handler.Reply = _ => Redirect(to);

        var ex = await Assert.ThrowsAsync<BlockedAddressException>(() => _downloader.DownloadBytes(Feed));

        Assert.Equal(host, ex.Host);
        Assert.Single(_handler.Requests);
        Assert.Contains(host, AppUpdater.Explain(ex));
    }

    [Theory]
    [InlineData("https://updates.example.test/releases.win.json")]
    [InlineData("http://github.com/releases.win.json")]
    [InlineData("not a url")]
    public async Task A_starting_address_off_the_list_is_never_contacted(string url)
    {
        await Assert.ThrowsAsync<BlockedAddressException>(() => _downloader.DownloadString(url));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Too_many_redirects_stop()
    {
        _handler.Reply = _ => Redirect(Feed);

        await Assert.ThrowsAsync<HttpRequestException>(() => _downloader.DownloadString(Feed));

        Assert.Equal(UpdateDownloader.MaxRedirects + 1, _handler.Requests.Count);
    }

    [Fact]
    public async Task Downloads_a_file_and_reports_progress_to_100()
    {
        var body = new byte[300_000];
        Random.Shared.NextBytes(body);
        _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        var target = Path.Combine(_dir, "AIExportViewer-0.2.0-full.nupkg");
        var seen = new List<int>();

        await _downloader.DownloadFile(AppUpdater.ReleasesUrl + "AIExportViewer-0.2.0-full.nupkg", target, seen.Add);

        Assert.Equal(body, File.ReadAllBytes(target));
        Assert.Equal(100, seen[^1]);
        Assert.Equal(seen.Order(), seen);
    }

    [Fact]
    public async Task Failures_are_explained_in_plain_words()
    {
        _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var missing = await Assert.ThrowsAsync<HttpRequestException>(() => _downloader.DownloadString(Feed));
        Assert.Equal("No updates have been published yet.", AppUpdater.Explain(missing));

        _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var busy = await Assert.ThrowsAsync<HttpRequestException>(() => _downloader.DownloadString(Feed));
        Assert.Contains("code 503", AppUpdater.Explain(busy));

        Assert.Contains("internet connection", AppUpdater.Explain(new HttpRequestException("offline")));
    }

    [Fact]
    public async Task A_copy_that_was_not_installed_never_checks()
    {
        using var updater = new AppUpdater(new UpdateDownloader(_handler));

        Assert.False(updater.IsInstalled);
        Assert.Null(updater.CurrentVersion);
        Assert.Null(updater.ReadyVersion);
        var result = await updater.CheckAsync();
        Assert.True(result.Ok);
        Assert.Null(result.Version);
        Assert.Empty(_handler.Requests);
    }

    private sealed record SeenRequest(Uri Url, List<string> Headers, string UserAgent);

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
}
