using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExportViewer.Core.Json;

namespace ExportViewer.Core.Sources;

/// <summary>A problem with a message that can be shown to the user as is.</summary>
public sealed class ManifestException(string message) : Exception(message);

/// <summary>One zip file listed in the manifest.</summary>
/// <param name="FileName">A safe file name for the zip. Never a path.</param>
/// <param name="Url">The download link, or null when it is missing or not a trusted Claude link.</param>
/// <param name="Problem">Why this file can't be downloaded, in plain words; null when it can.</param>
public sealed record ManifestFile(int Index, string Category, int? Part, string FileName, Uri? Url, string? Problem);

/// <summary>
/// The manifest that comes with an export: when it was made and a download link per zip file (see export-schema.md
/// section 2). The manifest is untrusted input: links are only accepted for <see cref="TrustedHost"/> over HTTPS,
/// and file names are reduced to the export's own naming pattern so they can never point at another folder.
/// </summary>
public sealed partial record ExportManifest(DateTimeOffset? CreatedAt, string? Version, int? TotalFiles, IReadOnlyList<ManifestFile> Files)
{
    public const string TrustedHost = "claude.ai";

    /// <summary>The manifest's instructions say each link expires 24 hours after it is created.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    /// <summary>A manifest is a few KB; anything far bigger is not one.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    public DateTimeOffset? LinksExpireAt => CreatedAt + LinkLifetime;

    public bool LinksExpired(DateTimeOffset now) => LinksExpireAt is { } e && now >= e;

    /// <summary>Only HTTPS links to claude.ai's export path, with no user name or unusual port.</summary>
    public static bool IsTrustedDownloadUrl(Uri? url) =>
        url is { IsAbsoluteUri: true }
        && url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.IdnHost, TrustedHost, StringComparison.OrdinalIgnoreCase)
        && url.IsDefaultPort
        && url.UserInfo.Length == 0
        && url.AbsolutePath.StartsWith("/export/", StringComparison.Ordinal);

    public static ExportManifest Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > MaxBytes) throw new ManifestException("That file is too big to be an export manifest.");
            using var doc = JsonDocument.Parse(fs);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new ManifestException("That file is not an export manifest. It could not be read as JSON.");
        }
        catch (FileNotFoundException)
        {
            throw new ManifestException("That file could not be found. It may have been moved or deleted.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ManifestException("The app is not allowed to read that file.");
        }
        catch (IOException)
        {
            throw new ManifestException("That file could not be read.");
        }
    }

    public static ExportManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.HasArray("data_files"))
            throw new ManifestException("That file is not an export manifest. It has no list of files to download.");

        DateTimeOffset? created = DateTimeOffset.TryParse(root.Str("created_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var c) ? c : null;
        var files = new List<ManifestFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var f in root.Arr("data_files"))
        {
            var category = f.Str("category") ?? "";
            var part = f.Long("part") is { } p and >= 0 and < 100_000 ? (int)p : (int?)null;
            var name = SafeFileName(f.Str("filename"), category, part);
            Uri.TryCreate(f.Str("export_url"), UriKind.Absolute, out var url);
            string? problem = null;
            if (name is null) problem = "The manifest gives this file an unusual name, so it was left out.";
            else if (!names.Add(name)) problem = "The manifest lists this file twice.";
            else if (url is null) problem = "The manifest has no download link for this file.";
            else if (!IsTrustedDownloadUrl(url)) problem = $"The download link does not go to {TrustedHost}, so it is not offered.";
            var index = f.Long("batch_index") is { } b and >= 0 and < int.MaxValue ? (int)b : i;
            files.Add(new ManifestFile(index, category, part, name ?? $"file {i + 1}", problem is null ? url : null, problem));
            i++;
        }
        var total = root.Long("total_files") is { } t and >= 0 and < int.MaxValue ? (int)t : (int?)null;
        return new ExportManifest(created, root.Str("version"), total, files.OrderBy(f => f.Index).ToList());
    }

    /// <summary>True for names like <c>conversations-000.zip</c>: no folders, no unusual characters.</summary>
    public static bool IsSafeFileName(string name) => ExportZipName().IsMatch(name);

    /// <summary>The manifest's file name if it matches the export pattern, else one built from category and part.</summary>
    internal static string? SafeFileName(string? given, string category, int? part)
    {
        if (given is not null && ExportZipName().IsMatch(given)) return given;
        if (part is { } p && CategoryName().IsMatch(category)) return $"{category}-{p:D3}.zip";
        return null;
    }

    [GeneratedRegex(@"^[a-z0-9_]{1,64}-\d{3,5}\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ExportZipName();

    [GeneratedRegex(@"^[a-z0-9_]{1,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryName();
}

public enum DownloadedState { Missing, InProgress, Ready, Damaged }

/// <param name="Path">The zip found in the folder, or null when there is none.</param>
public sealed record DownloadedFile(ManifestFile File, DownloadedState State, string? Path);

/// <summary>
/// Looks for a manifest's zip files in a folder, such as the browser's downloads folder, and checks each one opens
/// as a zip. Files are only read. A browser may rename a repeat download to <c>name (1).zip</c>, so those count too;
/// the newest copy that opens wins.
/// </summary>
public static partial class DownloadedFiles
{
    public static IReadOnlyList<DownloadedFile> Check(ExportManifest manifest, string folder, CancellationToken ct = default)
    {
        var candidates = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).ToList()
            : [];
        var partials = Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*.zip*", SearchOption.TopDirectoryOnly).Where(IsPartial).ToList()
            : [];
        var result = new List<DownloadedFile>();
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!ExportManifest.IsSafeFileName(f.FileName))
            {
                result.Add(new DownloadedFile(f, DownloadedState.Missing, null));
                continue;
            }
            var stem = System.IO.Path.GetFileNameWithoutExtension(f.FileName);
            var matches = candidates
                .Where(p => IsCopyOf(System.IO.Path.GetFileNameWithoutExtension(p), stem))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            var ready = matches.FirstOrDefault(OpensAsZip);
            if (ready is not null)
                result.Add(new DownloadedFile(f, DownloadedState.Ready, ready));
            else if (partials.Any(p => IsCopyOf(PartialStem(p), stem)))
                result.Add(new DownloadedFile(f, DownloadedState.InProgress, matches.FirstOrDefault()));
            else
                result.Add(new DownloadedFile(f, matches.Count > 0 ? DownloadedState.Damaged : DownloadedState.Missing, matches.FirstOrDefault()));
        }
        return result;
    }

    // Browsers write to a temporary name until a download finishes: Chrome and Edge ".crdownload", Firefox ".part",
    // Safari ".download" (a folder).
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".download", ".partial"];

    private static bool IsPartial(string path) =>
        PartialExtensions.Any(x => path.EndsWith(".zip" + x, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>projects-000 (1).zip.part</c> → <c>projects-000 (1)</c>.</summary>
    private static string PartialStem(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')));

    internal static bool IsCopyOf(string name, string stem)
    {
        if (string.Equals(name, stem, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return false;
        return CopySuffix().IsMatch(name[stem.Length..]);
    }

    /// <summary>
    /// A complete download opens as a zip with at least one file in it. The zip's index is at its end, so a download
    /// that stopped part-way fails here. Damage inside a file is caught when it is unzipped.
    /// </summary>
    public static bool OpensAsZip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.Entries.Any(e => !e.FullName.EndsWith('/'));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // "name (1)", "name(2)", "name-1", "name_1" as browsers and operating systems number repeat downloads.
    [GeneratedRegex(@"^(\s?\(\d{1,3}\)|[-_ ]\d{1,3})$")]
    private static partial Regex CopySuffix();
}
