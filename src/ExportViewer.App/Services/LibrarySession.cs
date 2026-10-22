using ExportViewer.Core.Audit;
using ExportViewer.Core.Discovery;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Reports;
using ExportViewer.Core.Review;
using ExportViewer.Core.Search;
using ExportViewer.Core.Sources;
using ExportViewer.Core.Store;

namespace ExportViewer.App.Services;

public enum SessionState { Closed, Unzipping, Loading, NeedsRebuild, Ready, Failed }

/// <summary>The library that is currently open, and its load lifecycle.</summary>
public sealed class LibrarySession(AppSettings settings, AppLog log) : IDisposable
{
    private CancellationTokenSource? _cts;
    private LibraryDatabase? _db;
    private ReviewStore? _reviews;

    public SessionState State { get; private set; }
    public string? Root { get; private set; }

    /// <summary>Short name for the open export: the folder name, or the zip files' folder for zips.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>The zip files the library was unzipped from, or null when an unzipped folder was opened.</summary>
    public IReadOnlyList<string>? ZipSources { get; private set; }

    public IReadOnlyList<string> UnzipProblems { get; private set; } = [];
    public UnzipProgress? Unzip { get; private set; }
    public LoadProgress? Progress { get; private set; }
    public LoadSummary? LastSummary { get; private set; }
    public LibraryChanges? PendingChanges { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Bumped after every load so pages know to re-query.</summary>
    public int Version { get; private set; }

    public bool IsReady => State == SessionState.Ready && _db is not null;
    public LibraryQueries Queries => new(Db);
    public SearchService Search => new(Db);
    public AuditQueries Audit => new(Db);
    public AuditScanner Scanner => new(Db);

    /// <summary>The reviewer's flags and notes for the open library.</summary>
    public ReviewStore Reviews => _reviews ?? throw new InvalidOperationException("No library is open.");

    private LibraryDatabase Db => _db ?? throw new InvalidOperationException("No library is open.");

    public event Action? Changed;

    public async Task OpenAsync(string root)
    {
        Close();
        await OpenFolderAsync(root, null);
    }

    /// <summary>Unzips export zip files into the app's working folder, then opens that folder.</summary>
    public async Task OpenZipsAsync(IReadOnlyList<string> sources)
    {
        Close();
        Error = null;
        var files = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var target = settings.UnzipFolderFor(files);
        _cts = new CancellationTokenSource();
        Set(SessionState.Unzipping);
        var progress = new Progress<UnzipProgress>(p => { Unzip = p; Changed?.Invoke(); });
        try
        {
            var result = await Task.Run(() => ZipExtractor.Extract(files, target, progress, _cts.Token));
            UnzipProblems = result.Problems;
            log.Write($"unzip: zips={files.Count} files={result.Files} bytes={result.Bytes} problems={result.Problems.Count}");
        }
        catch (UnzipException ex)
        {
            ZipExtractor.TryDeleteFolder(target);
            Fail(ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            log.Write("unzip cancelled");
            Set(SessionState.Closed);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            ZipExtractor.TryDeleteFolder(target);
            Fail("The app is not allowed to read those zip files.");
            return;
        }
        catch (IOException)
        {
            // For example a zip that was moved or deleted after it was chosen.
            ZipExtractor.TryDeleteFolder(target);
            Fail("One of the zip files could not be found or read. Check they are still there and try again.");
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Unzip = null;
        }
        await OpenFolderAsync(target, files);
    }

    private async Task OpenFolderAsync(string root, IReadOnlyList<string>? zips)
    {
        Root = Path.GetFullPath(root);
        ZipSources = zips;
        DisplayName = zips is null ? Path.GetFileName(Root.TrimEnd('\\', '/')) : ZipDisplayName(zips);
        Error = null;
        try
        {
            if (!Directory.Exists(Root)) throw new DirectoryNotFoundException();
            _db = settings.Data.MemoryOnly ? LibraryDatabase.OpenInMemory() : LibraryDatabase.OpenFile(settings.DatabasePathFor(Root));
            _reviews = settings.Data.MemoryOnly ? ReviewStore.OpenInMemory() : ReviewStore.OpenFile(settings.ReviewPathFor(Root));
            var label = zips is null ? Root : string.Join("; ", zips);
            _reviews.SetLibraryLabel(label);
            _db.SetMeta("source_label", label);
            settings.TouchRecent(zips is null ? Root : Path.GetDirectoryName(zips[0]) ?? zips[0], zips);
            var loader = new LibraryLoader(_db);
            var changes = await Task.Run(() => loader.Compare(LibraryScanner.Scan(Root)));
            log.Write($"open: new={changes.New.Count} unchanged={changes.Unchanged.Count} changed={changes.ChangedOrRemoved.Count} memoryOnly={settings.Data.MemoryOnly} zips={zips?.Count ?? 0}");
            if (changes.NeedsRebuild)
            {
                PendingChanges = changes;
                Set(SessionState.NeedsRebuild);
                return;
            }
            if (changes.IsUpToDate && !loader.NeedsReanalysis)
            {
                Version++;
                Set(SessionState.Ready);
                return;
            }
            await LoadAsync(rebuild: false);
        }
        catch (DirectoryNotFoundException)
        {
            Fail("That folder could not be found. It may have been moved, renamed or disconnected.");
        }
        catch (UnauthorizedAccessException)
        {
            Fail("The app is not allowed to read that folder.");
        }
    }

    private static string ZipDisplayName(IReadOnlyList<string> zips)
    {
        var folders = zips.Select(z => Path.GetFileName(Path.GetDirectoryName(z) ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var count = zips.Count(z => z.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return folders.Count == 1 && folders[0].Length > 0 ? $"{folders[0]} ({count} zip files)" : $"{count} zip files";
    }

    /// <summary>Loads new files, or rebuilds everything when the user agreed after files changed.</summary>
    public async Task LoadAsync(bool rebuild)
    {
        if (_db is null || Root is null) return;
        _cts = new CancellationTokenSource();
        PendingChanges = null;
        Set(SessionState.Loading);
        var progress = new Progress<LoadProgress>(p => { Progress = p; Changed?.Invoke(); });
        try
        {
            LastSummary = await new LibraryLoader(_db).LoadAsync(Root, rebuild, progress, _cts.Token);
            log.Write($"loaded: files={LastSummary.Files.Count} records={LastSummary.Files.Sum(f => f.Records)} skippedFiles={LastSummary.SkippedFiles.Count} seconds={LastSummary.Elapsed.TotalSeconds:F1}");
            Version++;
            Set(SessionState.Ready);
        }
        catch (OperationCanceledException)
        {
            log.Write("load cancelled");
            Version++;
            // Files finished before the cancel stay loaded; the rest load next time.
            Set(SessionState.Ready);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Write($"load failed: {ex.GetType().Name}");
            Fail("The export could not be loaded. Check the folder is still available and try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Progress = null;
        }
    }

    /// <summary>Skip the rebuild and open what was loaded before.</summary>
    public void UseExisting()
    {
        PendingChanges = null;
        Version++;
        Set(SessionState.Ready);
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Header details for reports: which export, the dates it covers, and now.</summary>
    public ReportContext ReportContext()
    {
        var o = Queries.GetOverview();
        return new ReportContext(DisplayName ?? "Export", o.FirstActivity, o.LastActivity, TimeZoneInfo.Local, DateTimeOffset.Now);
    }

    /// <summary>Closes the library. A library opened from zip files has its unzipped working copy removed.</summary>
    public void Close()
    {
        _cts?.Cancel();
        _db?.Dispose();
        _db = null;
        _reviews?.Dispose();
        _reviews = null;
        if (ZipSources is not null && Root is not null) ZipExtractor.TryDeleteFolder(Root);
        Root = null;
        DisplayName = null;
        ZipSources = null;
        UnzipProblems = [];
        LastSummary = null;
        PendingChanges = null;
        Set(SessionState.Closed);
    }

    private void Fail(string message)
    {
        Error = message;
        _db?.Dispose();
        _db = null;
        _reviews?.Dispose();
        _reviews = null;
        if (ZipSources is not null && Root is not null) ZipExtractor.TryDeleteFolder(Root);
        Set(SessionState.Failed);
    }

    private void Set(SessionState s)
    {
        State = s;
        Changed?.Invoke();
    }

    public void Dispose() => Close();
}
