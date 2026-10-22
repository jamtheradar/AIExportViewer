namespace ExportViewer.Core.Model;

[Flags]
public enum MessageTraits
{
    None = 0,
    EmptyContent = 1 << 0,
    ThinkingOnly = 1 << 1,
    HasToolUse = 1 << 2,
    HasAttachments = 1 << 3,
    HasFiles = 1 << 4,
    TextMismatch = 1 << 5,
    HasThinking = 1 << 6,
    HasInjectedContext = 1 << 7,
    HasToolError = 1 << 8,
}

public sealed record UserRecord(string Uuid, string? FullName, string? Email, string? Phone, string Raw);

public sealed record LoginRecord(
    string? AccountUuid, string? Timestamp, string? Ip, string? Browser, string? Os, string? Device, string? Method, string Raw);

public sealed record ConversationRecord(
    string Uuid,
    string? Name,
    string? Summary,
    string? CreatedAt,
    string? UpdatedAt,
    string? AccountUuid,
    string Raw,
    IReadOnlyList<MessageRecord> Messages);

public sealed record MessageRecord(
    string Uuid,
    string ConversationUuid,
    string? ParentUuid,
    string Sender,
    string? CreatedAt,
    string? UpdatedAt,
    int Seq,
    MessageTraits Traits,
    string Preview,
    string SearchText,
    string ToolText,
    IReadOnlyList<AttachmentText> Attachments,
    string Raw);

public sealed record AttachmentText(string? FileName, string Content);

public sealed record ProjectRecord(
    string Uuid,
    string? Name,
    string? Description,
    bool IsPrivate,
    bool IsStarter,
    string? PromptTemplate,
    string? CreatedAt,
    string? UpdatedAt,
    string? CreatorUuid,
    string? CreatorName,
    string Raw,
    IReadOnlyList<ProjectDocRecord> Docs);

public sealed record ProjectDocRecord(string Uuid, string? Filename, string Content, string? CreatedAt);

public sealed record MemoryRecord(
    string AccountUuid,
    string? SnapshotAt,
    string? ConversationsMemory,
    IReadOnlyList<MemoryFileRecord> Files,
    IReadOnlyList<ProjectMemoryRecord> ProjectMemories,
    string Raw);

public sealed record MemoryFileRecord(string Path, string Content, string? UpdatedAt);

public sealed record ProjectMemoryRecord(string ProjectUuid, string Content);

public sealed record ArtifactRecord(
    string Id,
    string? Kind,
    string? Visibility,
    string? OwnerUuid,
    string? UpdatedAt,
    string? ActiveVersion,
    string? Title,
    string Raw,
    string Folder,
    string? PageMarkdown,
    string? CommentsRaw,
    string? ArtifactCommentsRaw,
    IReadOnlyList<ArtifactVersionRecord> Versions);

public sealed record ArtifactVersionRecord(string VersionId, string? Title, string? CreatedAt, string? Description, string? ContentFolder);

public sealed record GenericRecord(string Collection, string? Key, string? Label, string Raw);

public sealed record ManifestRecord(string? CreatedAt, int? TotalFiles, string Raw);
