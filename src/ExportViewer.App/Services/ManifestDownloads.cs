using ExportViewer.Core.Sources;
using ExportViewer.Download;

namespace ExportViewer.App.Services;

/// <summary>
/// The manifest being downloaded and the folder being watched for its zip files. Kept for the session so leaving the
/// Download screen and coming back doesn't lose track, and a download carries on in the background.
/// Download links are never saved or logged.
/// </summary>
public sealed class ManifestDownloads(AppLog log) : IDisposable
{
    private readonly HashSet<string> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);
    private ExportDownloader? _downloader;
    private CancellationTokenSource? _cts;

    /// <summary>Raised while downloading (progress) and when a download finishes.</summary>
    public event Action? Changed;

    /// <summary>True while the app itself is downloading from claude.ai: the only time it is online.</summary>
    public bool IsDownloading => _cts is not null;

    public DownloadProgress? Progress { get; private set; }

    /// <summary>Why the app's own download of a file failed, by file name, from the last "Download all".</summary>
    public IReadOnlyDictionary<string, string> Problems => _problems;

    public string? ManifestPath { get; private set; }
    public ExportManifest? Manifest { get; private set; }
    public string? Folder { get; private set; }
    public IReadOnlyList<DownloadedFile> Found { get; private set; } = [];

    /// <summary>Zip files whose link was handed to the browser this session.</summary>
    public IReadOnlySet<string> Opened => _opened;

    public void Load(string path)
    {
        var manifest = ExportManifest.Read(path);
        Manifest = manifest;
        ManifestPath = Path.GetFullPath(path);
        Folder ??= Path.GetDirectoryName(ManifestPath);
        Found = [];
        _opened.Clear();
        _problems.Clear();
        log.Write($"manifest: files={manifest.Files.Count} offered={manifest.Files.Count(f => f.Url is not null)} expired={manifest.LinksExpired(DateTimeOffset.UtcNow)}");
    }

    public void SetFolder(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Found = [];
    }

    public void MarkOpened(string fileName)
    {
        _opened.Add(fileName);
        log.Write($"download link opened in browser: {fileName}");
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Manifest is not { } m || Folder is not { } folder) return;
        try
        {
            Found = await Task.Run(() => DownloadedFiles.Check(m, folder, ct), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went away or can't be listed; show everything as not downloaded.
            Found = [];
        }
    }

    /// <summary>Files the app can download now: offered by the manifest, not already downloaded or in the browser.</summary>
    public IReadOnlyList<ManifestFile> ToDownload() =>
        Manifest is not { } m ? [] : m.Files
            .Where(f => f.Url is not null)
            .Where(f => Found.FirstOrDefault(x => x.File == f)?.State is not (DownloadedState.Ready or DownloadedState.InProgress))
            .ToList();

    /// <summary>Downloads every file not yet downloaded, one at a time, straight into <see cref="Folder"/>.</summary>
    public async Task DownloadAllAsync()
    {
        if (IsDownloading || Manifest is not { } m || Folder is not { } folder) return;
        await CheckAsync();
        var files = ToDownload();
        if (files.Count == 0) return;
        _cts = new CancellationTokenSource();
        foreach (var f in files) _problems.Remove(f.FileName);
        Changed?.Invoke();
        var progress = new Progress<DownloadProgress>(p => { Progress = p; Changed?.Invoke(); });
        try
        {
            var downloader = _downloader ??= new ExportDownloader();
            var ct = _cts.Token;
            var results = await Task.Run(() => downloader.DownloadAsync(m, files, folder, progress, ct), ct);
            foreach (var r in results)
            {
                if (r.Problem is { } problem) _problems[r.File.FileName] = problem;
                log.Write($"download: file={r.File.FileName} ok={r.Ok} status={r.HttpStatus?.ToString() ?? "-"} bytes={r.Bytes} seconds={r.Elapsed.TotalSeconds:F1}");
            }
        }
        catch (OperationCanceledException)
        {
            log.Write("download cancelled");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Progress = null;
        }
        await CheckAsync();
        Changed?.Invoke();
    }

    public void CancelDownload() => _cts?.Cancel();

    /// <summary>The manifest and every zip that is ready, for opening as a library.</summary>
    public IReadOnlyList<string> ReadyFiles() =>
        Found.Where(f => f.State == DownloadedState.Ready && f.Path is not null).Select(f => f.Path!)
            .Concat(ManifestPath is { } p && File.Exists(p) ? [p] : [])
            .ToList();

    public void Dispose()
    {
        _cts?.Cancel();
        _downloader?.Dispose();
    }
}
