using ExportViewer.Download;

namespace ExportViewer.App.Services;

public enum UpdateStatus { NotInstalled, Idle, Checking, Downloading, UpToDate, Ready, Failed }

/// <summary>
/// Keeps the installed Windows app up to date. At start-up (unless switched off in Settings) it looks for a newer
/// version on GitHub, downloads it in the background and offers a restart. Only versions and timings are logged.
/// </summary>
public sealed class AppUpdates(AppSettings settings, AppLog log) : IDisposable
{
    private readonly AppUpdater _updater = new();
    private CancellationTokenSource? _cts;

    /// <summary>Raised when the status or download progress changes.</summary>
    public event Action? Changed;

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;

    /// <summary>The newer version being downloaded or ready to install.</summary>
    public string? NewVersion { get; private set; }

    public int Percent { get; private set; }

    /// <summary>Why the last check failed, in plain words.</summary>
    public string? Problem { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public bool IsInstalled => _updater.IsInstalled;

    /// <summary>True while the app is contacting GitHub for an update.</summary>
    public bool IsOnline => Status is UpdateStatus.Checking or UpdateStatus.Downloading;

    public string Version => _updater.CurrentVersion ?? AppInfo.Current.VersionString;

    /// <summary>Runs once when the app starts.</summary>
    public async Task StartAsync()
    {
        if (!IsInstalled)
        {
            Set(UpdateStatus.NotInstalled);
            return;
        }
        if (_updater.ReadyVersion is { } ready)
        {
            NewVersion = ready;
            Set(UpdateStatus.Ready);
            return;
        }
        if (settings.Data.CheckForUpdates) await CheckAsync();
    }

    /// <summary>Looks for a newer version and, when there is one, downloads it ready to install.</summary>
    public async Task CheckAsync()
    {
        if (!IsInstalled || IsOnline || Status == UpdateStatus.Ready) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Problem = null;
        Percent = 0;
        Set(UpdateStatus.Checking);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var found = await Task.Run(() => _updater.CheckAsync(ct), ct);
            LastChecked = DateTimeOffset.Now;
            if (!found.Ok) Fail(found.Problem!);
            else if (found.Version is null)
            {
                log.Write($"update check: up to date, version={Version} seconds={clock.Elapsed.TotalSeconds:F1}");
                Set(UpdateStatus.UpToDate);
            }
            else
            {
                NewVersion = found.Version;
                Set(UpdateStatus.Downloading);
                var done = await Task.Run(() => _updater.DownloadAsync(p => { Percent = p; Changed?.Invoke(); }, ct), ct);
                if (!done.Ok) Fail(done.Problem!);
                else
                {
                    log.Write($"update downloaded: version={done.Version} seconds={clock.Elapsed.TotalSeconds:F1}");
                    Set(UpdateStatus.Ready);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStatus.Idle);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Closes the open export (so its working files are tidied away), then restarts into the new version.</summary>
    public void RestartToUpdate(LibrarySession session)
    {
        if (Status != UpdateStatus.Ready) return;
        log.Write($"update: restarting to install version={NewVersion}");
        session.Close();
        _updater.RestartToUpdate();
    }

    private void Fail(string problem)
    {
        Problem = problem;
        log.Write("update check failed");
        Set(UpdateStatus.Failed);
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _updater.Dispose();
    }
}
