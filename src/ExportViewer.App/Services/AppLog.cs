namespace ExportViewer.App.Services;

/// <summary>
/// Optional diagnostic log, off by default. Privacy rule: only file names, counts and timings are ever written —
/// never message text, titles, names or emails. Callers must pass only such values.
/// </summary>
public sealed class AppLog(AppSettings settings)
{
    private readonly object _gate = new();

    public string LogFile => Path.Combine(settings.LogsFolder, "app.log");

    public void Write(string message)
    {
        if (!settings.Data.LoggingEnabled) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(settings.LogsFolder);
                File.AppendAllText(LogFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}
