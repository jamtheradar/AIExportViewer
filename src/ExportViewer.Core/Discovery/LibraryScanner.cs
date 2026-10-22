using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExportViewer.Core.Discovery;

public enum FileKind
{
    Conversations,
    Users,
    Logins,
    Project,
    Memory,
    Artifact,
    Manifest,
    Generic,
    Skipped,
}

/// <param name="RelativePath">Path relative to the library root, always with forward slashes.</param>
/// <param name="Collection">Collection name for generic files; null otherwise.</param>
/// <param name="SkipReason">Plain-English reason when <see cref="Kind"/> is <see cref="FileKind.Skipped"/>.</param>
public sealed record DiscoveredFile(
    string RelativePath,
    string FullPath,
    FileKind Kind,
    long Size,
    long ModifiedTicks,
    string? Collection = null,
    string? SkipReason = null);

public sealed record ScanResult(string Root, IReadOnlyList<DiscoveredFile> Files, int OtherFiles, int ZipFiles);

/// <summary>
/// Walks a library folder (one or more unzipped exports, any layout) and recognises each JSON file by its
/// path pattern and shape. Read-only.
/// </summary>
public static partial class LibraryScanner
{
    public static ScanResult Scan(string root, CancellationToken ct = default)
    {
        root = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {root}");

        var files = new List<DiscoveredFile>();
        int other = 0, zips = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            var artifactJson = System.IO.Path.Combine(dir, "artifact.json");
            if (dir != root && File.Exists(artifactJson))
            {
                // An artifact folder belongs to the artifact as a whole; its version files are not records.
                files.Add(Describe(root, artifactJson));
                continue;
            }

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                files.Add(Skipped(root, dir, "This folder could not be opened."));
                continue;
            }

            foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) files.Add(Describe(root, entry));
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zips++;
                else other++;
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new ScanResult(root, files, other, zips);
    }

    internal static DiscoveredFile Describe(string root, string path)
    {
        var info = new FileInfo(path);
        var rel = Relative(root, path);
        SniffResult sniff;
        try { sniff = JsonSniffer.Sniff(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiscoveredFile(rel, path, FileKind.Skipped, info.Length, info.LastWriteTimeUtc.Ticks, SkipReason: "The file could not be read.");
        }
        var (kind, reason) = Classify(sniff, info.Name);
        var collection = kind == FileKind.Generic ? CollectionName(root, path) : null;
        return new DiscoveredFile(rel, path, kind, info.Length, info.LastWriteTimeUtc.Ticks, collection, reason);
    }

    internal static (FileKind Kind, string? Reason) Classify(SniffResult s, string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (s.Empty) return (FileKind.Skipped, "The file is empty.");

        var p = s.Properties;
        if (s.Root == JsonTokenType.StartArray)
        {
            if (p.Contains("chat_messages")) return (FileKind.Conversations, null);
            if (p.Contains("email_address") && p.Contains("uuid")) return (FileKind.Users, null);
        }
        else if (s.Root == JsonTokenType.StartObject)
        {
            if (p.Contains("login_events")) return (FileKind.Logins, null);
            if (p.Contains("data_files") && p.Contains("total_files")) return (FileKind.Manifest, null);
            if (name == "artifact.json" && p.Contains("id") && p.Contains("versions")) return (FileKind.Artifact, null);
            if (p.Contains("uuid") && p.Contains("docs") && (p.Contains("creator") || p.Contains("prompt_template"))) return (FileKind.Project, null);
            if (p.Contains("account_uuid") && (p.Contains("memory_files") || p.Contains("conversations_memory") || p.Contains("project_memories")))
                return (FileKind.Memory, null);
        }

        // Shape not conclusive (first record odd, truncated, or first value too large to peek): fall back to well-known
        // names, as long as the root is the right type (or could not be read).
        var arrayOk = s.Root is JsonTokenType.StartArray or JsonTokenType.None;
        var objectOk = s.Root is JsonTokenType.StartObject or JsonTokenType.None;
        if (arrayOk && name.StartsWith("conversations", StringComparison.Ordinal)) return (FileKind.Conversations, null);
        if (arrayOk && name == "users.json") return (FileKind.Users, null);
        if (objectOk && name == "login_history.json") return (FileKind.Logins, null);
        if (objectOk && name == "artifact.json") return (FileKind.Artifact, null);
        if (s.Invalid) return (FileKind.Skipped, "The file is not valid JSON.");
        return (FileKind.Generic, null);
    }

    /// <summary>Parent folder name without an export part suffix (<c>frames-000</c> → <c>frames</c>); file name at the root.</summary>
    internal static string CollectionName(string root, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(dir), System.IO.Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(path);
        return PartSuffix().Replace(System.IO.Path.GetFileName(dir), "");
    }

    public static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    private static DiscoveredFile Skipped(string root, string path, string reason) =>
        new(Relative(root, path), path, FileKind.Skipped, 0, 0, SkipReason: reason);

    [GeneratedRegex(@"-\d{3,}$")]
    private static partial Regex PartSuffix();
}
