using System.Text;
using System.Text.Json;

namespace ExportViewer.Core.Tests.Fixtures;

/// <summary>
/// Writes a synthetic conversations.json of a target size, streamed so the generator itself stays small in memory.
/// Text is made-up words; a rare marker word appears in about 1 in 1,000 messages so search timing can be measured.
/// </summary>
internal static class LargeExportGenerator
{
    public const string RareWord = "zanthoxylum";

    public static (int Conversations, int Messages) Write(string path, long targetBytes, int messagesPerConversation = 20, int seed = 42)
    {
        var rng = new Random(seed);
        var vocabulary = Enumerable.Range(0, 3000).Select(_ => Word(rng)).ToArray();
        int conversations = 0, messages = 0;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new Utf8JsonWriter(fs);
        w.WriteStartArray();
        while (fs.Length + w.BytesPending < targetBytes)
        {
            var conv = Fake.Id(1_000_000 + conversations);
            var account = Fake.Id(1 + conversations % 25);
            var start = conversations * 7.0;
            w.WriteStartObject();
            w.WriteString("uuid", conv);
            w.WriteString("name", Sentence(rng, vocabulary, 5));
            w.WriteString("summary", "");
            w.WriteString("created_at", Fake.T(start));
            w.WriteString("updated_at", Fake.T(start + messagesPerConversation));
            w.WriteStartObject("account");
            w.WriteString("uuid", account);
            w.WriteEndObject();
            w.WriteStartArray("chat_messages");
            var parent = Fake.Root;
            for (var i = 0; i < messagesPerConversation; i++)
            {
                var id = Fake.Id(10_000_000 + messages);
                var human = i % 2 == 0;
                var text = Sentence(rng, vocabulary, human ? 30 : 380);
                if (rng.Next(1000) == 0) text += " " + RareWord;
                w.WriteStartObject();
                w.WriteString("uuid", id);
                w.WriteString("text", text);
                w.WriteStartArray("content");
                w.WriteStartObject();
                w.WriteString("start_timestamp", Fake.T(start + i));
                w.WriteString("stop_timestamp", Fake.T(start + i));
                w.WriteNull("flags");
                w.WriteString("type", "text");
                w.WriteString("text", text);
                w.WriteStartArray("citations");
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteString("sender", human ? "human" : "assistant");
                w.WriteString("created_at", Fake.T(start + i));
                w.WriteString("updated_at", Fake.T(start + i));
                w.WriteStartArray("attachments");
                w.WriteEndArray();
                w.WriteStartArray("files");
                w.WriteEndArray();
                w.WriteString("parent_message_uuid", parent);
                w.WriteEndObject();
                parent = id;
                messages++;
            }
            w.WriteEndArray();
            w.WriteEndObject();
            conversations++;
            if (w.BytesPending > 1 << 20) w.Flush();
        }
        w.WriteEndArray();
        w.Flush();
        return (conversations, messages);
    }

    private static string Word(Random rng)
    {
        const string consonants = "bcdfghjklmnprstvwz";
        const string vowels = "aeiou";
        var sb = new StringBuilder();
        for (var i = 0; i < rng.Next(2, 5); i++) sb.Append(consonants[rng.Next(consonants.Length)]).Append(vowels[rng.Next(vowels.Length)]);
        return sb.ToString();
    }

    private static string Sentence(Random rng, string[] vocabulary, int words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            // Skewed choice so some words are common and most are rare, like real text.
            var idx = (int)(vocabulary.Length * Math.Pow(rng.NextDouble(), 3));
            if (i > 0) sb.Append(' ');
            sb.Append(vocabulary[idx]);
        }
        return sb.ToString();
    }
}
