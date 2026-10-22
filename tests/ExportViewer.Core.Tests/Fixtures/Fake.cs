using System.Globalization;

namespace ExportViewer.Core.Tests.Fixtures;

/// <summary>
/// Synthetic export records shaped like the real export (docs/export-schema.md). Every value here is made up.
/// Property names are snake_case on purpose: they are serialised as-is.
/// </summary>
internal static class Fake
{
    public const string Root = "00000000-0000-4000-8000-000000000000";
    private static readonly DateTimeOffset Base = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static string Id(int n) => $"10000000-0000-4000-8000-{n:D12}";

    public static string T(double minutes) =>
        Base.AddMinutes(minutes).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static object User(string uuid, string name, string email, string? phone = null) =>
        new { uuid, full_name = name, email_address = email, verified_phone_number = phone };

    public static object Logins(params object[] events) => new { login_events = events };

    public static object Login(string account, double at, string ip = "192.0.2.10") => new
    {
        account_uuid = account,
        timestamp = T(at),
        ip_address = ip,
        user_agent = new { browser_family = "Chrome", browser_version = "150.0.0", os_family = "Windows", os_version = "10", device_family = "Other" },
        method = "sso",
        location_info = (object?)null,
    };

    public static object Conversation(string uuid, string account, string name, double created, double updated, params object[] messages) => new
    {
        uuid,
        name,
        summary = "",
        created_at = T(created),
        updated_at = T(updated),
        account = new { uuid = account },
        chat_messages = messages,
    };

    public static object Message(string uuid, string parent, string sender, double created, object[] content,
        string? text = null, object[]? attachments = null, object[]? files = null) => new
    {
        uuid,
        text = text ?? string.Concat(content.Select(TextOf)),
        content,
        sender,
        created_at = T(created),
        updated_at = T(created),
        attachments = attachments ?? [],
        files = files ?? [],
        parent_message_uuid = parent,
    };

    /// <summary>A human question and assistant answer pair, chained from <paramref name="parent"/>.</summary>
    public static object[] Turn(int humanId, int assistantId, string parent, double at, string question, string answer) =>
    [
        Message(Id(humanId), parent, "human", at, [Text(question)]),
        Message(Id(assistantId), Id(humanId), "assistant", at + 0.5, [Text(answer)]),
    ];

    private static string TextOf(object block) =>
        block.GetType().GetProperty("type")?.GetValue(block) as string == "text"
            ? block.GetType().GetProperty("text")?.GetValue(block) as string ?? ""
            : "\n```\nThis block is not supported on your current device yet.\n```\n";

    public static object Text(string text) => new
    {
        start_timestamp = T(0), stop_timestamp = T(0), flags = (object?)null, type = "text", text, citations = Array.Empty<object>(),
    };

    public static object Thinking(params string[] summaries) => new
    {
        start_timestamp = T(0), stop_timestamp = T(0), flags = (object?)null, type = "thinking", thinking = "",
        summaries = summaries.Select(s => new { summary = s }).ToArray(),
        cut_off = false, truncated = false, hidden = false, thinking_hidden = true, alternative_display_type = (object?)null, signature = (object?)null,
    };

    public static object ToolUse(string id, string name, object input, string? message = null) => new
    {
        start_timestamp = T(0), stop_timestamp = T(0), flags = (object?)null, type = "tool_use", id, name, input, message,
        integration_name = (string?)null, integration_icon_url = "https://example.invalid/icon.png", icon_name = "globe",
        display_content = (object?)null, tool_origin = "first_party",
    };

    public static object ToolResult(string toolUseId, string name, object[] content, bool isError = false) => new
    {
        start_timestamp = T(0), stop_timestamp = T(0), flags = (object?)null, type = "tool_result", tool_use_id = toolUseId, name, content,
        is_error = isError, structured_content = (object?)null, meta = (object?)null, display_content = new { type = "text", text = "shown text" },
    };

    public static object ResultText(string text) => new { type = "text", text };

    public static object Knowledge(string title, string url, string text) => new
    {
        type = "knowledge", title, url, text, is_missing = false, is_citable = true,
        metadata = new { type = "webpage_metadata", site_domain = "example.org", favicon_url = "https://example.org/favicon.ico", site_name = "Example" },
        prompt_context_metadata = new { search_provider = "anthropic", url },
    };

    public static object Injected(string prompt) => new
    {
        start_timestamp = T(0), stop_timestamp = T(0), flags = (object?)null, type = "injected_prompt_block", prompt,
        injection_source = "memory_block_head", initial_turn_only = false, skip_on_truncated_continuation = false,
    };

    public static object Attachment(string content, string name = "") => new { file_name = name, file_size = content.Length, file_type = "txt", extracted_content = content };

    public static object FileRef(string uuid, string name) => new { file_uuid = uuid, file_name = name };

    public static object Project(string uuid, string name, string creator, double updated, params object[] docs) => new
    {
        uuid, name, description = "Synthetic project", is_private = true, is_starter_project = false, prompt_template = "Be brief.",
        created_at = T(0), updated_at = T(updated), creator = new { uuid = creator, full_name = "Creator" }, docs,
    };

    public static object Doc(string uuid, string filename, string content) => new { uuid, filename, content, created_at = T(1) };

    public static object Memory(string account, double updated, string summary, params (string Path, string Content)[] files) => new
    {
        conversations_memory = summary,
        memory_files = files.Select(f => new { path = f.Path, content = f.Content, updated_at = T(updated) }).ToArray(),
        account_uuid = account,
        project_memories = new Dictionary<string, string> { [Id(900)] = "Project memory text" },
    };

    public static object Artifact(string id, string kind, string owner, double updated, params (string Id, string Title)[] versions) => new
    {
        id, kind, visibility = "private",
        versions = versions.Select(v => new { id = v.Id, title = v.Title, created_at = T(updated) }).ToArray(),
        owner_account = owner, updated_at = T(updated), active_version = versions.Length > 0 ? versions[0].Id : null,
    };

    public static object Manifest(double created, params string[] files) => new
    {
        instructions = "Download each file.",
        created_at = T(created),
        total_files = files.Length,
        data_files = files.Select((f, i) => new { batch_index = i, export_url = "https://example.invalid/export/x", category = f.Split('-')[0], part = 0, filename = f }).ToArray(),
        version = "1.0",
    };
}
