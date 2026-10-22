using ExportViewer.Core.Discovery;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Search;
using ExportViewer.Core.Store;

namespace ExportViewer.App.Services;

public enum SessionState { Closed, Loading, NeedsRebuild, Ready, Failed }

/// <summary>The library that is currently open, and its load lifecycle.</summary>
public sealed class LibrarySession(AppSettings settings, AppLog log) : IDisposable
{
    private CancellationTokenSource? _cts;
    private LibraryDatabase? _db;

    public SessionState State { get; private set; }
    public string? Root { get; private set; }
    public LoadProgress? Progress { get; private set; }
    public LoadSummary? LastSummary { get; private set; }
    public LibraryChanges? PendingChanges { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Bumped after every load so pages know to re-query.</summary>
    public int Version { get; private set; }

    public bool IsReady => State == SessionState.Ready && _db is not null;
    public LibraryQueries Queries => new(_db ?? throw new InvalidOperationException("No library is open."));
    public SearchService Search => new(_db ?? throw new InvalidOperationException("No library is open."));

    public event Action? Changed;

    public async Task OpenAsync(string root)
    {
        Close();
        Root = Path.GetFullPath(root);
        Error = null;
        try
        {
            if (!Directory.Exists(Root)) throw new DirectoryNotFoundException();
            _db = settings.Data.MemoryOnly ? LibraryDatabase.OpenInMemory() : LibraryDatabase.OpenFile(settings.DatabasePathFor(Root));
            settings.TouchRecent(Root);
            var loader = new LibraryLoader(_db);
            var changes = await Task.Run(() => loader.Compare(LibraryScanner.Scan(Root)));
            log.Write($"open: new={changes.New.Count} unchanged={changes.Unchanged.Count} changed={changes.ChangedOrRemoved.Count} memoryOnly={settings.Data.MemoryOnly}");
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

    public void Close()
    {
        _cts?.Cancel();
        _db?.Dispose();
        _db = null;
        Root = null;
        LastSummary = null;
        PendingChanges = null;
        Set(SessionState.Closed);
    }

    private void Fail(string message)
    {
        Error = message;
        _db?.Dispose();
        _db = null;
        Set(SessionState.Failed);
    }

    private void Set(SessionState s)
    {
        State = s;
        Changed?.Invoke();
    }

    public void Dispose() => Close();
}
