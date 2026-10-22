using System.Text.Json;
using ExportViewer.Core.Audit;

namespace ExportViewer.App.Services;

public enum ViewMode { Basic, Expert }

/// <param name="Path">The library folder, or for zip files the folder they are in.</param>
/// <param name="Zips">The zip (and manifest) files when the library was opened from zips; null for an unzipped folder.</param>
public sealed record RecentLibrary(string Path, DateTimeOffset LastOpened, List<string>? Zips = null);

public sealed class SettingsData
{
    public ViewMode DefaultViewMode { get; set; } = ViewMode.Basic;
    public bool MemoryOnly { get; set; }
    public bool LoggingEnabled { get; set; }
    public List<RecentLibrary> Recent { get; set; } = [];
    public List<Watchlist> Watchlists { get; set; } = [];
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
    public string ReviewsFolder => Path.Combine(DataFolder, "reviews");

    /// <summary>Working copies of zip files while they are open. Removed on close and at the next start.</summary>
    public string UnzippedFolder => Path.Combine(DataFolder, "unzipped");

    /// <summary>Reports waiting to be handed to the Mac "save" dialog. Removed straight after.</summary>
    public string ExportsTempFolder => Path.Combine(DataFolder, "exports-temp");
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

    public void TouchRecent(string path, IReadOnlyList<string>? zips = null)
    {
        Update(d =>
        {
            d.Recent.RemoveAll(r => zips is null
                ? r.Zips is null && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)
                : r.Zips is not null && r.Zips.SequenceEqual(zips, StringComparer.OrdinalIgnoreCase));
            d.Recent.Insert(0, new RecentLibrary(path, DateTimeOffset.Now, zips?.ToList()));
            if (d.Recent.Count > 10) d.Recent.RemoveRange(10, d.Recent.Count - 10);
        });
    }

    /// <summary>Database file for a library folder. The name is a hash so the folder path itself is not exposed in file names.</summary>
    public string DatabasePathFor(string libraryRoot) => Path.Combine(LibrariesFolder, $"library-{KeyFor(libraryRoot)}.db");

    /// <summary>The reviewer's flags and notes for a library, kept apart from the library copy so a rebuild never loses them.</summary>
    public string ReviewPathFor(string libraryRoot) => Path.Combine(ReviewsFolder, $"review-{KeyFor(libraryRoot)}.db");

    /// <summary>Working folder for a set of zip files. The same zips always get the same folder, so reopening them is quick.</summary>
    public string UnzipFolderFor(IEnumerable<string> sources) =>
        Path.Combine(UnzippedFolder, Hash(string.Join('\n', sources.Select(s => Path.GetFullPath(s).ToLowerInvariant()).Order(StringComparer.Ordinal))));

    private static string KeyFor(string libraryRoot) =>
        Hash(Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant());

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
