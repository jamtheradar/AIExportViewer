using System.Diagnostics;
using ExportViewer.Core.Loading;
using ExportViewer.Core.Search;
using ExportViewer.Core.Store;
using ExportViewer.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace ExportViewer.Core.Tests;

/// <summary>Builds a 500 MB synthetic export once and loads it into a file-backed library (as the app does).</summary>
public sealed class LargeLibraryFixture : IDisposable
{
    public const long TargetBytes = 500L * 1024 * 1024;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "aiev-perf", Guid.NewGuid().ToString("N"));
    public LibraryDatabase Db { get; }
    public int Conversations { get; }
    public int Messages { get; }
    public long FileBytes { get; }
    public TimeSpan LoadTime { get; }
    public long PeakManagedBytes { get; }
    public LoadSummary Summary { get; }

    public LargeLibraryFixture()
    {
        var file = Path.Combine(Root, "conversations-000", "conversations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        (Conversations, Messages) = LargeExportGenerator.Write(file, TargetBytes);
        FileBytes = new FileInfo(file).Length;

        Db = LibraryDatabase.OpenFile(Path.Combine(Root, "library.db"));
        GC.Collect();
        long peak = 0;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(25);
            }
        });
        var sw = Stopwatch.StartNew();
        Summary = new LibraryLoader(Db).LoadAsync(Root, true).GetAwaiter().GetResult();
        LoadTime = sw.Elapsed;
        cts.Cancel();
        sampler.Wait();
        PeakManagedBytes = peak;
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

[Trait("Category", "Performance")]
public class PerformanceTests(LargeLibraryFixture lib, ITestOutputHelper output) : IClassFixture<LargeLibraryFixture>
{
    [Fact]
    public void Loads_a_500MB_conversations_file_without_holding_it_in_memory()
    {
        output.WriteLine($"File {lib.FileBytes / 1024 / 1024} MB, {lib.Conversations:N0} conversations, {lib.Messages:N0} messages");
        output.WriteLine($"Load {lib.LoadTime.TotalSeconds:F1}s, peak managed heap {lib.PeakManagedBytes / 1024 / 1024} MB");

        Assert.True(lib.FileBytes >= LargeLibraryFixture.TargetBytes);
        Assert.Equal(lib.Conversations, lib.Summary.Files.Single().Records);
        Assert.Equal(lib.Messages, new LibraryQueries(lib.Db).GetOverview().Messages);
        // Streaming: the managed heap stays far below the file size.
        Assert.True(lib.PeakManagedBytes < 250L * 1024 * 1024, $"Peak managed heap was {lib.PeakManagedBytes / 1024 / 1024} MB");
    }

    [Theory]
    [InlineData(LargeExportGenerator.RareWord)]
    [InlineData("ba*")]
    [InlineData("\"" + "placeholder" + "\"")]
    public void Search_returns_in_under_a_second(string query)
    {
        if (query == "\"placeholder\"") query = CommonPhrase();
        var search = new SearchService(lib.Db);
        search.Search(new SearchRequest(query)); // warm the page cache, as after the first search in the app

        var sw = Stopwatch.StartNew();
        var r = search.Search(new SearchRequest(query));
        sw.Stop();
        output.WriteLine($"'{query}': {r.Total:N0} results in {sw.ElapsedMilliseconds} ms over {lib.Messages:N0} messages");

        Assert.Null(r.Error);
        Assert.True(r.Total > 0);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Search took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Conversation_list_pages_quickly()
    {
        var q = new LibraryQueries(lib.Db);
        var sw = Stopwatch.StartNew();
        var (total, items) = q.GetConversations(new ConversationFilter(Limit: 50));
        sw.Stop();
        output.WriteLine($"List page: {sw.ElapsedMilliseconds} ms for {total:N0} conversations");
        Assert.Equal(50, items.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    private string CommonPhrase()
    {
        // Two consecutive words from a real message, so the phrase certainly exists.
        var first = new LibraryQueries(lib.Db).GetConversations(new ConversationFilter(Limit: 1)).Items[0];
        var words = first.Title.Split(' ');
        return $"\"{words[0]} {words[1]}\"";
    }
}
