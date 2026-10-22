using ExportViewer.Core.Review;
using ExportViewer.Core.Sources;

namespace ExportViewer.App.Services;

public sealed record LocalDatabase(string FilePath, string? LibraryRoot, long Bytes, DateTime LastWrite);

/// <summary>Lists and deletes everything the app has stored on this computer.</summary>
public sealed class LocalData(AppSettings settings, LibrarySession session)
{
    public IReadOnlyList<LocalDatabase> Databases() => List(settings.LibrariesFolder, ReadRoot);

    /// <summary>Flags and notes, one file per library.</summary>
    public IReadOnlyList<LocalDatabase> Reviews() => List(settings.ReviewsFolder, ReviewStore.ReadLibraryLabel);

    /// <summary>Size of the unzipped working copies (normally only the open library's, if it came from zip files).</summary>
    public long UnzippedBytes() => FolderBytes(settings.UnzippedFolder);

    public long TotalBytes() => FolderBytes(settings.DataFolder);

    public void DeleteDatabase(string path)
    {
        if (session.Root is { } root && string.Equals(settings.DatabasePathFor(root), path, StringComparison.OrdinalIgnoreCase)) session.Close();
        DeleteDb(path);
    }

    public void DeleteReview(string path)
    {
        if (session.Root is { } root && string.Equals(settings.ReviewPathFor(root), path, StringComparison.OrdinalIgnoreCase)) session.Close();
        DeleteDb(path);
    }

    /// <summary>Closes the open library and removes every file the app has written, including settings and logs.</summary>
    public void DeleteAll()
    {
        session.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(settings.DataFolder))
        {
            foreach (var f in Directory.EnumerateFiles(settings.DataFolder, "*", SearchOption.AllDirectories)) File.Delete(f);
            foreach (var d in Directory.EnumerateDirectories(settings.DataFolder)) Directory.Delete(d, true);
        }
        settings.Update(d =>
        {
            d.Recent.Clear();
            d.Watchlists.Clear();
            d.LoggingEnabled = false;
        });
        // Update() rewrites settings.json with the defaults so the app keeps working; remove it too.
        File.Delete(Path.Combine(settings.DataFolder, "settings.json"));
    }

    /// <summary>Removes working files left behind if the app was closed unexpectedly. Called at start-up.</summary>
    public static void CleanUpWorkingFolders(AppSettings settings)
    {
        ZipExtractor.TryDeleteFolder(settings.UnzippedFolder);
        ZipExtractor.TryDeleteFolder(settings.ExportsTempFolder);
    }

    private static IReadOnlyList<LocalDatabase> List(string folder, Func<string, string?> label)
    {
        if (!Directory.Exists(folder)) return [];
        var list = new List<LocalDatabase>();
        foreach (var db in Directory.EnumerateFiles(folder, "*.db"))
        {
            var bytes = new[] { db, db + "-wal", db + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            list.Add(new LocalDatabase(db, label(db), bytes, File.GetLastWriteTime(db)));
        }
        return list.OrderByDescending(d => d.LastWrite).ToList();
    }

    private static long FolderBytes(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    private static void DeleteDb(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists)) File.Delete(f);
    }

    private static string? ReadRoot(string dbPath)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key IN ('source_label', 'root') ORDER BY key = 'source_label' DESC LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
