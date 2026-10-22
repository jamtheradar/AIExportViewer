using ExportViewer.Core.Rendering;
using Microsoft.JSInterop;

namespace ExportViewer.App.Services;

/// <summary>
/// Links in export content never navigate. A click asks for confirmation showing the full address; only then is it
/// handed to the computer's normal browser. The app itself makes no request.
/// </summary>
public sealed class ExternalLinks
{
    public string? PendingUrl { get; private set; }

    public event Action? Changed;

    [JSInvokable]
    public void Request(string url)
    {
        if (!SafeHtmlRenderer.IsAllowedUrl(url)) return;
        PendingUrl = url;
        Changed?.Invoke();
    }

    public async Task ConfirmAsync()
    {
        var url = PendingUrl;
        Dismiss();
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && SafeHtmlRenderer.IsAllowedUrl(url))
            await Launcher.Default.OpenAsync(uri);
    }

    public void Dismiss()
    {
        PendingUrl = null;
        Changed?.Invoke();
    }
}
