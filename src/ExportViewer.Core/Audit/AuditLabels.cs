using ExportViewer.Core.Store;

namespace ExportViewer.Core.Audit;

public static class FindingSource
{
    public const string Sensitive = "sensitive";
    public const string Watchlist = "watchlist";
}

/// <summary>Where a finding was made, in addition to the <see cref="SearchKind"/> values for indexed text.</summary>
public static class FindingLocation
{
    public const string UserDetails = "user";
    public const string SignIn = "login";
}

/// <summary>Plain-English names shared by the screens and the reports.</summary>
public static class AuditLabels
{
    public static string Type(string type) => Enum.TryParse<FindingType>(type, out var t) ? Type(t) : type;

    public static string Type(FindingType t) => t switch
    {
        FindingType.TaxFileNumber => "Tax file number",
        FindingType.BusinessNumber => "ABN",
        FindingType.MedicareNumber => "Medicare number",
        FindingType.CardNumber => "Card number",
        FindingType.BankAccount => "Bank account",
        FindingType.Iban => "IBAN",
        FindingType.Email => "Email address",
        FindingType.Phone => "Phone number",
        FindingType.IpAddress => "IP address",
        FindingType.ApiKey => "API key or secret",
        FindingType.Token => "Login token",
        FindingType.PrivateKey => "Private key",
        FindingType.Password => "Password",
        _ => t.ToString(),
    };

    public static string Location(string location) => location switch
    {
        SearchKind.Message => "Chat message",
        SearchKind.Tool => "Claude's tool activity",
        SearchKind.Attachment => "Attachment",
        SearchKind.Conversation => "Conversation title or summary",
        SearchKind.Project => "Project details",
        SearchKind.ProjectDoc => "Project document",
        SearchKind.Memory => "Memory",
        SearchKind.Artifact => "Artifact or page",
        FindingLocation.UserDetails => "User details",
        FindingLocation.SignIn => "Sign-in record",
        _ => location,
    };

    public static string Confidence(Confidence c) => c switch
    {
        Audit.Confidence.High => "Likely",
        Audit.Confidence.Medium => "Possible",
        _ => "Unsure",
    };
}
