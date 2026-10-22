using System.Net;
using Velopack.Sources;

namespace ExportViewer.Download;

/// <summary>
/// The only way Velopack reaches the network. Same rules as <see cref="ExportDownloader"/>:
/// <list type="bullet">
/// <item>Only HTTPS to <see cref="AppUpdater.AllowedHosts"/>. Redirects are followed by hand and each one is checked the same way.</item>
/// <item>No cookies, no saved sign-in, no proxy password, no automatic decompression.</item>
/// <item>Headers Velopack asks for are dropped, and so is the query string of the first request (Velopack adds the
/// app's version and machine details there; GitHub doesn't need them).</item>
/// </list>
/// </summary>
internal sealed class UpdateDownloader : IFileDownloader, IDisposable
{
    public const int MaxRedirects = 5;

    /// <summary>The release list is a few kilobytes; anything near this is not a release list.</summary>
    internal const int MaxTextBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateDownloader() : this(CreateHandler())
    {
    }

    internal UpdateDownloader(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AIExportViewer");
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>An HTTPS address on an allowed host, with no user name and the default port.</summary>
    public static bool IsAllowedAddress(Uri? url) =>
        url is { IsAbsoluteUri: true }
        && url.Scheme == Uri.UriSchemeHttps
        && AppUpdater.AllowedHosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase)
        && url.IsDefaultPort
        && url.UserInfo.Length == 0;

    public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null,
        double timeout = 30, CancellationToken cancelToken = default)
    {
        using var limit = Limit(timeout, cancelToken);
        using var response = await OpenAsync(url, limit.Token);
        var length = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(limit.Token);
        await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long total = 0;
        var reported = -1;
        while (await input.ReadAsync(buffer, limit.Token) is var n and > 0)
        {
            total += n;
            if (total > length) throw new HttpRequestException("The update server sent more data than expected.");
            await output.WriteAsync(buffer.AsMemory(0, n), limit.Token);
            if (length is > 0 and var l && (int)(total * 100 / l) is var percent && percent != reported)
                progress(reported = percent);
        }
        if (length is { } expected && total != expected) throw new HttpRequestException("The update download stopped before it was complete.");
        progress(100);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var limit = Limit(timeout, CancellationToken.None);
        using var response = await OpenAsync(url, limit.Token);
        if (response.Content.Headers.ContentLength > MaxTextBytes) throw new HttpRequestException("The update list is larger than expected.");
        await using var input = await response.Content.ReadAsStreamAsync(limit.Token);
        using var ms = new MemoryStream();
        var buffer = new byte[1 << 16];
        while (await input.ReadAsync(buffer, limit.Token) is var n and > 0)
        {
            if (ms.Length + n > MaxTextBytes) throw new HttpRequestException("The update list is larger than expected.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));

    private static CancellationTokenSource Limit(double minutes, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (minutes > 0) cts.CancelAfter(TimeSpan.FromMinutes(minutes));
        return cts;
    }

    /// <summary>Sends the request and follows redirects by hand, checking every address before it is contacted.</summary>
    private async Task<HttpResponseMessage> OpenAsync(string address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) throw new BlockedAddressException("not a web address");
        // Redirect targets keep their query string: GitHub's download host needs its signed one.
        url = new UriBuilder(url) { Query = "" }.Uri;
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowedAddress(url)) throw new BlockedAddressException(url.IsAbsoluteUri ? url.Host : "another address");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("*/*");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308))
            {
                if (status == 200) return response;
                response.Dispose();
                throw new HttpRequestException($"The update server replied with code {status}.", null, (HttpStatusCode)status);
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("The update server sent an unexpected reply.");
            if (hop >= MaxRedirects) throw new HttpRequestException("The update server redirected too many times.");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>An update request, or one of its redirects, pointed somewhere the app doesn't download from.</summary>
public sealed class BlockedAddressException(string host) : Exception($"Updates are not downloaded from {host}.")
{
    public string Host { get; } = host;
}
