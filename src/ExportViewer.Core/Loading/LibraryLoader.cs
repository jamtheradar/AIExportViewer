using System.Diagnostics;
using System.Text.Json;
using ExportViewer.Core.Conversations;
using ExportViewer.Core.Discovery;
using ExportViewer.Core.Json;
using ExportViewer.Core.Model;
using ExportViewer.Core.Parsing;
using ExportViewer.Core.Store;
using Microsoft.Data.Sqlite;

namespace ExportViewer.Core.Loading;

public enum LoadStage { Scanning, Reading, Finishing, Done }

public sealed record LoadProgress(LoadStage Stage, string? CurrentFile, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, int Records);

/// <summary>How the library folder differs from what the database already holds.</summary>
public sealed record LibraryChanges(
    IReadOnlyList<DiscoveredFile> New,
    IReadOnlyList<DiscoveredFile> Unchanged,
    IReadOnlyList<string> ChangedOrRemoved)
{
    public bool NeedsRebuild => ChangedOrRemoved.Count > 0;
    public bool IsUpToDate => New.Count == 0 && ChangedOrRemoved.Count == 0;
}

public sealed record FileLoadResult(string Path, FileKind Kind, int Records, int Skipped, string? Note);

public sealed record LoadSummary(
    string Root,
    IReadOnlyList<FileLoadResult> Files,
    IReadOnlyList<DiscoveredFile> SkippedFiles,
    int OtherFiles,
    int ZipFiles,
    bool Rebuilt,
    TimeSpan Elapsed);

public sealed class LibraryLoader
{
    /// <summary>Bump when tree or integrity rules change so existing libraries re-check every conversation.</summary>
    public const string AnalysisVersion = "3";

    private const int ConversationsPerCommit = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    private readonly LibraryDatabase _db;

    public LibraryLoader(LibraryDatabase db) => _db = db;

    /// <summary>True when the library was analysed with older rules and needs a (quick) re-check, even if no files changed.</summary>
    public bool NeedsReanalysis => _db.GetMeta("analysis_version") != AnalysisVersion;

    public LibraryChanges Compare(ScanResult scan)
    {
        var known = new Dictionary<string, (long Size, long Mtime)>(StringComparer.Ordinal);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT path, size, mtime FROM source_files WHERE status <> 'loading'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        }
        var @new = new List<DiscoveredFile>();
        var same = new List<DiscoveredFile>();
        var changed = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in scan.Files.Where(f => f.Kind != FileKind.Skipped))
        {
            present.Add(f.RelativePath);
            if (!known.TryGetValue(f.RelativePath, out var k)) @new.Add(f);
            else if (k.Size == f.Size && k.Mtime == f.ModifiedTicks) same.Add(f);
            else changed.Add(f.RelativePath);
        }
        changed.AddRange(known.Keys.Where(p => !present.Contains(p)));
        return new LibraryChanges(@new, same, changed);
    }

    /// <summary>
    /// Loads new files from the library folder. When files were changed or removed since the last load the library is
    /// rebuilt from scratch if <paramref name="rebuildIfChanged"/> is true; otherwise only new files are loaded.
    /// </summary>
    public async Task<LoadSummary> LoadAsync(string root, bool rebuildIfChanged, IProgress<LoadProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new LoadProgress(LoadStage.Scanning, null, 0, 0, 0, 0, 0));
        var scan = await Task.Run(() => LibraryScanner.Scan(root, ct), ct).ConfigureAwait(false);
        var changes = Compare(scan);
        var rebuilt = false;
        IReadOnlyList<DiscoveredFile> toLoad = changes.New;
        if (changes.NeedsRebuild && rebuildIfChanged)
        {
            _db.Reset();
            toLoad = scan.Files.Where(f => f.Kind != FileKind.Skipped).ToList();
            rebuilt = true;
        }
        _db.SetMeta("root", scan.Root);

        var results = await Task.Run(() => LoadFiles(scan.Root, toLoad, progress, ct), ct).ConfigureAwait(false);

        using (var c = _db.Open())
        using (var w = new LibraryWriter(c))
        {
            w.Raw("DELETE FROM load_issues WHERE path IS NULL AND message LIKE 'Skipped:%'").ExecuteNonQuery();
            foreach (var s in scan.Files.Where(f => f.Kind == FileKind.Skipped))
                w.Raw("INSERT INTO load_issues(path, severity, message) SELECT $1, 1, $2 WHERE NOT EXISTS (SELECT 1 FROM load_issues WHERE path=$1 AND message=$2)",
                    s.RelativePath, "Skipped: " + s.SkipReason).ExecuteNonQuery();
            w.Commit();
        }

        progress?.Report(new LoadProgress(LoadStage.Finishing, null, toLoad.Count, toLoad.Count, 0, 0, 0));
        if (NeedsReanalysis)
        {
            using var c = _db.Open();
            LibraryDatabase.Exec(c, "UPDATE conversations SET dirty=1");
        }
        await Task.Run(() => Finalise(ct), ct).ConfigureAwait(false);
        _db.SetMeta("analysis_version", AnalysisVersion);
        _db.SetMeta("last_loaded", DateTimeOffset.UtcNow.ToString("O"));
        progress?.Report(new LoadProgress(LoadStage.Done, null, toLoad.Count, toLoad.Count, 0, 0, 0));

        return new LoadSummary(scan.Root, results, scan.Files.Where(f => f.Kind == FileKind.Skipped).ToList(),
            scan.OtherFiles, scan.ZipFiles, rebuilt, sw.Elapsed);
    }

    private List<FileLoadResult> LoadFiles(string root, IReadOnlyList<DiscoveredFile> files, IProgress<LoadProgress>? progress, CancellationToken ct)
    {
        // Small files first so people and projects exist before the big conversation files.
        var ordered = files.OrderBy(f => f.Kind switch
        {
            FileKind.Manifest => 0, FileKind.Users => 1, FileKind.Logins => 2, FileKind.Project => 3, FileKind.Memory => 4,
            FileKind.Artifact => 5, FileKind.Generic => 6, _ => 7,
        }).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToList();

        var bytesTotal = ordered.Sum(f => f.Size);
        var tracker = new ProgressTracker(progress, ordered.Count, bytesTotal);
        var results = new List<FileLoadResult>();

        using var c = _db.Open();
        using var w = new LibraryWriter(c);
        foreach (var f in ordered)
        {
            ct.ThrowIfCancellationRequested();
            tracker.StartFile(f.RelativePath);
            var fileId = w.BeginFile(f.RelativePath, f.Kind.ToString(), f.Collection, f.Size, f.ModifiedTicks);
            FileLoadResult result;
            try
            {
                result = f.Kind switch
                {
                    FileKind.Conversations => LoadConversations(w, f, fileId, tracker, ct),
                    FileKind.Artifact => LoadArtifact(w, root, f, fileId),
                    _ => LoadDocument(w, root, f, fileId, ct),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = new FileLoadResult(f.RelativePath, f.Kind, 0, 0, "The file could not be read.");
                w.Issue(f.RelativePath, 2, result.Note!);
            }
            w.EndFile(fileId, result.Note is null ? "loaded" : "partial", result.Records, result.Skipped, result.Note);
            w.Commit();
            results.Add(result);
            tracker.EndFile(f.Size);
        }
        return results;
    }

    private static FileLoadResult LoadConversations(LibraryWriter w, DiscoveredFile f, long fileId, ProgressTracker tracker, CancellationToken ct)
    {
        int records = 0, skipped = 0, sinceCommit = 0;
        string? note = null;
        using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var counting = new CountingStream(fs, n => tracker.Bytes(n));
        try
        {
            // One conversation in memory at a time: the array is enumerated element by element.
            foreach (var element in EnumerateArray(counting, ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var conv = Mappers.Conversation(element);
                    var bad = Mappers.SkippedMessages(element);
                    if (bad > 0)
                    {
                        skipped += bad;
                        w.Issue(f.RelativePath, 1, $"{bad} message(s) without an ID were skipped in one conversation.");
                    }
                    w.Conversation(conv, fileId);
                    records++;
                    tracker.Record();
                }
                catch (FormatException ex)
                {
                    skipped++;
                    w.Issue(f.RelativePath, 1, $"Record {records + skipped} skipped: {ex.Message}");
                }
                if (++sinceCommit >= ConversationsPerCommit)
                {
                    w.Commit();
                    sinceCommit = 0;
                }
            }
        }
        catch (JsonException)
        {
            note = records == 0
                ? "The file is not valid JSON or is cut short. Nothing could be read from it."
                : $"The file is cut short or damaged. The first {records} conversation(s) were read; anything after that is missing.";
            w.Issue(f.RelativePath, 2, note);
        }
        return new FileLoadResult(f.RelativePath, f.Kind, records, skipped, note);
    }

    /// <remarks>Throws <see cref="JsonException"/> when the root is not an array or the file is cut short.</remarks>
    private static IEnumerable<JsonElement> EnumerateArray(Stream s, CancellationToken ct) =>
        JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(s, JsonOptions, ct).ToBlockingEnumerable(ct);

    private static FileLoadResult LoadDocument(LibraryWriter w, string root, DiscoveredFile f, long fileId, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        }
        catch (JsonException)
        {
            const string note = "The file is not valid JSON or is cut short. Nothing could be read from it.";
            w.Issue(f.RelativePath, 2, note);
            return new FileLoadResult(f.RelativePath, f.Kind, 0, 0, note);
        }

        using (doc)
        {
            var e = doc.RootElement;
            int records = 0, skipped = 0;
            void Skip(string why)
            {
                skipped++;
                w.Issue(f.RelativePath, 1, why);
            }

            switch (f.Kind)
            {
                case FileKind.Users:
                    foreach (var u in Items(e))
                        if (Mappers.User(u) is { } user) { w.User(user, fileId); records++; }
                        else Skip("A user without an ID was skipped.");
                    break;
                case FileKind.Logins:
                    foreach (var l in e.Arr("login_events")) { w.Login(Mappers.Login(l)); records++; }
                    break;
                case FileKind.Project:
                    foreach (var p in Items(e))
                    {
                        try { w.Project(Mappers.Project(p), fileId); records++; }
                        catch (FormatException ex) { Skip(ex.Message); }
                    }
                    break;
                case FileKind.Memory:
                    var fallback = System.IO.Path.GetFileNameWithoutExtension(f.FullPath);
                    var exportDate = w.ExportDateFor(f.RelativePath);
                    foreach (var m in Items(e))
                    {
                        try { w.Memory(Mappers.Memory(m, Guid.TryParse(fallback, out _) ? fallback : null), fileId, exportDate); records++; }
                        catch (FormatException ex) { Skip(ex.Message); }
                    }
                    break;
                case FileKind.Manifest:
                    var folder = System.IO.Path.GetDirectoryName(f.RelativePath)?.Replace('\\', '/') ?? "";
                    w.Manifest(folder, Mappers.Manifest(e));
                    records = 1;
                    break;
                default:
                    foreach (var r in GenericItems(e))
                    {
                        ct.ThrowIfCancellationRequested();
                        w.Generic(Mappers.Generic(f.Collection ?? "other", r), fileId);
                        records++;
                    }
                    break;
            }
            return new FileLoadResult(f.RelativePath, f.Kind, records, skipped, null);
        }
    }

    private static FileLoadResult LoadArtifact(LibraryWriter w, string root, DiscoveredFile f, long fileId)
    {
        var folderFull = System.IO.Path.GetDirectoryName(f.FullPath)!;
        var folderRel = LibraryScanner.Relative(root, folderFull);
        try
        {
            using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(fs);
            w.Artifact(Mappers.Artifact(doc.RootElement, folderRel, folderFull), fileId);
            return new FileLoadResult(f.RelativePath, f.Kind, 1, 0, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            var note = ex is FormatException ? ex.Message : "The artifact description is not valid JSON.";
            w.Issue(f.RelativePath, 2, note);
            return new FileLoadResult(f.RelativePath, f.Kind, 0, 1, note);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [e];

    /// <summary>A root array is a list of records; an object with one array property holds them; otherwise the object is one record.</summary>
    private static IEnumerable<JsonElement> GenericItems(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
        if (e.ValueKind == JsonValueKind.Object)
        {
            var arrays = e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
            if (arrays.Count == 1 && e.EnumerateObject().Count() <= 3) return arrays[0].Value.EnumerateArray();
        }
        return [e];
    }

    // ---- finalise: tree, integrity, derived columns, memory index ----

    private void Finalise(CancellationToken ct)
    {
        using var c = _db.Open();
        using var w = new LibraryWriter(c);
        var dirty = new List<(string Uuid, string? UpdatedAt, string? Name)>();
        using (var r = w.Raw("SELECT uuid, updated_at, name FROM conversations WHERE dirty=1").ExecuteReader())
            while (r.Read()) dirty.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));

        var n = 0;
        foreach (var (uuid, updatedAt, name) in dirty)
        {
            ct.ThrowIfCancellationRequested();
            FinaliseConversation(w, uuid, updatedAt, name);
            if (++n % 500 == 0) w.Commit();
        }

        ReindexMemories(w);
        w.Commit();
    }

    private static void FinaliseConversation(LibraryWriter w, string uuid, string? updatedAt, string? name)
    {
        var msgs = new List<MessageInfo>();
        var previews = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var r = w.Raw("SELECT uuid, parent_uuid, sender, created_at, seq, traits, preview FROM messages WHERE conversation_uuid=$1 ORDER BY seq", uuid).ExecuteReader())
            while (r.Read())
            {
                var m = new MessageInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? "unknown" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), (MessageTraits)r.GetInt32(5));
                msgs.Add(m);
                previews[m.Uuid] = r.IsDBNull(6) ? null : r.GetString(6);
            }

        var tree = MessageTree.Build(msgs.Select(m => new TreeNode(m.Uuid, m.ParentUuid, m.CreatedAt, m.Seq)));
        var path = tree.LatestPath();
        var flags = IntegrityChecker.Check(updatedAt, msgs, tree);
        var traits = msgs.Aggregate(MessageTraits.None, (t, m) => t | m.Traits);

        var title = string.IsNullOrWhiteSpace(name)
            ? path.Select(p => msgs.First(m => m.Uuid == p.Uuid)).Where(m => m.Sender == "human").Select(m => previews[m.Uuid])
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) is { } first ? Truncate(first, 80) : null
            : name;

        w.Raw("DELETE FROM integrity_flags WHERE conversation_uuid=$1", uuid).ExecuteNonQuery();
        foreach (var f in flags)
            w.Raw("INSERT INTO integrity_flags(conversation_uuid, severity, code, detail, message_uuid) VALUES($1,$2,$3,$4,$5)",
                uuid, (int)f.Severity, f.Code, f.Detail, f.MessageUuid).ExecuteNonQuery();

        w.Raw("""
            UPDATE conversations SET title=$2, message_count=$3, total_messages=$4, first_message_at=$5, last_message_at=$6,
              traits=$7, fork_count=$8, integrity=$9, dirty=0 WHERE uuid=$1
            """,
            uuid, title, path.Count, msgs.Count, msgs.Min(m => m.CreatedAt), msgs.Max(m => m.CreatedAt), (int)traits, tree.ForkCount,
            flags.Count == 0 ? 0 : (int)flags.Max(f => f.Severity)).ExecuteNonQuery();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>Only the newest memory snapshot per person is searchable, so older exports don't duplicate hits.</summary>
    private static void ReindexMemories(LibraryWriter w)
    {
        w.UnindexKind(SearchKind.Memory);
        var latest = new List<(long Id, string Account, string? At, string Raw)>();
        using (var r = w.Raw("""
            SELECT id, account_uuid, snapshot_at, raw FROM memory_snapshots s
            WHERE id = (SELECT id FROM memory_snapshots t WHERE t.account_uuid = s.account_uuid ORDER BY COALESCE(snapshot_at,'') DESC, file_id DESC LIMIT 1)
            """).ExecuteReader())
            while (r.Read()) latest.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));

        foreach (var (id, account, at, raw) in latest)
        {
            using var doc = JsonDocument.Parse(raw);
            var m = Mappers.Memory(doc.RootElement, account);
            if (!string.IsNullOrWhiteSpace(m.ConversationsMemory))
                w.IndexMemory($"mem:{id}:summary", account, at, "Memory summary", m.ConversationsMemory);
            foreach (var f in m.Files) w.IndexMemory($"mem:{id}:{f.Path}", account, f.UpdatedAt ?? at, f.Path, f.Content);
            foreach (var p in m.ProjectMemories) w.IndexMemory($"mem:{id}:project:{p.ProjectUuid}", account, at, "Project memory", p.Content);
        }
    }

    private sealed class ProgressTracker(IProgress<LoadProgress>? progress, int filesTotal, long bytesTotal)
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private string? _file;
        private int _filesDone;
        private long _bytesBeforeFile;
        private long _fileBytes;
        private int _records;
        private long _lastReport = -1000;

        public void StartFile(string path) { _file = path; _fileBytes = 0; Report(true); }
        public void EndFile(long size) { _filesDone++; _bytesBeforeFile += size; _fileBytes = 0; Report(true); }
        public void Bytes(long n) { _fileBytes += n; Report(false); }
        public void Record() { _records++; Report(false); }

        private void Report(bool force)
        {
            if (progress is null) return;
            var now = _sw.ElapsedMilliseconds;
            if (!force && now - _lastReport < 100) return;
            _lastReport = now;
            progress.Report(new LoadProgress(LoadStage.Reading, _file, _filesDone, filesTotal, _bytesBeforeFile + _fileBytes, bytesTotal, _records));
        }
    }
}
