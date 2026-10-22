using System.Text.Json;

namespace ExportViewer.Core.Tests.Fixtures;

/// <summary>
/// A believable but entirely made-up organisation export for looking at the app: a dozen people, a few months of
/// conversations with edits, tool use, attachments, sensitive values, projects, memories and artifacts.
/// Used for screenshots and manual checks instead of a real export.
/// </summary>
internal static class DemoExport
{
    private static readonly (string Name, string Email)[] People =
    [
        ("Avery Lindqvist", "avery.lindqvist@example.test"),
        ("Jordan Okafor", "jordan.okafor@example.test"),
        ("Priya Raman", "priya.raman@example.test"),
        ("Morgan Castellano", "morgan.castellano@example.test"),
        ("Riley Nakamura", "riley.nakamura@example.test"),
        ("Casey Brennan", "casey.brennan@example.test"),
        ("Sam Delacroix", "sam.delacroix@example.test"),
        ("Taylor Mbeki", "taylor.mbeki@example.test"),
        ("Quinn Halvorsen", "quinn.halvorsen@example.test"),
        ("Rowan Achterberg", "rowan.achterberg@example.test"),
        ("Drew Fitzgerald", "drew.fitzgerald@example.test"),
        ("Harper Venkataraman", "harper.venkataraman@example.test"),
    ];

    private static readonly string[] Topics =
    [
        "Draft the Q3 board update", "Rewrite the parental leave policy", "Summarise supplier contract changes",
        "Plan the warehouse move", "Customer complaint reply", "Prepare interview questions for payroll officer",
        "Explain the new GST rules", "Tidy up the onboarding checklist", "Budget variance commentary",
        "Write a job ad for a junior accountant", "Compare three CRM options", "Meeting notes from the ops review",
        "Fix the Excel formula for leave balances", "Draft an email to the landlord", "Training plan for new starters",
        "Risk register update", "Marketing copy for the spring sale", "Translate the safety notice",
        "Respond to a tender question", "Staff survey summary", "Write a performance review",
        "Christmas roster options", "Explain the cash flow forecast", "Privacy policy plain-English version",
    ];

    private static readonly string[] Answers =
    [
        "Here's a draft you can adapt.\n\n## Summary\n\nRevenue is **4% ahead** of budget for the quarter, mainly from the new wholesale accounts. Costs are steady.\n\n- Wholesale: up 11%\n- Retail: flat\n- Online: down 2%\n\nI'd suggest leading with the wholesale result, then covering the online dip and what's being done about it.",
        "Good question. In short, the change only affects invoices issued after 1 July. Anything before that stays under the old rules.\n\nIf you'd like, I can draft a short note for the finance team explaining it.",
        "Here are five questions that test both skills and fit:\n\n1. Walk me through how you'd reconcile a payroll run that's out by a small amount.\n2. Tell me about a time you found an error someone else had made.\n3. How do you keep up with award changes?\n4. What would you do in your first week here?\n5. How do you handle a deadline you can't meet?",
        "I've tightened the wording and removed the repetition. The main changes:\n\n| Section | Change |\n|---|---|\n| Eligibility | Simplified to one sentence |\n| How to apply | Now a numbered list |\n| Pay | Moved up, as it's what people look for first |\n\nLet me know if you'd like a shorter version for the intranet.",
        "That formula is counting blank rows. Try this instead:\n\n```\n=SUMIFS(C:C, A:A, \"Annual\", B:B, \">\"&DATE(2026,1,1))\n```\n\nIt only adds rows where the type is Annual and the date is this year.",
    ];

    public static void Write(string root)
    {
        void Json(string relative, object value)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        void Text(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        var rng = new Random(7);
        var ids = People.Select((_, i) => Fake.Id(1 + i)).ToArray();

        Json("manifest-demo.json", Fake.Manifest(0, "light_metadata-000.zip", "conversations-000.zip", "projects-000.zip", "memories-000.zip", "frames-000.zip"));
        Json("light_metadata-000/users.json", People.Select((p, i) => Fake.User(ids[i], p.Name, p.Email, i == 0 ? "+61400000000" : null)).ToArray());
        Json("light_metadata-000/login_history.json", Fake.Logins(ids.Select((id, i) => Fake.Login(id, i * 600)).ToArray()));

        // Activity is uneven on purpose: a few heavy users, a long tail, and one person with almost nothing.
        var weights = new[] { 14, 11, 9, 7, 6, 5, 4, 3, 3, 2, 2, 1 };
        var conversations = new List<object>();
        var next = 1000;
        var conversationNo = 0;
        for (var person = 0; person < People.Length; person++)
        {
            for (var k = 0; k < weights[person]; k++)
            {
                var start = rng.Next(0, 120 * 24 * 60);
                var topic = Topics[(conversationNo + person) % Topics.Length];
                var convId = Fake.Id(100_000 + conversationNo);
                var messages = new List<object>();
                var parent = Fake.Root;
                var turns = 1 + rng.Next(6);
                var at = (double)start;
                for (var t = 0; t < turns; t++)
                {
                    var humanId = Fake.Id(next++);
                    var question = t == 0 ? $"{topic}. Keep it short and plain." : "Thanks. Can you make it a bit more formal?";
                    object[]? attachments = null;
                    if (conversationNo % 9 == 3 && t == 0)
                        attachments = [Fake.Attachment($"Notes from the meeting. Contact {People[person].Name} on {FakeSecrets.Mobile} if anything is unclear.", "meeting-notes.txt")];
                    if (conversationNo % 13 == 5 && t == 0)
                        question += $" My TFN is {FakeSecrets.Tfn} if you need it for the form.";
                    if (conversationNo % 17 == 8 && t == 0)
                        question += $" The card on file is {FakeSecrets.Card}.";
                    messages.Add(Fake.Message(humanId, parent, "human", at, [Fake.Text(question)], attachments: attachments));

                    // Every so often the person edits their question, leaving a second version.
                    if (conversationNo % 7 == 2 && t == 0)
                    {
                        var editId = Fake.Id(next++);
                        var editAnswer = Fake.Id(next++);
                        messages.Add(Fake.Message(editId, parent, "human", at + 0.2, [Fake.Text(question + " Use Australian spelling.")]));
                        messages.Add(Fake.Message(editAnswer, editId, "assistant", at + 0.5, [Fake.Text(Answers[(t + conversationNo) % Answers.Length])]));
                    }

                    // One conversation in fifteen ends without Claude's reply, so the incomplete warning shows.
                    if (conversationNo % 15 == 14 && t == turns - 1)
                    {
                        parent = humanId;
                        break;
                    }

                    var answerId = Fake.Id(next++);
                    object[] content = conversationNo % 5 == 1 && t == 0
                        ?
                        [
                            Fake.ToolUse($"toolu_{conversationNo}", "web_search", new { query = topic }, "Searching the web"),
                            Fake.ToolResult($"toolu_{conversationNo}", "web_search", [Fake.Knowledge("Guidance for small business", "https://example.org/guide", "Official guidance")]),
                            Fake.Text(Answers[(t + conversationNo) % Answers.Length]),
                        ]
                        : [Fake.Text(Answers[(t + conversationNo) % Answers.Length])];
                    messages.Add(Fake.Message(answerId, humanId, "assistant", at + 1, content));
                    parent = answerId;
                    at += 3 + rng.Next(40);
                }
                conversations.Add(Fake.Conversation(convId, ids[person], topic, start, at + 1, messages.ToArray()));
                conversationNo++;
            }
        }
        Json("conversations-000/conversations.json", conversations);

        Json($"projects-000/projects/{Fake.Id(900)}.json", Fake.Project(Fake.Id(900), "HR policies refresh", ids[1], 5000,
            Fake.Doc(Fake.Id(901), "leave-policy.md", "# Leave policy\n\nStaff may take annual leave after notice to their manager.")));
        Json($"projects-000/projects/{Fake.Id(902)}.json", Fake.Project(Fake.Id(902), "Board reporting", ids[0], 9000,
            Fake.Doc(Fake.Id(903), "template.md", "# Board report template\n\n1. Summary\n2. Financials\n3. Risks")));

        Json($"memories-000/memories/{ids[0]}.json", Fake.Memory(ids[0], 3, "Avery prepares board reports and prefers short, formal answers.", ("/profile.md", "Works in finance.")));
        Json($"memories-000/memories/{ids[2]}.json", Fake.Memory(ids[2], 3, "Priya recruits for the payroll team.", ("/profile.md", "Uses Australian spelling.")));

        Json($"frames-000/artifacts/{Fake.Id(800)}/artifact.json", Fake.Artifact(Fake.Id(800), "page", ids[0], 4000, ("1790000000-aaaa", "Q3 board summary")));
        Text($"frames-000/artifacts/{Fake.Id(800)}/page.md", "# Q3 board summary\n\nRevenue ahead of budget; costs steady.");
        Text($"frames-000/artifacts/{Fake.Id(800)}/comments.json", "[]");
        Json($"frames-000/artifacts/{Fake.Id(801)}/artifact.json", Fake.Artifact(Fake.Id(801), "artifact", ids[3], 7000,
            ("1790000001-bbbb", "Warehouse move timeline v2"), ("1790000000-cccc", "Warehouse move timeline v1")));
        Text($"frames-000/artifacts/{Fake.Id(801)}/versions/1790000001-bbbb/index.html", "<html><body><h1>Warehouse move</h1><p>Week 1: pack. Week 2: move.</p></body></html>");
        Json($"frames-000/artifacts/{Fake.Id(801)}/versions/1790000001-bbbb.files.json", new { files = Array.Empty<object>() });
    }
}
