using System.Text.Json;

namespace ExportViewer.App.Services;

public enum ViewMode { Basic, Expert }

public sealed record RecentLibrary(string Path, DateTimeOffset LastOpened);

public sealed class SettingsData
{
    public ViewMode DefaultViewMode { get; set; } = ViewMode.Basic;
    public bool MemoryOnly { get; set; }
    public bool LoggingEnabled { get; set; }
    public List<RecentLibrary> Recent { get; set; } = [];
}

/// <summary>
/// Settings and local data locations. Everything the app writes lives under <see cref="DataFolder"/>, so Settings can
/// list it and "Delete all local data" can remove it.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string DataFolder { get; }
    public string LibrariesFolder => Path.Combine(DataFolder, "libraries");
    public string LogsFolder => Path.Combine(DataFolder, "logs");
    private string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public SettingsData Data { get; private set; }

    public AppSettings(string dataFolder)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(DataFolder);
        Data = Load();
    }

    private SettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsFile)) ?? new SettingsData();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged settings file is replaced with defaults rather than stopping the app.
        }
        return new SettingsData();
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Data, Json));
        }
    }

    public void Update(Action<SettingsData> change)
    {
        change(Data);
        Save();
    }

    public void TouchRecent(string path)
    {
        Update(d =>
        {
            d.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            d.Recent.Insert(0, new RecentLibrary(path, DateTimeOffset.Now));
            if (d.Recent.Count > 10) d.Recent.RemoveRange(10, d.Recent.Count - 10);
        });
    }

    /// <summary>Database file for a library folder. The name is a hash so the folder path itself is not exposed in file names.</summary>
    public string DatabasePathFor(string libraryRoot)
    {
        var key = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(LibrariesFolder, $"library-{hash}.db");
    }
}
