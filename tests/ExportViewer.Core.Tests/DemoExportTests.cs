using ExportViewer.Core.Tests.Fixtures;

namespace ExportViewer.Core.Tests;

public class DemoExportTests
{
    [Fact]
    public async Task Demo_export_loads_cleanly()
    {
        using var lib = new TempLibrary();
        DemoExport.Write(lib.Root);

        var summary = await lib.LoadAsync();
        var o = lib.Queries.GetOverview();

        Assert.Empty(summary.SkippedFiles);
        Assert.All(summary.Files, f => Assert.Null(f.Note));
        Assert.Equal(12, o.Users);
        Assert.Equal(67, o.Conversations);
        Assert.Equal(2, o.Projects);
        Assert.Equal(2, o.Artifacts);
    }

    /// <summary>
    /// Writes the demo export to the folder in AIEV_DEMO_OUT, for screenshots and manual checks. Does nothing otherwise.
    /// <c>$env:AIEV_DEMO_OUT="C:\temp\demo"; dotnet test --filter "Category=DemoData"</c>
    /// </summary>
    [Fact]
    [Trait("Category", "DemoData")]
    public void Write_demo_export_for_manual_checks()
    {
        if (Environment.GetEnvironmentVariable("AIEV_DEMO_OUT") is not { Length: > 0 } folder) return;
        DemoExport.Write(folder);
    }
}
