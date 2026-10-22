using ExportViewer.Core.Search;
using ExportViewer.Core.Store;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class SearchQueryTests
{
    [Theory]
    [InlineData("quokka", "\"quokka\"")]
    [InlineData("quokka island", "\"quokka\" AND \"island\"")]
    [InlineData("quokka OR wombat", "\"quokka\" OR \"wombat\"")]
    [InlineData("quokka or wombat", "\"quokka\" OR \"wombat\"")]
    [InlineData("\"Rottnest Island\"", "\"Rottnest Island\"")]
    [InlineData("quok*", "\"quok\"*")]
    [InlineData("quokka -wombat", "\"quokka\" NOT \"wombat\"")]
    [InlineData("quokka OR island NOT wombat", "(\"quokka\" OR \"island\") NOT \"wombat\"")]
    [InlineData("say \"hi\" AND", "\"say\" AND \"hi\"")]
    [InlineData("a\"b", "\"a\" AND \"b\"")]
    [InlineData("col:value NEAR(x) ^start", "\"col:value\" AND \"NEAR\" AND \"x\" AND \"^start\"")]
    public void Translates_user_syntax(string input, string expected) =>
        Assert.Equal(expected, SearchQuery.ToFts(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-wombat")]
    [InlineData("AND OR NOT")]
    [InlineData("*** ---")]
    public void Returns_null_when_nothing_can_be_searched(string input) =>
        Assert.Null(SearchQuery.ToFts(input));
}

public class SearchServiceTests : IAsyncLifetime
{
    private readonly TempLibrary _lib = new();

    public async Task InitializeAsync()
    {
        _lib.WriteStandardExport();
        _lib.Json("extra/conversations.json", new[]
        {
            Fake.Conversation(Fake.Id(500), Fake.Id(2), "Script test", 1000, 1001,
                Fake.Turn(501, 502, Fake.Root, 1000, "What does <script>alert('x')</script> do?", "It shows an alert box.")),
        });
        await _lib.LoadAsync();
    }

    public Task DisposeAsync()
    {
        _lib.Dispose();
        return Task.CompletedTask;
    }

    private SearchResults Search(string q, string? account = null, string[]? kinds = null, DateTimeOffset? from = null) =>
        new SearchService(_lib.Db).Search(new SearchRequest(q, account, from, null, kinds));

    [Fact]
    public void Finds_text_in_every_kind_of_content()
    {
        Assert.Contains(Search("quokkas").Hits, h => h.Kind == SearchKind.Message && h.TargetId == Fake.Id(100));
        Assert.Contains(Search("habitat").Hits, h => h.Kind == SearchKind.Tool);
        Assert.Contains(Search("Rottnest").Hits, h => h.Kind == SearchKind.Attachment);
        Assert.Contains(Search("burrows").Hits, h => h.Kind == SearchKind.ProjectDoc && h.TargetId == Fake.Id(900));
        Assert.Contains(Search("marsupial").Hits, h => h.Kind == SearchKind.Memory && h.TargetId == Fake.Id(1));
        Assert.Contains(Search("Echidna").Hits, h => h.Kind == SearchKind.Artifact && h.TargetId == Fake.Id(800));
        Assert.Contains(Search("budget").Hits, h => h.Kind == SearchKind.Conversation);
    }

    [Fact]
    public void Supports_phrase_prefix_and_or()
    {
        Assert.NotEmpty(Search("\"small marsupials\"").Hits);
        Assert.Empty(Search("\"marsupials small\"").Hits);
        Assert.NotEmpty(Search("quok*").Hits);
        Assert.Equal(Search("quokkas").Total + Search("budget").Total, Search("quokkas OR budget").Total);
    }

    [Fact]
    public void Filters_by_person_kind_and_date()
    {
        Assert.All(Search("budget", account: Fake.Id(2)).Hits, h => Assert.Equal(Fake.Id(2), h.AccountUuid));
        Assert.Empty(Search("quokkas", account: Fake.Id(2)).Hits);
        Assert.All(Search("quokkas", kinds: [SearchKind.Message]).Hits, h => Assert.Equal(SearchKind.Message, h.Kind));
        Assert.Empty(Search("quokkas", kinds: [SearchKind.Message], from: DateTimeOffset.Parse(Fake.T(500))).Hits);
    }

    [Fact]
    public void Snippets_are_html_encoded_with_marks()
    {
        var hit = Search("script alert").Hits.First(h => h.Kind == SearchKind.Message);

        Assert.DoesNotContain("<script>", hit.SnippetHtml);
        Assert.Contains("&lt;<mark>script</mark>&gt;", hit.SnippetHtml);
        Assert.Contains("<mark>alert</mark>", hit.SnippetHtml);
        Assert.Equal("Sam Sample", hit.UserName);
        Assert.Equal("Script test", hit.Title);
    }

    [Fact]
    public void Hostile_input_never_throws()
    {
        foreach (var q in new[] { "\"", "((((", "a AND", "NEAR(a b)", "title:x", "\u0000", "' OR 1=1 --" })
            Assert.Null(Search(q).Error);
    }
}
