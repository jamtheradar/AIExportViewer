using System.Net;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ExportViewer.Download;

/// <param name="Version">The newer version found or downloaded, or null when there is none.</param>
/// <param name="Problem">Why it couldn't check or download, in plain words that can be shown to the user as is.</param>
public sealed record UpdateResult(string? Version, string? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Updates the installed Windows app (Velopack) from the project's GitHub releases. Apart from export downloads, this
/// is the app's only network use. It sends nothing about the user or their exports: each request is a plain download
/// of a public release file, through <see cref="UpdateDownloader"/>.
/// <para>A copy that wasn't installed with the installer (a developer build, the Mac app) never checks.</para>
/// </summary>
public sealed class AppUpdater : IDisposable
{
    /// <summary>GitHub sends this on to the newest release's files. Changing the repository needs the owner's approval.</summary>
    public const string ReleasesUrl = "https://github.com/jamtheradar/AIExportViewer/releases/latest/download/";

    /// <summary>
    /// Hosts updates come from, the first request and every redirect: GitHub, then the host it keeps release files on
    /// (the older name is kept in case GitHub sends a download there). Adding a host needs the owner's approval.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedHosts = ["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    private readonly UpdateDownloader _downloader;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _found;

    public AppUpdater() : this(new UpdateDownloader())
    {
    }

    internal AppUpdater(UpdateDownloader downloader)
    {
        _downloader = downloader;
        // Without the start-up work (a Mac or test run) there is nothing installed to update.
        if (VelopackLocator.IsCurrentSet) _manager = new UpdateManager(new SimpleWebSource(ReleasesUrl, downloader));
    }

    /// <summary>
    /// Velopack's start-up work: finishes an install, update or uninstall (and exits when run for one), and installs
    /// an update that was downloaded last time. Must be the first thing the Windows app does.
    /// </summary>
    public static void RunStartupHooks() => VelopackApp.Build().Run();

    /// <summary>True when this copy was installed with the installer, so it can update itself.</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>The installed version, or null when this copy wasn't installed with the installer.</summary>
    public string? CurrentVersion => IsInstalled ? _manager!.CurrentVersion?.ToString() : null;

    /// <summary>A newer version that has been downloaded and is installed by <see cref="RestartToUpdate"/> or at the next start.</summary>
    public string? ReadyVersion => IsInstalled ? _manager!.UpdatePendingRestart?.Version.ToString() : null;

    /// <summary>Looks for a newer version. Nothing is downloaded yet.</summary>
    public async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return new UpdateResult(null, null);
        try
        {
            _found = await _manager!.CheckForUpdatesAsync().WaitAsync(ct);
            return new UpdateResult(_found?.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new UpdateResult(null, Explain(ex));
        }
    }

    /// <summary>Downloads the version found by <see cref="CheckAsync"/>. <paramref name="progress"/> gets 0 to 100.</summary>
    public async Task<UpdateResult> DownloadAsync(Action<int>? progress = null, CancellationToken ct = default)
    {
        if (_found is not { } found || _manager is null) return new UpdateResult(null, null);
        try
        {
            await _manager.DownloadUpdatesAsync(found, progress, ct);
            return new UpdateResult(found.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new UpdateResult(null, Explain(ex));
        }
    }

    /// <summary>Closes the app straight away, installs the downloaded version and starts it again.</summary>
    public void RestartToUpdate()
    {
        if (IsInstalled && _manager!.UpdatePendingRestart is { } ready) _manager.ApplyUpdatesAndRestart(ready);
    }

    internal static string Explain(Exception ex) => ex switch
    {
        BlockedAddressException b => $"The update was sent on to {b.Host}, which this app doesn't download from, so it was stopped.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "No updates have been published yet.",
        HttpRequestException { StatusCode: { } code } => $"GitHub, where updates are published, had a problem (code {(int)code}). The app will try again next time it starts.",
        HttpRequestException or HttpIOException or OperationCanceledException => "The app could not reach GitHub, where updates are published. Check the internet connection.",
        IOException or UnauthorizedAccessException => "The update could not be saved. Check there is enough free disk space.",
        _ => "The update could not be checked or downloaded. The app will try again next time it starts.",
    };

    public void Dispose() => _downloader.Dispose();
}
