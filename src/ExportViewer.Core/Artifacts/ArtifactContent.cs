using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExportViewer.Core.Json;

namespace ExportViewer.Core.Artifacts;

public sealed record ArtifactFile(string Path, long? Size, string? MimeType, string? Sha256);

public sealed record CommentThread(bool Resolved, string? Tab, string? QuotedText, IReadOnlyList<Comment> Comments);

public sealed record Comment(string? Author, string Body, string? CreatedAt, bool ByClaude);

/// <summary>
/// Reads artifact files from the export folder for viewing. Read-only, and every path is checked to stay inside the
/// artifact's version folder, so a crafted file listing cannot reach elsewhere on disk.
/// </summary>
public static partial class ArtifactContent
{
    public const int MaxTextBytes = 2 * 1024 * 1024;

    /// <param name="libraryRoot">Library folder.</param>
    /// <param name="contentFolder">Version folder relative to the library root (e.g. <c>x/artifacts/id/versions/v1</c>).</param>
    public static IReadOnlyList<ArtifactFile> ListFiles(string libraryRoot, string contentFolder)
    {
        var dir = Resolve(libraryRoot, contentFolder, "");
        if (dir is null || !Directory.Exists(dir)) return [];

        var listing = dir.TrimEnd(System.IO.Path.DirectorySeparatorChar) + ".files.json";
        if (File.Exists(listing))
        {
            try
            {
                using var fs = new FileStream(listing, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var doc = JsonDocument.Parse(fs);
                var files = doc.RootElement.Arr("files")
                    .Select(f => new ArtifactFile(f.Str("exported_as") ?? f.Str("original_path") ?? "", f.Long("size_bytes"), f.Str("mime_type"), f.Str("sha256")))
                    .Where(f => f.Path.Length > 0)
                    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0) return files;
            }
            catch (JsonException)
            {
                // Fall back to listing the folder.
            }
        }

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => new ArtifactFile(System.IO.Path.GetRelativePath(dir, p).Replace('\\', '/'), new FileInfo(p).Length, null, null))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads a text file, or returns null if it is missing, outside the folder, or not text.</summary>
    public static (string Text, bool Truncated)? ReadText(string libraryRoot, string contentFolder, string relativePath, int maxBytes = MaxTextBytes)
    {
        var path = Resolve(libraryRoot, contentFolder, relativePath);
        if (path is null || !File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var len = (int)Math.Min(fs.Length, maxBytes);
        var buffer = new byte[len];
        fs.ReadExactly(buffer);
        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(len, 8000)) >= 0) return null;
        return (Encoding.UTF8.GetString(buffer), fs.Length > maxBytes);
    }

    /// <summary>True when the file on disk matches the sha256 in the export's file listing.</summary>
    public static bool? VerifyHash(string libraryRoot, string contentFolder, ArtifactFile file)
    {
        if (file.Sha256 is null) return null;
        var path = Resolve(libraryRoot, contentFolder, file.Path);
        if (path is null || !File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return string.Equals(Convert.ToHexString(SHA256.HashData(fs)), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full path for a file inside the version folder, or null if it would escape that folder.</summary>
    internal static string? Resolve(string libraryRoot, string contentFolder, string relativePath)
    {
        var root = System.IO.Path.GetFullPath(libraryRoot);
        var baseDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, contentFolder));
        if (!IsUnder(baseDir, root)) return null;
        if (relativePath.Length == 0) return baseDir;
        if (System.IO.Path.IsPathRooted(relativePath)) return null;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, relativePath));
        return IsUnder(full, baseDir) ? full : null;
    }

    private static bool IsUnder(string path, string dir)
    {
        var d = dir.EndsWith(System.IO.Path.DirectorySeparatorChar) ? dir : dir + System.IO.Path.DirectorySeparatorChar;
        return path.Equals(dir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wraps artifact HTML for a sandboxed iframe: a CSP that blocks scripts and every remote load goes first.
    /// The iframe itself must also use an empty <c>sandbox</c> attribute.
    /// </summary>
    public static string PreviewDocument(string html) =>
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:\">" + html;

    /// <summary>Claude Docs pages use HTML comments as tab markers; turn them into headings and drop other comments.</summary>
    public static string CleanPageMarkdown(string markdown) =>
        OtherComment().Replace(TabComment().Replace(markdown, m => $"\n\n---\n\n**Tab: {m.Groups[1].Value.Trim()}**\n\n"), "");

    public static IReadOnlyList<CommentThread> ParseComments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var threads = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : doc.RootElement.Arr("threads");
            return threads.Select(t => new CommentThread(
                t.Bool("resolved") ?? false, t.Str("tab"), t.Str("quoted_text"),
                t.Arr("comments").Select(c => new Comment(
                    c.Path("author", "full_name") ?? c.Str("author_role"),
                    c.Str("body") ?? c.Str("text") ?? "",
                    c.UtcStr("created_at"),
                    c.Bool("posted_by_agent") ?? false)).ToList())).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"<!--\s*tab:(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex TabComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex OtherComment();
}
