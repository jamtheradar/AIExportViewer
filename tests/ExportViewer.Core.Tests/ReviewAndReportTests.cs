using System.Text;
using ExportViewer.Core.Audit;
using ExportViewer.Core.Conversations;
using ExportViewer.Core.Reports;
using ExportViewer.Core.Review;
using ExportViewer.Core.Search;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class ReviewStoreTests
{
    [Fact]
    public void Flags_and_notes_can_be_added_updated_and_removed()
    {
        using var store = ReviewStore.OpenInMemory();
        var conv = Fake.Id(100);
        store.Set(conv, null, "Check with HR");
        store.Set(conv, Fake.Id(101), "");
        var updated = store.Set(conv, Fake.Id(101), "  Shared a client name  ");

        Assert.Equal("Shared a client name", updated.Note);
        Assert.Equal(2, store.Count());
        Assert.True(store.Get(conv, null)!.IsConversation);
        Assert.Equal([conv], store.FlaggedConversations());

        store.Remove(conv, Fake.Id(101));
        Assert.Null(store.Get(conv, Fake.Id(101)));
        Assert.Single(store.ForConversation(conv));
        store.Clear();
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public async Task Flags_survive_a_library_rebuild_because_they_are_stored_separately()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        await lib.LoadAsync();
        var path = Path.Combine(lib.Root, "..", Guid.NewGuid().ToString("N") + "-review.db");
        try
        {
            using (var store = ReviewStore.OpenFile(path))
            {
                store.SetLibraryLabel("Test library");
                store.Set(Fake.Id(100), Fake.Id(102), "Odd answer");
                store.Set(Fake.Id(999), null, "Gone later");
            }

            lib.Db.Reset();
            await lib.LoadAsync();

            using var reopened = ReviewStore.OpenFile(path);
            var items = FlagList.Build(reopened.All(), lib.Queries);
            var answer = Assert.Single(items, i => i.Flag.MessageUuid == Fake.Id(102));
            Assert.Equal("Research quokkas", answer.ConversationTitle);
            Assert.Equal("assistant", answer.MessageSender);
            Assert.False(answer.Missing);
            Assert.True(Assert.Single(items, i => i.Flag.ConversationUuid == Fake.Id(999)).Missing);
            Assert.Equal("Test library", ReviewStore.ReadLibraryLabel(path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
        }
    }
}

public class ReportTests
{
    private static readonly ReportContext Ctx = new("Test export", Fake.T(0), Fake.T(100), TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 22, 1, 2, 0, TimeSpan.Zero));

    private static Report Sample(params string[][] rows) => new()
    {
        Title = "Sample",
        Context = Ctx,
        Details = [("Person", "Alex Example")],
        Notes = ["A note"],
        Columns = [new("A"), new("B")],
        Rows = rows,
    };

    [Fact]
    public void Csv_has_header_lines_quotes_and_neutralised_formulas()
    {
        var bytes = CsvReportWriter.ToBytes(Sample(["=HYPERLINK(\"http://x\")", "plain"], ["has, comma", "line\nbreak"], ["-5", "@SUM(A1)"]));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Report,Sample\r\nExport,Test export\r\n", text);
        Assert.Contains("Generated,2026-10-22 01:02 (UTC+00:00)", text);
        Assert.Contains("\r\nA,B\r\n", text);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\",plain", text);
        Assert.Contains("\"has, comma\",\"line\nbreak\"", text);
        Assert.Contains("'-5,'@SUM(A1)", text);
    }

    [Fact]
    public void Pdf_is_produced_for_tables_and_transcripts_including_non_latin_text()
    {
        var table = PdfReportWriter.ToBytes(Sample(["Café ünïcödé", "日本語 and 中文"], ["Emoji 😀 here", new string('x', 5000)]));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(table, 0, 4));

        var transcript = new Report
        {
            Title = "Conversation: Test",
            Context = Ctx,
            Columns = [new("Time"), new("From"), new("Message"), new("Note")],
            Rows = [["2026-01-05 09:00", "Alex", "Hello\n\nSecond paragraph\twith tab", "Reviewer flag: check"], ["2026-01-05 09:01", "Claude", "Hi", ""]],
            Layout = ReportLayout.Transcript,
        };
        var pdf = PdfReportWriter.ToBytes(transcript);
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task Conversation_report_lists_text_tool_steps_attachments_and_reviewer_notes()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        await lib.LoadAsync();
        var d = lib.Queries.GetConversation(Fake.Id(100))!;
        var flags = new[] { new ReviewFlag(1, Fake.Id(100), Fake.Id(102), "Check the source", Fake.T(1), Fake.T(1)), new ReviewFlag(2, Fake.Id(100), null, "", Fake.T(1), Fake.T(1)) };

        var report = ReportBuilder.Conversation(Ctx, d, d.Messages, flags, allVersions: false);

        Assert.Equal(ReportLayout.Transcript, report.Layout);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal("Alex Example", report.Rows[0][1]);
        Assert.Contains("Tell me about quokkas", report.Rows[0][2]);
        Assert.Contains("[Attachment: notes.txt]\nAttached notes mention Rottnest Island", report.Rows[0][2]);
        Assert.DoesNotContain("likes marsupials", report.Rows[0][2]); // injected context is not part of what the person wrote
        Assert.Contains("Searched the web for “quokka habitat”", report.Rows[1][2]);
        Assert.Contains("quokka.png (the file itself is not in the export)", report.Rows[1][2]);
        Assert.Equal("Reviewer flag: Check the source", report.Rows[1][3]);
        Assert.Contains("Flagged by the reviewer.", report.Notes);
        Assert.Contains(("Conversation ID", Fake.Id(100)), report.Details);
        Assert.Equal("2026-01-05 09:10", report.Rows[0][0]);
    }

    [Fact]
    public async Task Conversation_search_and_flag_reports_mask_sensitive_values_unless_asked_not_to()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        lib.Json("export-a/conversations-001/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(300), Fake.Id(1), $"TFN {FakeSecrets.Tfn}", 40, 41,
                Fake.Turn(301, 302, Fake.Root, 40, $"card {FakeSecrets.Card} for wombats", "ok")),
        });
        await lib.LoadAsync();
        var d = lib.Queries.GetConversation(Fake.Id(300))!;
        var flags = new[] { new ReviewFlag(1, Fake.Id(300), Fake.Id(301), "", Fake.T(1), Fake.T(1)) };

        static string All(Report r) => Encoding.UTF8.GetString(CsvReportWriter.ToBytes(r));
        var masked = All(ReportBuilder.Conversation(Ctx, d, d.Messages, flags, allVersions: false));
        Assert.DoesNotContain(FakeSecrets.Card, masked);
        Assert.DoesNotContain(FakeSecrets.Tfn, masked);
        Assert.Contains("•••• •••• •••• 1111", masked);
        Assert.Contains(FakeSecrets.Card, All(ReportBuilder.Conversation(Ctx, d, d.Messages, flags, allVersions: false, mask: false)));

        var hits = new SearchService(lib.Db).Search(new SearchRequest("wombats"));
        Assert.DoesNotContain(FakeSecrets.Card, All(ReportBuilder.Search(Ctx, "wombats", [], hits.Total, hits.Hits, k => k)));

        var items = FlagList.Build(flags, lib.Queries);
        var flagReport = All(ReportBuilder.Flags(Ctx, items, []));
        Assert.DoesNotContain(FakeSecrets.Card, flagReport);
        Assert.DoesNotContain(FakeSecrets.Tfn, flagReport);
    }

    [Fact]
    public async Task Search_and_findings_reports_carry_plain_text_and_masked_values()
    {
        using var lib = new TempLibrary();
        lib.WriteStandardExport();
        lib.Json("export-a/conversations-001/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(300), Fake.Id(1), "Card", 40, 41, Fake.Turn(301, 302, Fake.Root, 40, $"card {FakeSecrets.Card} & wombats", "ok")),
        });
        await lib.LoadAsync();

        var results = new SearchService(lib.Db).Search(new SearchRequest("wombats"));
        var search = ReportBuilder.Search(Ctx, "wombats", [("Person", "Everyone")], results.Total, results.Hits, k => k);
        Assert.Contains(search.Rows, r => r[4].Contains("«wombats»") && r[4].Contains('&'));

        new AuditScanner(lib.Db).Run([]);
        var q = new AuditQueries(lib.Db);
        var (total, rows) = q.GetFindings(new FindingFilter(Type: nameof(FindingType.CardNumber)));
        var findings = ReportBuilder.Findings(Ctx, FindingSource.Sensitive, rows, total, q.GetScanInfo(), []);
        var csv = Encoding.UTF8.GetString(CsvReportWriter.ToBytes(findings));
        Assert.Contains("•••• •••• •••• 1111", csv);
        Assert.DoesNotContain(FakeSecrets.Card, csv);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(PdfReportWriter.ToBytes(findings), 0, 4));
    }

    [Fact]
    public void File_names_are_safe_and_dated()
    {
        var r = new Report { Title = "Conversation: a/b\\c:d*?", Context = Ctx, Columns = [new("A")], Rows = [] };
        Assert.Equal("Conversation a b c d 2026-10-22 0102.pdf", r.FileName("pdf"));
    }
}
