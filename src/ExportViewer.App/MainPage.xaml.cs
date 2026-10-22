using Microsoft.AspNetCore.Components.WebView;

namespace ExportViewer.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>Privacy rule: the web view never leaves the app. External links go through the confirm dialog instead.</summary>
    private void OnUrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        if (e.UrlLoadingStrategy != UrlLoadingStrategy.OpenInWebView) e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        var core = e.WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
        // Second layer behind the page CSP: refuse any request that is not for the app's own content.
        core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAppUri(args.Request.Uri))
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
#endif
    }

    internal static bool IsAppUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
            ? (u.Host == "0.0.0.1" && u.Scheme is "https" or "app") || u.Scheme is "data" or "blob" or "about"
            : false;
}
