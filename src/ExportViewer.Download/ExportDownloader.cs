using System.Diagnostics;
using System.Net;
using ExportViewer.Core.Sources;

namespace ExportViewer.Download;

public sealed record DownloadProgress(string FileName, int FileNumber, int FileCount, long Bytes, long? TotalBytes);

/// <param name="Path">Where the zip was saved, or null when it wasn't.</param>
/// <param name="Problem">Why it wasn't saved, in plain words that can be shown to the user as is.</param>
/// <param name="HttpStatus">The final response code, for the log. Never shown to managers.</param>
public sealed record DownloadResult(ManifestFile File, string? Path, string? Problem, long Bytes, int? HttpStatus, TimeSpan Elapsed)
{
    public bool Ok => Problem is null && Path is not null;
}

/// <summary>
/// Downloads an export's zip files from claude.ai when the user asks. Apart from <see cref="AppUpdater"/>, this is the app's only network code.
/// <list type="bullet">
/// <item>Only HTTPS to <see cref="AllowedHosts"/>. Redirects are followed by hand and each one is checked the same way.</item>
/// <item>No cookies, no saved sign-in, no proxy password, no automatic decompression.</item>
/// <item>Each file is written to a temporary name, checked to open as a zip, then renamed. An existing file is never
/// overwritten: a repeat gets <c>name (1).zip</c>, which <see cref="DownloadedFiles"/> recognises.</item>
/// <item>One file at a time, as each link may only work once.</item>
/// </list>
/// </summary>
public sealed class ExportDownloader : IDisposable
{
    /// <summary>
    /// Hosts the app downloads from, the first request and every redirect. If claude.ai turns out to redirect to a
    /// storage host, add it here only after checking it with the owner.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedHosts = [ExportManifest.TrustedHost];

    public const int MaxRedirects = 5;

    /// <summary>Same limit as unzipping; no export file should come near it.</summary>
    public const long MaxBytes = 200L * 1024 * 1024 * 1024;

    /// <summary>Suffix of the file being written. Not one browsers use, so a leftover is never mistaken for theirs.</summary>
    internal const string TempSuffix = ".downloading";

    private readonly HttpClient _http;
    private readonly TimeSpan _stallTimeout;

    public ExportDownloader() : this(CreateHandler(), TimeSpan.FromSeconds(60))
    {
    }

    internal ExportDownloader(HttpMessageHandler handler, TimeSpan stallTimeout)
    {
        _stallTimeout = stallTimeout;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AIExportViewer/0.1");
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
        && AllowedHosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase)
        && url.IsDefaultPort
        && url.UserInfo.Length == 0;

    /// <summary>Downloads <paramref name="files"/> one after another into <paramref name="folder"/>.</summary>
    /// <exception cref="OperationCanceledException">The user cancelled. The file being written is removed.</exception>
    public async Task<IReadOnlyList<DownloadResult>> DownloadAsync(ExportManifest manifest, IReadOnlyList<ManifestFile> files, string folder,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DownloadResult>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            if (manifest.LinksExpired(DateTimeOffset.UtcNow))
            {
                results.Add(new DownloadResult(file, null, "The download links have expired.", 0, null, TimeSpan.Zero));
                continue;
            }
            progress?.Report(new DownloadProgress(file.FileName, i + 1, files.Count, 0, null));
            results.Add(await DownloadOneAsync(file, folder, b => progress?.Report(new DownloadProgress(file.FileName, i + 1, files.Count, b.Done, b.Total)), ct));
        }
        return results;
    }

    internal async Task<DownloadResult> DownloadOneAsync(ManifestFile file, string folder, Action<(long Done, long? Total)>? onBytes, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        DownloadResult Fail(string problem, int? status = null, long bytes = 0) => new(file, null, problem, bytes, status, clock.Elapsed);

        if (!ExportManifest.IsTrustedDownloadUrl(file.Url) || !IsAllowedAddress(file.Url) || !ExportManifest.IsSafeFileName(file.FileName))
            return Fail("This file has no download link the app can use.");
        if (!Directory.Exists(folder))
            return Fail("The folder to save into could not be found.");

        HttpResponseMessage response;
        try
        {
            var opened = await OpenAsync(file.Url!, ct);
            if (opened.Problem is { } p) return Fail(p, opened.Status);
            response = opened.Response!;
        }
        catch (HttpRequestException)
        {
            return Fail(NoConnection);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(NoConnection);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (Refused(response) is { } refused) return Fail(refused, status);

            var length = response.Content.Headers.ContentLength;
            if (length > MaxBytes) return Fail("This file is bigger than the app will download. Use “Download in browser” instead.", status);
            if (length is { } needed && !HasSpace(folder, needed))
                return Fail($"There isn't enough free disk space for this file. It needs about {needed / 1024 / 1024:N0} MB.", status);

            var temp = Path.Combine(folder, file.FileName + TempSuffix);
            long total = 0;
            var writing = false;
            try
            {
                TryDelete(temp);
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    var sinceReport = Stopwatch.StartNew();
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        stall.CancelAfter(_stallTimeout);
                        writing = false;
                        var n = await input.ReadAsync(buffer, stall.Token);
                        if (n == 0) break;
                        // A zip starts with "PK". Anything else (such as a sign-in page) is stopped straight away.
                        if (total == 0 && (n < 2 || buffer[0] != (byte)'P' || buffer[1] != (byte)'K'))
                            return Fail(NotAZip, status);
                        total += n;
                        if (total > MaxBytes || total > length) return Fail("claude.ai sent more data than expected, so the file was not kept.", status, total);
                        writing = true;
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        if (sinceReport.ElapsedMilliseconds > 250)
                        {
                            onBytes?.Invoke((total, length));
                            sinceReport.Restart();
                        }
                    }
                }
                onBytes?.Invoke((total, length));
                if (total == 0) return Fail(NotAZip, status);
                if ((length is { } expected && total != expected) || !DownloadedFiles.OpensAsZip(temp))
                    return Fail("The download stopped before the file was complete. Try again.", status, total);

                var final = UniqueName(folder, file.FileName);
                File.Move(temp, final, overwrite: false);
                return new DownloadResult(file, final, null, total, status, clock.Elapsed);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail("The download stopped responding. Try again.", status, total);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                return Fail(writing || ex is UnauthorizedAccessException
                    ? "The file could not be saved. Check there is enough free disk space and that the folder can be saved to."
                    : "The connection to claude.ai was lost part-way. Try again.", status, total);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    private const string NoConnection = "The app could not reach claude.ai. Check the internet connection and try again.";

    private const string NotAZip =
        "claude.ai sent a web page instead of the zip file. This usually means the link needs you to be signed in, or it was already used. Try “Download in browser”.";

    /// <summary>Sends the request and follows redirects by hand, checking every address before it is contacted.</summary>
    private async Task<(HttpResponseMessage? Response, string? Problem, int? Status)> OpenAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/zip, application/octet-stream;q=0.9, */*;q=0.1");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308)) return (response, null, status);

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) return (null, "claude.ai sent an unexpected reply. Try “Download in browser”.", status);
            var next = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (!IsAllowedAddress(next))
                return (null, $"claude.ai sent the download on to {(next.IsAbsoluteUri ? next.Host : "another address")}, which this app doesn't download from. Try “Download in browser”.", status);
            if (next.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase))
                return (null, "claude.ai asked for a sign-in, which this app doesn't do. Try “Download in browser”.", status);
            if (hop >= MaxRedirects) return (null, "claude.ai redirected the download too many times. Try “Download in browser”.", status);
            url = next;
        }
    }

    /// <summary>A plain-words reason when the reply is not the file, or null when it looks like the file.</summary>
    internal static string? Refused(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.Headers.TryGetValues("cf-mitigated", out _))
            return "claude.ai asked for a browser check, which this app can't do. Try “Download in browser”.";
        if (status is 401 or 403)
            return "claude.ai didn't accept the link. It may need you to be signed in, or it was already used. Try “Download in browser”.";
        if (status is 404 or 410)
            return "claude.ai says this link no longer works. It may have been used already or expired. If “Download in browser” doesn't work either, ask for a new export.";
        if (status is 429 or >= 500)
            return $"claude.ai is busy or had a problem (code {status}). Try again in a few minutes.";
        if (status != 200)
            return $"claude.ai sent an unexpected reply (code {status}). Try “Download in browser”.";
        if (response.Content.Headers.ContentType?.MediaType is { } type && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase)))
            return NotAZip;
        return null;
    }

    internal static string UniqueName(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}).zip");
        return path;
    }

    private static bool HasSpace(string folder, long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is null || new DriveInfo(root).AvailableFreeSpace >= needed + 256L * 1024 * 1024;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Free space can't be read on this drive; the write itself fails cleanly if it runs out.
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
