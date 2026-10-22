using System.Text.Json;

namespace ExportViewer.Core.Discovery;

internal sealed record SniffResult(
    JsonTokenType Root,
    IReadOnlySet<string> Properties,
    bool Complete,
    bool Empty,
    bool Invalid);

/// <summary>
/// Reads only the start of a JSON file to learn its shape: the root token and the property names of the
/// root object (or of the first element of a root array). Whole files are never parsed here.
/// </summary>
internal static class JsonSniffer
{
    public const int DefaultMaxBytes = 256 * 1024;

    public static SniffResult Sniff(string path, int maxBytes = DefaultMaxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var buffer = new byte[(int)Math.Min(maxBytes, Math.Max(fs.Length, 1))];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0) read += n;
        return Sniff(buffer.AsSpan(0, read), isFinalBlock: fs.Position >= fs.Length);
    }

    public static SniffResult Sniff(ReadOnlySpan<byte> data, bool isFinalBlock)
    {
        var props = new HashSet<string>(StringComparer.Ordinal);
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) data = data[3..];
        if (data.Trim((ReadOnlySpan<byte>)[0x20, 0x09, 0x0A, 0x0D]).IsEmpty)
            return new SniffResult(JsonTokenType.None, props, isFinalBlock, Empty: isFinalBlock, Invalid: false);

        var reader = new Utf8JsonReader(data, isFinalBlock, default);
        var root = JsonTokenType.None;
        var targetDepth = -1;
        try
        {
            while (reader.Read())
            {
                if (root == JsonTokenType.None)
                {
                    root = reader.TokenType;
                    if (root == JsonTokenType.StartObject) targetDepth = 1;
                    else if (root == JsonTokenType.StartArray) targetDepth = 2;
                    else return new SniffResult(root, props, true, false, false);
                    continue;
                }

                if (root == JsonTokenType.StartArray && reader.CurrentDepth == 1)
                {
                    // First element of a root array: only objects carry useful property names.
                    if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) return new SniffResult(root, props, true, false, false);
                    if (reader.TokenType != JsonTokenType.StartObject) return new SniffResult(root, props, true, false, false);
                }
                if (root == JsonTokenType.StartArray && reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndArray)
                    return new SniffResult(root, props, true, false, false);
                if (root == JsonTokenType.StartObject && reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.EndObject)
                    return new SniffResult(root, props, true, false, false);

                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == targetDepth)
                    props.Add(reader.GetString() ?? "");
            }
        }
        catch (JsonException)
        {
            return new SniffResult(root, props, false, false, Invalid: true);
        }
        return new SniffResult(root, props, false, false, false);
    }
}
