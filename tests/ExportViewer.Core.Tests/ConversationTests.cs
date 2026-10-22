using System.Text.Json;
using ExportViewer.Core.Conversations;
using ExportViewer.Core.Model;
using ExportViewer.Core.Parsing;
using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class MessageTreeTests
{
    private static TreeNode N(string id, string parent, double at, int seq) => new(id, parent, Fake.T(at), seq);

    [Fact]
    public void Linear_conversation_is_one_path()
    {
        var tree = MessageTree.Build([N("a", Fake.Root, 0, 0), N("b", "a", 1, 1), N("c", "b", 2, 2)]);

        Assert.Equal(["a", "b", "c"], tree.LatestPath().Select(n => n.Uuid));
        Assert.Equal(0, tree.ForkCount);
        Assert.Empty(tree.OrphanUuids);
    }

    [Fact]
    public void Edited_message_creates_a_fork_and_the_newest_leaf_wins()
    {
        // a → b(answer) → c(question) → d(answer); the user then edited c into c2 → d2.
        var tree = MessageTree.Build([
            N("a", Fake.Root, 0, 0), N("b", "a", 1, 1),
            N("c", "b", 2, 2), N("d", "c", 3, 3),
            N("c2", "b", 4, 4), N("d2", "c2", 5, 5),
        ]);

        Assert.Equal(["a", "b", "c2", "d2"], tree.LatestPath().Select(n => n.Uuid));
        Assert.Equal(1, tree.ForkCount);
        Assert.Equal(["c", "c2"], tree.SiblingsOf("c2").Select(n => n.Uuid));
        Assert.Equal(["a", "b", "c", "d"], tree.Path(new Dictionary<string, string> { ["b"] = "c" }).Select(n => n.Uuid));
    }

    [Fact]
    public void Older_branch_that_continued_longer_still_wins_if_its_leaf_is_newest()
    {
        var tree = MessageTree.Build([
            N("a", Fake.Root, 0, 0),
            N("b1", "a", 1, 1), N("b2", "a", 2, 2),
            N("c1", "b1", 10, 3),
        ]);

        Assert.Equal(["a", "b1", "c1"], tree.LatestPath().Select(n => n.Uuid));
    }

    [Fact]
    public void Edited_first_message_gives_several_roots()
    {
        var tree = MessageTree.Build([N("a", Fake.Root, 0, 0), N("b", "a", 1, 1), N("a2", Fake.Root, 2, 2), N("b2", "a2", 3, 3)]);

        Assert.Equal(2, tree.Roots.Count);
        Assert.Equal(["a2", "b2"], tree.LatestPath().Select(n => n.Uuid));
        Assert.Equal(["a", "b"], tree.Path(new Dictionary<string, string> { [MessageTree.RootSentinel] = "a" }).Select(n => n.Uuid));
    }

    [Fact]
    public void Missing_parent_is_attached_to_the_root_and_reported()
    {
        var tree = MessageTree.Build([N("a", Fake.Root, 0, 0), N("x", "not-in-export", 1, 1)]);

        Assert.Equal(["x"], tree.OrphanUuids);
        Assert.Equal(2, tree.Roots.Count);
    }

    [Fact]
    public void Parent_loop_does_not_hang_or_hide_messages()
    {
        var tree = MessageTree.Build([N("a", "b", 0, 0), N("b", "a", 1, 1)]);

        Assert.NotEmpty(tree.LatestPath());
        Assert.Equal(2, tree.Count);
        Assert.NotEmpty(tree.OrphanUuids);
    }

    [Fact]
    public void Very_long_conversation_does_not_overflow_the_stack()
    {
        var nodes = Enumerable.Range(0, 50_000).Select(i => N("m" + i, i == 0 ? Fake.Root : "m" + (i - 1), i, i));

        Assert.Equal(50_000, MessageTree.Build(nodes).LatestPath().Count);
    }
}

public class IntegrityTests
{
    private static MessageInfo M(string id, string parent, string sender, double at, MessageTraits traits = MessageTraits.None) =>
        new(id, parent, sender, Fake.T(at), (int)at, traits);

    private static IReadOnlyList<IntegrityFlag> Check(string? updated, params MessageInfo[] msgs) =>
        IntegrityChecker.Check(updated, msgs, MessageTree.Build(msgs.Select(m => new TreeNode(m.Uuid, m.ParentUuid, m.CreatedAt, m.Seq))));

    [Fact]
    public void Complete_conversation_has_no_flags() =>
        Assert.Empty(Check(Fake.T(1), M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1)));

    [Fact]
    public void Empty_last_reply_is_high()
    {
        var f = Assert.Single(Check(Fake.T(1), M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1, MessageTraits.EmptyContent)));
        Assert.Equal(("ends_with_empty_reply", Severity.High), (f.Code, f.Severity));
    }

    [Fact]
    public void Thinking_only_last_reply_is_high() =>
        Assert.Equal("ends_with_thinking_only",
            Assert.Single(Check(Fake.T(1), M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1, MessageTraits.ThinkingOnly))).Code);

    [Fact]
    public void Ending_on_the_users_message_is_high() =>
        Assert.Equal("ends_without_reply", Assert.Single(Check(Fake.T(0), M("a", Fake.Root, "human", 0))).Code);

    [Fact]
    public void Empty_reply_mid_conversation_is_medium()
    {
        var flags = Check(Fake.T(3),
            M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1, MessageTraits.EmptyContent),
            M("c", "b", "human", 2), M("d", "c", "assistant", 3));
        var f = Assert.Single(flags);
        Assert.Equal(("empty_reply", Severity.Medium, "b"), (f.Code, f.Severity, f.MessageUuid));
    }

    [Fact]
    public void Empty_reply_on_an_abandoned_branch_is_not_flagged()
    {
        var flags = Check(Fake.T(3),
            M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1, MessageTraits.EmptyContent), M("b2", "a", "assistant", 2));
        Assert.Empty(flags);
    }

    [Fact]
    public void Update_long_after_last_message_is_low_and_short_gaps_are_ignored()
    {
        Assert.Empty(Check(Fake.T(30), M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1)));
        var f = Assert.Single(Check(Fake.T(60 * 24 * 3), M("a", Fake.Root, "human", 0), M("b", "a", "assistant", 1)));
        Assert.Equal(("updated_after_last_message", Severity.Low), (f.Code, f.Severity));
        Assert.Contains("days", f.Detail);
    }

    [Fact]
    public void Conversation_with_no_content_at_all_gets_one_clear_flag()
    {
        var f = Assert.Single(Check(Fake.T(1),
            M("a", Fake.Root, "human", 0, MessageTraits.EmptyContent | MessageTraits.HasFiles), M("b", "a", "assistant", 1, MessageTraits.EmptyContent)));
        Assert.Equal(("no_content", Severity.High), (f.Code, f.Severity));

        Assert.DoesNotContain(Check(Fake.T(1),
            M("a", Fake.Root, "human", 0, MessageTraits.EmptyContent | MessageTraits.HasAttachments), M("b", "a", "assistant", 1, MessageTraits.EmptyContent)),
            x => x.Code == "no_content");
    }

    [Fact]
    public void Empty_user_message_is_medium_unless_it_carries_files()
    {
        Assert.Contains(Check(Fake.T(3), M("a", Fake.Root, "human", 0, MessageTraits.EmptyContent), M("b", "a", "assistant", 1)),
            f => f.Code == "empty_user_message");
        Assert.Empty(Check(Fake.T(3), M("a", Fake.Root, "human", 0, MessageTraits.EmptyContent | MessageTraits.HasFiles), M("b", "a", "assistant", 1)));
    }

    [Fact]
    public void Missing_parent_is_high() =>
        Assert.Contains(Check(Fake.T(1), M("a", Fake.Root, "human", 0), M("b", "gone", "assistant", 1)), f => f.Code == "missing_parent");
}

public class MapperTests
{
    private static MessageRecord Map(object message) =>
        Mappers.Message(JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement, "conv", 0);

    [Fact]
    public void Placeholders_in_text_do_not_count_as_a_mismatch()
    {
        var m = Map(Fake.Message("m", Fake.Root, "assistant", 0,
            [Fake.Text("Before."), Fake.ToolUse("t", "bash_tool", new { command = "ls" }), Fake.ToolResult("t", "bash_tool", [Fake.ResultText("out")]), Fake.Text("After.")]));

        Assert.False(m.Traits.HasFlag(MessageTraits.TextMismatch));
        Assert.True(m.Traits.HasFlag(MessageTraits.HasToolUse));
    }

    [Fact]
    public void Real_disagreement_is_a_mismatch()
    {
        var m = Map(Fake.Message("m", Fake.Root, "assistant", 0, [Fake.Text("Full answer here")], text: "Different words"));

        Assert.True(m.Traits.HasFlag(MessageTraits.TextMismatch));
    }

    [Fact]
    public void Empty_and_thinking_only_replies_are_detected()
    {
        Assert.True(Map(Fake.Message("m", Fake.Root, "assistant", 0, [])).Traits.HasFlag(MessageTraits.EmptyContent));
        Assert.True(Map(Fake.Message("m", Fake.Root, "assistant", 0, [Fake.Thinking("hmm")])).Traits.HasFlag(MessageTraits.ThinkingOnly));
    }

    [Fact]
    public void Search_text_covers_text_and_thinking_and_tool_text_covers_tools_but_not_injected_context()
    {
        var m = Map(Fake.Message("m", Fake.Root, "assistant", 0,
        [
            Fake.Injected("SECRET MEMORY CONTEXT"),
            Fake.Thinking("Considering bilbies"),
            Fake.ToolUse("t", "web_search", new { query = "numbat range" }),
            Fake.ToolResult("t", "web_search", [Fake.Knowledge("Numbat page", "https://example.org/n", "Numbats eat termites")], isError: true),
            Fake.Text("Final answer about numbats"),
        ]));

        Assert.Contains("Final answer about numbats", m.SearchText);
        Assert.Contains("Considering bilbies", m.SearchText);
        Assert.Contains("numbat range", m.ToolText);
        Assert.Contains("Numbats eat termites", m.ToolText);
        Assert.DoesNotContain("SECRET", m.SearchText + m.ToolText);
        Assert.DoesNotContain("favicon", m.ToolText);
        Assert.True(m.Traits.HasFlag(MessageTraits.HasToolError));
        Assert.True(m.Traits.HasFlag(MessageTraits.HasInjectedContext));
        Assert.Equal("Final answer about numbats", m.Preview);
    }

    [Fact]
    public void Timestamps_are_normalised_to_utc()
    {
        Assert.Equal("2026-10-21T08:06:16.356855Z", Json.JsonExt.Utc("2026-10-21T08:06:16.356855+00:00"));
        Assert.Equal("2026-10-21T08:06:16.000000Z", Json.JsonExt.Utc("2026-10-21T18:06:16+10:00"));
        Assert.Null(Json.JsonExt.Utc("not a date"));
    }
}

public class MessageContentTests
{
    [Fact]
    public void Parses_every_block_type_seen_in_the_samples()
    {
        var raw = JsonSerializer.Serialize(Fake.Message("m", Fake.Root, "assistant", 0,
        [
            Fake.Thinking("one", "two"),
            Fake.ToolUse("toolu_1", "web_search", new { query = "q" }, "Searching the web"),
            Fake.ToolResult("toolu_1", "web_search", [Fake.Knowledge("T", "https://example.org", "x"), Fake.ResultText("plain")]),
            Fake.Injected("context"),
            Fake.Text("answer"),
        ], attachments: [Fake.Attachment("pasted", "a.txt")], files: [Fake.FileRef("f1", "pic.png")]));

        var c = MessageContent.Parse(raw);

        Assert.Collection(c.Blocks,
            b => Assert.Equal(["one", "two"], Assert.IsType<ThinkingBlock>(b).Summaries),
            b => Assert.Equal("web_search", Assert.IsType<ToolUseBlock>(b).Name),
            b =>
            {
                var r = Assert.IsType<ToolResultBlock>(b);
                Assert.Equal("toolu_1", r.ToolUseId);
                Assert.Equal(["knowledge", "text"], r.Items.Select(i => i.Type));
                Assert.Equal("shown text", r.Display?.Text);
            },
            b => Assert.Equal("context", Assert.IsType<InjectedBlock>(b).Prompt),
            b => Assert.Equal("answer", Assert.IsType<TextBlock>(b).Text));
        Assert.Equal("pasted", c.Attachments.Single().Content);
        Assert.Equal("pic.png", c.Files.Single().FileName);
    }

    [Theory]
    [InlineData("web_search", """{"query":"red kangaroo"}""", "Searched the web for “red kangaroo”")]
    [InlineData("bash_tool", """{"command":"ls","description":"List the files"}""", "Ran a command: List the files")]
    [InlineData("create_file", """{"path":"/mnt/outputs/report.docx"}""", "Created file report.docx")]
    [InlineData("Claude Docs:update", """{}""", "Used Claude Docs (update)")]
    [InlineData("recipe_display_v0", """{}""", "Showed recipe display")]
    [InlineData("something_new", """{}""", "Used tool “something_new”")]
    public void Tool_steps_are_described_in_plain_english(string name, string input, string expected)
    {
        var block = new ToolUseBlock("t", name, null, null, JsonDocument.Parse(input).RootElement.Clone(), null, null, null, "{}");

        Assert.Equal(expected, ToolStepDescriber.Describe(block));
    }
}
