using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ExportViewer.Core.Loading;

namespace ExportViewer.Core.Tests;

/// <summary>
/// Second line of defence behind the banned-API analyser: our compiled assemblies must not reference network types.
/// Only ExportViewer.Download is exempt, and it may use HTTP only.
/// </summary>
public class PrivacyGuardTests
{
    private static readonly string[] BannedNamespaces = ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic", "System.Net.NetworkInformation"];

    private static readonly string[] BannedTypes =
        ["System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest", "System.Net.FtpWebRequest", "System.Net.HttpListener", "System.Net.Dns"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIExportViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static IEnumerable<object[]> Assemblies()
    {
        yield return [typeof(LibraryLoader).Assembly.Location];
        var appBin = Path.Combine(RepoRoot(), "src", "ExportViewer.App", "bin");
        if (Directory.Exists(appBin))
            foreach (var dll in Directory.EnumerateFiles(appBin, "ExportViewer.App.dll", SearchOption.AllDirectories))
                yield return [dll];
    }

    /// <summary>Third-party PDF assemblies shipped with the app (see Directory.Build.targets for the ones left out).</summary>
    public static IEnumerable<object[]> PdfAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(p => Path.GetFileName(p).StartsWith("PdfSharp", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("MigraDoc", StringComparison.Ordinal))
            .Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Assemblies))]
    [MemberData(nameof(PdfAssemblies))]
    public void Assembly_does_not_reference_network_types(string path) => AssertNoNetworkTypes(path);

    [Fact]
    public void Network_capable_pdf_helper_assemblies_are_left_out_of_the_build()
    {
        Assert.NotEmpty(PdfAssemblies());
        foreach (var name in new[] { "PdfSharp.Cryptography.dll", "PdfSharp.Quality.dll", "PdfSharp.Snippets.dll" })
            Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), $"{name} must not be copied to the output (it uses HttpClient)");
        var appBin = Path.Combine(RepoRoot(), "src", "ExportViewer.App", "bin");
        if (Directory.Exists(appBin))
            Assert.Empty(Directory.EnumerateFiles(appBin, "PdfSharp.Cryptography.dll", SearchOption.AllDirectories));
    }

    private static void AssertNoNetworkTypes(string path)
    {
        var offenders = TypeReferences(path).Where(t => IsBanned(t.Namespace, t.FullName)).Select(t => t.FullName).Distinct().ToList();
        Assert.True(offenders.Count == 0, $"{Path.GetFileName(path)} references network types: {string.Join(", ", offenders)}");
    }

    private static bool IsBanned(string ns, string full) =>
        BannedNamespaces.Any(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal)) || BannedTypes.Contains(full);

    private static List<(string Namespace, string FullName)> TypeReferences(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var list = new List<(string, string)>();
        foreach (var handle in md.TypeReferences)
        {
            var t = md.GetTypeReference(handle);
            var ns = md.GetString(t.Namespace);
            list.Add((ns, ns.Length == 0 ? md.GetString(t.Name) : $"{ns}.{md.GetString(t.Name)}"));
        }
        return list;
    }

    [Fact]
    public void Download_assembly_uses_http_only()
    {
        var path = typeof(ExportViewer.Download.ExportDownloader).Assembly.Location;
        var refs = TypeReferences(path);
        Assert.Contains(refs, r => r.FullName == "System.Net.Http.HttpClient");
        var other = refs.Where(r => IsBanned(r.Namespace, r.FullName) && r.Namespace is not ("System.Net.Http" or "System.Net.Http.Headers")).Select(r => r.FullName).Distinct().ToList();
        Assert.True(other.Count == 0, $"ExportViewer.Download may only use HTTP, but references: {string.Join(", ", other)}");
    }

    [Fact]
    public void Core_does_not_depend_on_the_download_project()
    {
        using var fs = File.OpenRead(typeof(LibraryLoader).Assembly.Location);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        Assert.DoesNotContain(md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)), n => n == "ExportViewer.Download");
    }

    [Fact]
    public void Banned_api_analyser_is_wired_into_every_project()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var banned = File.ReadAllText(Path.Combine(root, "BannedSymbols.txt"));

        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", props);
        Assert.Contains("BannedSymbols.txt", props);
        Assert.Contains("T:System.Net.Http.HttpClient", banned);
        Assert.Contains("T:System.Net.Sockets.Socket", banned);
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            var allowed = Path.GetFileNameWithoutExtension(csproj) == "ExportViewer.Download";
            Assert.True(allowed || !text.Contains("AllowNetworkAccess", StringComparison.Ordinal),
                $"{Path.GetFileName(csproj)} must not opt out of the network ban");
        }
    }

    [Fact]
    public void Gitignore_keeps_real_export_data_out_of_the_repo()
    {
        var gitignore = File.ReadAllLines(Path.Combine(RepoRoot(), ".gitignore")).Select(l => l.Trim()).ToHashSet();

        foreach (var pattern in new[] { "samples/", "*.zip", "*.json", "conversations-*/", "frames-*/", "memories-*/", "projects-*/", "*.db", "*.sqlite" })
            Assert.Contains(pattern, gitignore);
    }
}
