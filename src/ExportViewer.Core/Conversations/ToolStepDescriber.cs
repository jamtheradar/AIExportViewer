using System.Text.Json;
using ExportViewer.Core.Json;

namespace ExportViewer.Core.Conversations;

/// <summary>One-line, plain-English descriptions of Claude's tool steps for the Basic view.</summary>
public static class ToolStepDescriber
{
    public static string Describe(ToolUseBlock t)
    {
        var input = t.Input ?? default;
        string? In(string name) => input.ValueKind == JsonValueKind.Object ? input.Str(name) : null;
        string Q(string? s) => s is null ? "" : $" for “{Shorten(s, 80)}”";

        var name = t.Name ?? "";
        var described = name switch
        {
            "web_search" or "web_search_fast" => $"Searched the web{Q(In("query"))}",
            "web_fetch" => $"Read a web page{(In("url") is { } u ? ": " + Shorten(u, 90) : "")}",
            "image_search" => $"Searched for images{Q(In("query"))}",
            "places_search" => "Searched for places",
            "conversation_search" => $"Searched the user's past conversations{Q(In("query"))}",
            "read_conversation" => "Read a past conversation",
            "memory_read" => "Read the user's saved memory",
            "bash_tool" => In("description") is { Length: > 0 } d ? $"Ran a command: {Shorten(d, 90)}" : "Ran a command",
            "view" => $"Looked at {FileName(In("path")) ?? "a file"}",
            "create_file" => $"Created file {FileName(In("path")) ?? ""}".TrimEnd(),
            "str_replace" => $"Edited file {FileName(In("path")) ?? ""}".TrimEnd(),
            "present_files" => "Shared files with the user",
            "ask_user_input_v0" => "Asked the user a question",
            "message_compose_v1" => "Drafted a message",
            "user_time_v0" => "Checked the current time",
            "tool_search" or "search_mcp_registry" or "search_skills" => "Looked for available tools",
            "Artifact" => "Worked on a published artifact",
            _ => null,
        };
        if (described is not null) return described;
        if (name.EndsWith("_v0", StringComparison.Ordinal) || name.EndsWith("_v1", StringComparison.Ordinal))
            return $"Showed {Humanise(name[..^3])}";
        if (name.Contains(':'))
        {
            var parts = name.Split(':', 2);
            return $"Used {parts[0]} ({Humanise(parts[1])})";
        }
        if (t.Message is { Length: > 0 } msg && !msg.StartsWith("Generating", StringComparison.Ordinal)) return Shorten(msg, 100);
        return name.Length > 0 ? $"Used tool “{name}”" : "Used a tool";
    }

    public static string Summarise(ToolResultBlock? r)
    {
        if (r is null) return "No result recorded in the export.";
        if (r.IsError) return "This step failed.";
        if (r.Name == "web_fetch") return "Page read";
        var knowledge = r.Items.Count(i => i.Type == "knowledge");
        if (knowledge > 0) return $"{knowledge} web result{(knowledge == 1 ? "" : "s")}";
        var files = r.Items.Count(i => i.Type == "local_resource");
        if (files > 0) return $"{files} file{(files == 1 ? "" : "s")} (not included in export)";
        var images = r.Items.Count(i => i.Type is "image" or "image_gallery");
        if (images > 0) return $"{images} image{(images == 1 ? "" : "s")} (not included in export)";
        return "Done";
    }

    private static string? FileName(string? path) =>
        string.IsNullOrEmpty(path) ? null : path.TrimEnd('/').Split('/').LastOrDefault();

    private static string Humanise(string s) => s.Replace('_', ' ').Trim();

    internal static string Shorten(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}
