using ExportViewer.Core.Audit;

namespace ExportViewer.App.Services;

/// <summary>
/// Runs the sensitive data and watchlist check in the background so the reviewer can keep browsing, and keeps the
/// watchlists (saved in settings, on this computer only).
/// </summary>
public sealed class AuditService : IDisposable
{
    private readonly LibrarySession _session;
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private CancellationTokenSource? _cts;
    private int _sessionVersion = -1;

    public AuditService(LibrarySession session, AppSettings settings, AppLog log)
    {
        _session = session;
        _settings = settings;
        _log = log;
        _session.Changed += OnSessionChanged;
    }

    public bool Running => _cts is not null;
    public ScanProgress? Progress { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Bumped when a check finishes or watchlists change, so pages know to re-query.</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<Watchlist> Watchlists => _settings.Data.Watchlists;

    public string WatchlistFingerprint => AuditScanner.Fingerprint(Watchlists);

    public async Task RunAsync()
    {
        if (Running || !_session.IsReady) return;
        _cts = new CancellationTokenSource();
        _sessionVersion = _session.Version;
        Error = null;
        Progress = new ScanProgress(0, 0, 0);
        Raise();
        var scanner = _session.Scanner;
        var lists = Watchlists.ToList();
        var progress = new Progress<ScanProgress>(p => { Progress = p; Changed?.Invoke(); });
        try
        {
            var token = _cts.Token;
            var s = await Task.Run(() => scanner.Run(lists, progress, token), token);
            _log.Write($"check: items={s.Items} findings={s.Findings} watchlistHits={s.WatchlistHits} seconds={s.Elapsed.TotalSeconds:F1}");
        }
        catch (OperationCanceledException)
        {
            _log.Write("check cancelled");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _log.Write($"check failed: {ex.GetType().Name}");
            Error = "The check could not finish. Try again once the export has finished opening.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Progress = null;
            Raise();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Save(Watchlist list)
    {
        _settings.Update(d =>
        {
            var i = d.Watchlists.FindIndex(w => w.Id == list.Id);
            if (i >= 0) d.Watchlists[i] = list;
            else d.Watchlists.Add(list);
        });
        Raise();
    }

    public void Delete(string id)
    {
        _settings.Update(d => d.Watchlists.RemoveAll(w => w.Id == id));
        Raise();
    }

    private void Raise()
    {
        Version++;
        Changed?.Invoke();
    }

    /// <summary>A check belongs to one library: stop it if that library closes or reloads.</summary>
    private void OnSessionChanged()
    {
        if (_session.Version == _sessionVersion && _session.IsReady) return;
        _sessionVersion = _session.Version;
        Cancel();
    }

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        Cancel();
    }
}
