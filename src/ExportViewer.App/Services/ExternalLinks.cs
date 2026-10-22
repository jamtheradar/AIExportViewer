using ExportViewer.Core.Rendering;
using ExportViewer.Core.Sources;
using Microsoft.JSInterop;

namespace ExportViewer.App.Services;

/// <summary>
/// Links in export content never navigate. A click asks for confirmation showing the full address; only then is it
/// handed to the computer's normal browser. The app itself makes no request.
/// </summary>
public sealed class ExternalLinks
{
    public string? PendingUrl { get; private set; }

    /// <summary>Set when the pending link is an export download link from a manifest: the zip it downloads.</summary>
    public string? PendingDownload { get; private set; }

    public event Action? Changed;

    [JSInvokable]
    public void Request(string url)
    {
        if (!SafeHtmlRenderer.IsAllowedUrl(url)) return;
        PendingUrl = url;
        PendingDownload = null;
        Changed?.Invoke();
    }

    /// <summary>A manifest download link. The browser does the download (and any sign-in); the app still makes no request.</summary>
    public void RequestDownload(ManifestFile file)
    {
        if (!ExportManifest.IsTrustedDownloadUrl(file.Url)) return;
        PendingUrl = file.Url!.AbsoluteUri;
        PendingDownload = file.FileName;
        Changed?.Invoke();
    }

    /// <summary>Raised after a download link was handed to the browser, with the zip's file name.</summary>
    public event Action<string>? DownloadOpened;

    public async Task ConfirmAsync()
    {
        var url = PendingUrl;
        var download = PendingDownload;
        Dismiss();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !SafeHtmlRenderer.IsAllowedUrl(url)) return;
        if (download is not null && !ExportManifest.IsTrustedDownloadUrl(uri)) return;
        if (await Launcher.Default.OpenAsync(uri) && download is not null) DownloadOpened?.Invoke(download);
    }

    public void Dismiss()
    {
        PendingUrl = null;
        PendingDownload = null;
        Changed?.Invoke();
    }
}
