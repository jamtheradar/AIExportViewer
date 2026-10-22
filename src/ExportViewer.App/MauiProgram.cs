using ExportViewer.App.Services;

namespace ExportViewer.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton(_ =>
        {
            var settings = new AppSettings(FileSystem.AppDataDirectory);
            LocalData.CleanUpWorkingFolders(settings);
            return settings;
        });
        builder.Services.AddSingleton<AppLog>();
        builder.Services.AddSingleton<ViewModeService>();
        builder.Services.AddSingleton<LibrarySession>();
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<LocalData>();
        builder.Services.AddSingleton<FolderPicker>();
        builder.Services.AddSingleton<FileSaver>();
        builder.Services.AddSingleton<ExternalLinks>();

        return builder.Build();
    }
}
