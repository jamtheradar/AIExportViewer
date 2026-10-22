namespace ExportViewer.App;

public partial class App : Application
{
    public App(Services.AppSettings settings)
    {
        InitializeComponent();
        ApplyTheme(settings.Data.Theme);
    }

    /// <summary>Matches the window frame to the page colours. Light by default whatever the computer is set to.</summary>
    public static void ApplyTheme(Services.ColourTheme theme)
    {
        if (Current is { } app)
            app.UserAppTheme = theme == Services.ColourTheme.Dark ? AppTheme.Dark : AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Page page = WebViewAvailable() ? new MainPage() : new WebViewMissingPage();
        var window = new Window(page) { Title = "AI Export Viewer", MinimumWidth = 960, MinimumHeight = 640 };
        // Closing the library on exit removes the unzipped working copy of any zip files that were open.
        window.Destroying += (_, _) => IPlatformApplication.Current?.Services.GetService<Services.LibrarySession>()?.Close();
        return window;
    }

    private static bool WebViewAvailable()
    {
#if WINDOWS
        try
        {
            return !string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
#else
        return true;
#endif
    }
}
