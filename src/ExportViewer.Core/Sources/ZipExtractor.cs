using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ExportViewer.Core.Sources;

public sealed record UnzipProgress(string? CurrentZip, int ZipsDone, int ZipsTotal, long BytesDone, long BytesTotal);

public sealed record UnzipResult(string Folder, int Files, long Bytes, IReadOnlyList<string> Problems);

/// <summary>A problem with a message that can be shown to the user as is.</summary>
public sealed class UnzipException(string message) : Exception(message);

/// <summary>
/// Unzips export zip files into a private working folder so they can be opened like an unzipped export. The zip
/// files are only read. Each zip goes in its own sub-folder named after it (as unzipping by hand would do), and every
/// extracted file gets the zip's own modified time, so unzipping the same zips again looks unchanged to the library.
/// </summary>
public static partial class ZipExtractor
{
    /// <summary>Refuse anything that would unpack to more than this, to protect the disk from damaged or hostile zips.</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024 * 1024;

    /// <summary>A file this many times bigger than its compressed size is treated as a zip bomb and skipped.</summary>
    public const int MaxRatio = 2_000;

    /// <summary>Export part zips are named like <c>conversations-000.zip</c>.</summary>
    public static bool LooksLikeExportZip(string path) => ExportZipName().IsMatch(Path.GetFileName(path));

    /// <summary>Export zips in a folder (and its sub-folders), plus JSON files next to them such as the manifest.</summary>
    public static IReadOnlyList<string> FindExportFiles(string folder)
    {
        var zips = Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories).Where(LooksLikeExportZip).ToList();
        var json = zips.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(d => Directory.EnumerateFiles(d!, "*.json", SearchOption.TopDirectoryOnly));
        return zips.Concat(json).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="sources">Zip files, and optionally loose JSON files (such as the manifest) which are copied as they are.</param>
    /// <param name="target">Working folder. Anything already in it is removed first.</param>
    public static UnzipResult Extract(IReadOnlyList<string> sources, string target, IProgress<UnzipProgress>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var zips = new List<(string Path, ZipArchive Archive, FileStream Stream)>();
        try
        {
            long declared = 0;
            foreach (var path in sources.Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                    zips.Add((path, zip, fs));
                    declared += zip.Entries.Sum(e => e.Length);
                }
                catch (InvalidDataException)
                {
                    fs?.Dispose();
                    problems.Add($"{Path.GetFileName(path)} is not a zip file or is damaged, so it was skipped.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    fs?.Dispose();
                    problems.Add($"{Path.GetFileName(path)} could not be read, so it was skipped.");
                }
            }
            var loose = sources.Where(p => !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
            declared += loose.Sum(p => new FileInfo(p).Length);

            if (declared > MaxTotalBytes)
                throw new UnzipException("These zip files would unpack to more than 200 GB, which is more than this app will unzip. Unzip them by hand and open the folder instead.");
            EnsureSpace(target, declared);

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            var targetFull = Path.GetFullPath(target);

            int files = 0, done = 0;
            long bytes = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, zip, _) in zips)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new UnzipProgress(Path.GetFileName(path), done, zips.Count, bytes, declared));
                var folder = Unique(used, Path.GetFileNameWithoutExtension(path));
                var root = Path.Combine(targetFull, folder);
                var stamp = File.GetLastWriteTimeUtc(path);
                var unsafePaths = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    var dest = SafeDestination(root, entry.FullName);
                    if (dest is null) { unsafePaths++; continue; }
                    if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio && entry.Length > 100L * 1024 * 1024)
                    {
                        problems.Add($"{entry.FullName} in {Path.GetFileName(path)} looks damaged (it would unpack to an unusual size), so it was skipped.");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    try
                    {
                        bytes += CopyEntry(entry, dest, ct, n => progress?.Report(new UnzipProgress(Path.GetFileName(path), done, zips.Count, bytes + n, declared)));
                        File.SetLastWriteTimeUtc(dest, stamp);
                        files++;
                    }
                    catch (InvalidDataException)
                    {
                        TryDelete(dest);
                        problems.Add($"{entry.FullName} in {Path.GetFileName(path)} is damaged, so it was skipped.");
                    }
                }
                if (unsafePaths > 0)
                    problems.Add($"{unsafePaths} item(s) in {Path.GetFileName(path)} pointed outside the zip and were skipped.");
                done++;
            }

            foreach (var path in loose)
            {
                var dest = Path.Combine(targetFull, Unique(used, Path.GetFileName(path)));
                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                    src.CopyTo(dst);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(path));
                bytes += new FileInfo(dest).Length;
                files++;
            }

            progress?.Report(new UnzipProgress(null, zips.Count, zips.Count, bytes, declared));
            if (zips.Count == 0 && loose.Count == 0) throw new UnzipException(problems.FirstOrDefault() ?? "No zip files were chosen.");
            return new UnzipResult(targetFull, files, bytes, problems);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFolder(target);
            throw;
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            TryDeleteFolder(target);
            throw new UnzipException("The zip files could not be unzipped. Check there is enough free disk space and try again.");
        }
        finally
        {
            foreach (var (_, zip, fs) in zips)
            {
                zip.Dispose();
                fs.Dispose();
            }
        }
    }

    /// <summary>Resolves an entry name under <paramref name="root"/>, or null when it would land outside it ("zip slip").</summary>
    internal static string? SafeDestination(string root, string entryName)
    {
        var name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == "..")) return null;
        var full = Path.GetFullPath(Path.Combine(root, name));
        var rootWithSep = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static long CopyEntry(ZipArchiveEntry entry, string dest, CancellationToken ct, Action<long> onBytes)
    {
        using var input = entry.Open();
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        long total = 0;
        long lastReport = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += n;
            // Never write more than the zip said the file holds.
            if (total > entry.Length) throw new InvalidDataException();
            output.Write(buffer, 0, n);
            if (total - lastReport > 8 << 20) { onBytes(total); lastReport = total; }
        }
        return total;
    }

    private static void EnsureSpace(string target, long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (root is null) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + 512L * 1024 * 1024)
                throw new UnzipException($"There isn't enough free disk space to unzip these files. They need about {needed / 1024 / 1024:N0} MB.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Free space can't be read on this drive; the copy itself will fail cleanly if it runs out.
        }
    }

    private static string Unique(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^[a-z0-9_]+-\d{3,}\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportZipName();
}
