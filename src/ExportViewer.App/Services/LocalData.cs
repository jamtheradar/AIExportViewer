using ExportViewer.Core.Store;

namespace ExportViewer.App.Services;

public sealed record LocalDatabase(string FilePath, string? LibraryRoot, long Bytes, DateTime LastWrite);

/// <summary>Lists and deletes everything the app has stored on this computer.</summary>
public sealed class LocalData(AppSettings settings, LibrarySession session)
{
    public IReadOnlyList<LocalDatabase> Databases()
    {
        if (!Directory.Exists(settings.LibrariesFolder)) return [];
        var list = new List<LocalDatabase>();
        foreach (var db in Directory.EnumerateFiles(settings.LibrariesFolder, "*.db"))
        {
            var bytes = new[] { db, db + "-wal", db + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            list.Add(new LocalDatabase(db, ReadRoot(db), bytes, File.GetLastWriteTime(db)));
        }
        return list.OrderByDescending(d => d.LastWrite).ToList();
    }

    public long TotalBytes() =>
        Directory.Exists(settings.DataFolder)
            ? new DirectoryInfo(settings.DataFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    public void DeleteDatabase(string path)
    {
        if (session.Root is { } root && string.Equals(settings.DatabasePathFor(root), path, StringComparison.OrdinalIgnoreCase)) session.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists)) File.Delete(f);
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
            d.LoggingEnabled = false;
        });
        // Update() rewrites settings.json with the defaults so the app keeps working; remove it too.
        File.Delete(Path.Combine(settings.DataFolder, "settings.json"));
    }

    private static string? ReadRoot(string dbPath)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='root'";
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
