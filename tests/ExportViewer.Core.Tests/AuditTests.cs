using ExportViewer.Core.Audit;
using ExportViewer.Core.Store;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class DetectorTests
{
    private static IReadOnlyList<Detection> Find(string text) => SensitiveDetectors.Find(text);

    private static Detection Single(string text, FindingType type)
    {
        var found = Find(text).Where(d => d.Type == type).ToList();
        Assert.True(found.Count == 1, $"expected one {type} in “{text}”, found {found.Count}: {string.Join(", ", Find(text).Select(d => d.Type))}");
        return found[0];
    }

    private static void None(string text, FindingType type) =>
        Assert.DoesNotContain(Find(text), d => d.Type == type);

    [Fact]
    public void Tax_file_number_needs_a_valid_check_digit_and_grouping_or_a_keyword()
    {
        Assert.Equal(Confidence.High, Single($"My TFN is {FakeSecrets.Tfn}.", FindingType.TaxFileNumber).Confidence);
        Assert.Equal(Confidence.Medium, Single($"Number {FakeSecrets.Tfn} on file", FindingType.TaxFileNumber).Confidence);
        Single($"tax file number {FakeSecrets.TfnUngrouped}", FindingType.TaxFileNumber);
        None($"order {FakeSecrets.TfnUngrouped} shipped", FindingType.TaxFileNumber);
        None("My TFN is 123 456 789", FindingType.TaxFileNumber); // check digit fails
    }

    [Fact]
    public void Abn_medicare_card_and_iban_must_pass_their_checksums()
    {
        Assert.Equal(Confidence.High, Single($"ABN {FakeSecrets.Abn}", FindingType.BusinessNumber).Confidence);
        None("ABN 51 824 753 557", FindingType.BusinessNumber);

        Single($"Medicare card {FakeSecrets.Medicare}", FindingType.MedicareNumber);
        None("Medicare card 2123 45671 1", FindingType.MedicareNumber);

        Assert.Equal(Confidence.High, Single($"card {FakeSecrets.Card} exp 12/30", FindingType.CardNumber).Confidence);
        None($"card {FakeSecrets.CardBadChecksum}", FindingType.CardNumber);
        None("card 0000 0000 0000 0000", FindingType.CardNumber);

        Single($"Pay to {FakeSecrets.Iban} please", FindingType.Iban);
        None("Pay to GB83 WEST 1234 5698 7654 32 please", FindingType.Iban);
    }

    [Fact]
    public void Bank_details_are_found_by_bsb_or_account_keywords()
    {
        Single("BSB: 062-000 thanks", FindingType.BankAccount);
        Single("account number 12345678", FindingType.BankAccount);
        Single("Deposit to 062-000 12345678", FindingType.BankAccount);
        None("meeting 3-4 pm in room 062-000", FindingType.BankAccount);
    }

    [Fact]
    public void Contact_details_and_ip_addresses()
    {
        Single($"email {FakeSecrets.Email} now", FindingType.Email);
        Single($"call {FakeSecrets.Mobile}", FindingType.Phone);
        Single("call +61 2 9876 5432", FindingType.Phone);
        Single($"server at {FakeSecrets.Ip}.", FindingType.IpAddress);
        None("upgrade to version 1.2.3.4", FindingType.IpAddress);
        None("loopback 127.0.0.1", FindingType.IpAddress);
    }

    [Fact]
    public void Keys_tokens_private_keys_and_passwords()
    {
        Assert.Equal("key:aws", Single($"key={FakeSecrets.AwsKey}", FindingType.ApiKey).Detector);
        Assert.Equal("key:github", Single($"token {FakeSecrets.GitHubToken}", FindingType.ApiKey).Detector);
        Assert.Equal("key:anthropic", Single($"export KEY={FakeSecrets.AnthropicKey}", FindingType.ApiKey).Detector);
        Single($"Authorization: Bearer {FakeSecrets.Jwt}", FindingType.Token);
        None("eyJub3QiOiJhIGp3dCJ9.eyJzdWIiOiJ4In0x.abcdefghij", FindingType.Token); // header has no "alg"
        Single($"here:\n{FakeSecrets.PemKey}\nthanks", FindingType.PrivateKey);

        var pw = Single("login with password: Tr0ub4dor3x", FindingType.Password);
        Assert.Equal("Tr0ub4dor3x", "login with password: Tr0ub4dor3x".Substring(pw.Start, pw.Length));
        Single("connect to postgres://admin:s3cretPw@db.example.test/app", FindingType.Password);
        None("the password is incorrect", FindingType.Password);
        None("password: ********", FindingType.Password);
        None("password: <your password here>", FindingType.Password);
    }

    [Fact]
    public void One_value_is_reported_once_as_its_most_specific_type()
    {
        var found = Find($"card {FakeSecrets.Card}");
        Assert.Single(found);
        Assert.Equal(FindingType.CardNumber, found[0].Type);
    }

    [Fact]
    public void Masking_keeps_only_enough_to_tell_values_apart()
    {
        Assert.Equal("•••• •••• •••• 1111", Masking.Mask(FindingType.CardNumber, FakeSecrets.Card));
        Assert.Equal("••• ••• 782", Masking.Mask(FindingType.TaxFileNumber, FakeSecrets.Tfn));
        Assert.Equal("s•••@•••.test", Masking.Mask(FindingType.Email, FakeSecrets.Email));
        Assert.Equal("203.•.•.•", Masking.Mask(FindingType.IpAddress, FakeSecrets.Ip));
        Assert.Equal("••••••••", Masking.Mask(FindingType.Password, "Tr0ub4dor3x"));
        Assert.StartsWith("ghp_", Masking.Mask(FindingType.ApiKey, FakeSecrets.GitHubToken));
        Assert.DoesNotContain("aaaa", Masking.Mask(FindingType.ApiKey, FakeSecrets.GitHubToken));
        // An unrecognised key keeps at most 4 characters, even if it has a dash early on.
        Assert.Equal("ab-c••••••••", Masking.Mask(FindingType.ApiKey, "ab-cdefgh-ijklmnop1234"));
        Assert.Equal($"Card •••• •••• •••• 1111 and {Masking.Mask(FindingType.Email, FakeSecrets.Email)}",
            Masking.MaskAll($"Card {FakeSecrets.Card} and {FakeSecrets.Email}"));
    }
}

public class WatchlistTests
{
    private static Watchlist List(bool patterns, params string[] terms) => new() { Name = "Clients", Patterns = patterns, Terms = terms.ToList() };

    [Fact]
    public void Words_match_whole_words_ignoring_case_and_spacing()
    {
        var m = WatchlistMatcher.Compile([List(false, "Acme Corp", "QX-12")], out var errors);
        Assert.Empty(errors);
        var hits = m.Find("Met with ACME   corp about QX-12; acmecorp is different. Not QX-123.");
        Assert.Equal(["Acme Corp", "QX-12"], hits.Select(h => h.Term));
        Assert.Equal("ACME   corp", "Met with ACME   corp about QX-12; acmecorp is different. Not QX-123."[hits[0].Start..(hits[0].Start + hits[0].Length)]);
    }

    [Fact]
    public void Patterns_are_checked_and_bad_ones_reported_by_line()
    {
        var list = List(true, @"PRJ-\d{4}", "", "(unclosed", "a*");
        var errors = WatchlistMatcher.Validate(list);
        Assert.Equal([3, 4], errors.Select(e => e.Line));
        var m = WatchlistMatcher.Compile([list], out _);
        var hit = Assert.Single(m.Find("see PRJ-2024 and PRJ-12"));
        Assert.Equal((4, 8), (hit.Start, hit.Length));
    }

    [Fact]
    public void A_slow_pattern_times_out_and_is_dropped_without_hanging()
    {
        // Back-references force the backtracking engine, where this pattern is exponential.
        var list = List(true, @"^(\w+\s?)*\1$");
        var m = WatchlistMatcher.Compile([list], out var errors);
        Assert.Empty(errors);
        var text = new string('a', 30) + " " + new string('b', 30) + "!";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < WatchlistMatcher.MaxTimeouts + 3; i++) m.Find(text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.Single(m.Problems);
    }
}

public class AuditScannerTests
{
    private static async Task<TempLibrary> LibraryWithSecrets()
    {
        var lib = new TempLibrary();
        lib.WriteStandardExport();
        var conv = Fake.Conversation(Fake.Id(300), Fake.Id(1), "Payroll help", 40, 41,
            Fake.Message(Fake.Id(301), Fake.Root, "human", 40, [Fake.Text($"My TFN is {FakeSecrets.Tfn} and card {FakeSecrets.Card}. Acme Corp asked.")],
                attachments: [Fake.Attachment($"password: Tr0ub4dor3x", "creds.txt")]),
            Fake.Message(Fake.Id(302), Fake.Id(301), "assistant", 41, [Fake.Text("Please don't share those.")]));
        lib.Json("export-a/conversations-001/conversations.json", new[] { conv });
        await lib.LoadAsync();
        return lib;
    }

    [Fact]
    public async Task Finds_sensitive_values_in_messages_attachments_and_fields()
    {
        using var lib = await LibraryWithSecrets();
        var summary = new AuditScanner(lib.Db).Run([]);
        var q = new AuditQueries(lib.Db);

        var (total, rows) = q.GetFindings(new FindingFilter(Limit: 500));
        Assert.Equal(total, summary.Findings);
        var tfn = Assert.Single(rows, r => r.Type == nameof(FindingType.TaxFileNumber));
        Assert.Equal(SearchKind.Message, tfn.Location);
        Assert.Equal(Fake.Id(300), tfn.TargetId);
        Assert.Equal("Payroll help", tfn.Title);
        Assert.Equal("Alex Example", tfn.UserName);
        Assert.Equal("••• ••• 782", tfn.Masked);
        // The card number next to it is masked in the context too.
        Assert.Contains("•••• •••• •••• 1111", tfn.ContextAfter);
        Assert.DoesNotContain(FakeSecrets.Card, tfn.ContextAfter);

        Assert.Contains(rows, r => r.Type == nameof(FindingType.Password) && r.Location == SearchKind.Attachment);
        Assert.Contains(rows, r => r.Type == nameof(FindingType.Phone) && r.Location == FindingLocation.UserDetails);
        Assert.Equal(2, rows.Count(r => r.Type == nameof(FindingType.IpAddress) && r.Location == FindingLocation.SignIn));

        Assert.Equal(FakeSecrets.Tfn, q.Reveal(tfn.Id));
        Assert.Equal("+61400000000", q.Reveal(rows.First(r => r.Location == FindingLocation.UserDetails).Id));
    }

    [Fact]
    public async Task Watchlist_hits_are_stored_separately_and_filterable()
    {
        using var lib = await LibraryWithSecrets();
        var list = new Watchlist { Name = "Clients", Terms = ["Acme Corp", "quokka"] };
        var summary = new AuditScanner(lib.Db).Run([list]);
        var q = new AuditQueries(lib.Db);

        Assert.True(summary.WatchlistHits >= 2);
        var (_, hits) = q.GetFindings(new FindingFilter(FindingSource.Watchlist, ListId: list.Id, Limit: 100));
        Assert.Contains(hits, h => h.Detector == "Acme Corp" && h.Masked == "Acme Corp" && h.ConversationUuid == Fake.Id(300));
        // Whole words only: "quokkas" in the title doesn't count, "quokka habitat" in the web search does.
        Assert.Contains(hits, h => h.Detector == "quokka" && h.Location == SearchKind.Tool);
        Assert.DoesNotContain(hits, h => h.Detector == "quokka" && h.Location == SearchKind.Conversation);
        Assert.All(q.GetCounts(FindingSource.Watchlist), c => Assert.Equal(list.Id, c.ListId));
        Assert.Equal(AuditScanner.Fingerprint([list]), q.GetScanInfo().WatchlistFingerprint);
    }

    [Fact]
    public async Task Scan_info_notices_new_files_and_a_second_run_replaces_results()
    {
        using var lib = await LibraryWithSecrets();
        var scanner = new AuditScanner(lib.Db);
        var first = scanner.Run([]).Findings;
        var q = new AuditQueries(lib.Db);
        Assert.False(q.GetScanInfo().LibraryChanged);
        Assert.Equal(first, scanner.Run([]).Findings);
        Assert.Equal(first, q.GetFindings(new FindingFilter(Limit: 1)).Total);

        lib.Json("export-b/conversations-000/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(400), Fake.Id(2), "Later", 50, 51, Fake.Turn(401, 402, Fake.Root, 50, $"ABN {FakeSecrets.Abn}", "ok")),
        });
        await lib.LoadAsync(rebuild: false);
        Assert.True(q.GetScanInfo().LibraryChanged);
    }

    [Fact]
    public async Task Titles_and_context_never_show_an_unmasked_value_even_past_the_per_item_limit()
    {
        using var lib = new TempLibrary();
        var many = string.Join(" ", Enumerable.Range(0, 260).Select(i => $"user{i}@example.test"));
        lib.Json("conversations-000/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(500), Fake.Id(1), $"Card {FakeSecrets.Card}", 1, 2, Fake.Turn(501, 502, Fake.Root, 1, many, "ok")),
        });
        await lib.LoadAsync();
        new AuditScanner(lib.Db).Run([]);

        var (_, rows) = new AuditQueries(lib.Db).GetFindings(new FindingFilter(Limit: 1000));
        Assert.NotEmpty(rows);
        foreach (var r in rows)
        {
            Assert.DoesNotContain(FakeSecrets.Card, r.Title);
            Assert.DoesNotContain("@example.test", r.ContextBefore + r.ContextAfter);
        }
    }

    [Fact]
    public void Contact_details_in_tool_activity_are_marked_unsure_but_secrets_are_not()
    {
        var email = new Detection(FindingType.Email, 0, 5, Confidence.Medium, "email");
        var key = new Detection(FindingType.ApiKey, 0, 5, Confidence.High, "key:aws");
        Assert.Equal(Confidence.Low, AuditScanner.Adjust(email, SearchKind.Tool));
        Assert.Equal(Confidence.Medium, AuditScanner.Adjust(email, SearchKind.Message));
        Assert.Equal(Confidence.High, AuditScanner.Adjust(key, SearchKind.Tool));
    }

    [Fact]
    public async Task Cancelling_keeps_the_previous_results()
    {
        using var lib = await LibraryWithSecrets();
        var scanner = new AuditScanner(lib.Db);
        var before = scanner.Run([]).Findings;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => scanner.Run([], null, cts.Token));
        Assert.Equal(before, new AuditQueries(lib.Db).GetFindings(new FindingFilter(Limit: 1)).Total);
    }
}
