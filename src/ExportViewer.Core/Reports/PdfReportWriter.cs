using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace ExportViewer.Core.Reports;

/// <summary>
/// Writes a report as an A4 PDF with the export name, date range and generation time at the top, and page numbers and
/// a confidentiality line on every page. Text is laid out as plain text; nothing in it is interpreted.
/// </summary>
public static class PdfReportWriter
{
    /// <summary>Longest text placed in one table cell; a table row can't break across pages.</summary>
    public const int MaxCell = 1_500;

    /// <summary>Longest text for one message in a transcript. The CSV keeps the full text.</summary>
    public const int MaxTranscriptText = 30_000;

    private static readonly Color Accent = new(0x2F, 0x48, 0x58);
    private static readonly Color Muted = new(0x5A, 0x63, 0x6B);
    private static readonly Color Shade = new(0xF1, 0xF3, 0xF5);
    private static readonly Color NoteShade = new(0xFF, 0xF4, 0xD6);
    private static readonly Color Rule = new(0xCF, 0xD5, 0xDB);

    public static byte[] ToBytes(Report report)
    {
        var fonts = PdfFonts.Instance;
        lock (PdfFonts.RenderLock)
        {
            var doc = Build(report, fonts);
            var renderer = new PdfDocumentRenderer { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Info.Title = report.Title;
            renderer.PdfDocument.Info.Creator = "AI Export Viewer";
            using var ms = new MemoryStream();
            renderer.PdfDocument.Save(ms, false);
            return ms.ToArray();
        }
    }

    public static void Write(Report report, Stream output)
    {
        var bytes = ToBytes(report);
        output.Write(bytes);
    }

    private static Document Build(Report report, PdfFonts fonts)
    {
        var doc = new Document();
        doc.Info.Title = report.Title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Sans;
        normal.Font.Size = 9;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        var section = doc.AddSection();
        var wide = report.Layout == ReportLayout.Table && report.Columns.Sum(c => c.Width) > 7;
        var setup = section.PageSetup;
        setup.PageFormat = PageFormat.A4;
        setup.Orientation = wide ? Orientation.Landscape : Orientation.Portrait;
        setup.TopMargin = Unit.FromCentimeter(2.0);
        setup.BottomMargin = Unit.FromCentimeter(1.8);
        setup.LeftMargin = setup.RightMargin = Unit.FromCentimeter(1.6);
        setup.HeaderDistance = Unit.FromCentimeter(0.8);
        setup.FooterDistance = Unit.FromCentimeter(0.8);
        var usable = (wide ? 29.7 : 21.0) - 3.2;

        var header = section.Headers.Primary.AddParagraph();
        header.Format.Font.Size = 7.5;
        header.Format.Font.Color = Muted;
        Text(header, fonts, $"{report.Title} · {report.Context.ExportName}");

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = Muted;
        Text(footer, fonts, $"Generated {report.Context.GeneratedText} by AI Export Viewer (offline). Confidential: contains staff data. Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var title = section.AddParagraph();
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Accent;
        title.Format.SpaceAfter = Unit.FromPoint(6);
        Text(title, fonts, report.Title);

        var meta = section.AddTable();
        meta.Borders.Visible = false;
        meta.AddColumn(Unit.FromCentimeter(3.4));
        meta.AddColumn(Unit.FromCentimeter(usable - 3.4));
        foreach (var (label, value) in report.HeaderLines())
        {
            var row = meta.AddRow();
            var l = row.Cells[0].AddParagraph();
            l.Format.Font.Color = Muted;
            Text(l, fonts, label);
            Text(row.Cells[1].AddParagraph(), fonts, Cap(value, MaxCell));
        }

        foreach (var note in report.Notes)
        {
            var p = section.AddParagraph();
            p.Format.Shading.Color = NoteShade;
            p.Format.Borders.Distance = Unit.FromPoint(4);
            p.Format.SpaceBefore = Unit.FromPoint(6);
            Text(p, fonts, note);
        }

        var gap = section.AddParagraph();
        gap.Format.SpaceAfter = Unit.FromPoint(6);

        if (report.Rows.Count == 0)
        {
            var p = section.AddParagraph();
            p.Format.Font.Italic = true;
            Text(p, fonts, report.EmptyText);
        }
        else if (report.Layout == ReportLayout.Transcript)
        {
            Transcript(section, fonts, report);
        }
        else
        {
            Grid(section, fonts, report, usable);
        }
        return doc;
    }

    private static void Grid(Section section, PdfFonts fonts, Report report, double usable)
    {
        var table = section.AddTable();
        table.Borders.Color = Rule;
        table.Borders.Width = 0.5;
        table.Format.Font.Size = 8;
        table.LeftPadding = table.RightPadding = Unit.FromPoint(3);
        table.TopPadding = table.BottomPadding = Unit.FromPoint(2);
        var totalWeight = report.Columns.Sum(c => c.Width);
        foreach (var c in report.Columns) table.AddColumn(Unit.FromCentimeter(usable * c.Width / totalWeight));

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Shade;
        head.Format.Font.Bold = true;
        for (var i = 0; i < report.Columns.Count; i++) Text(head.Cells[i].AddParagraph(), fonts, report.Columns[i].Name);

        foreach (var cells in report.Rows)
        {
            var row = table.AddRow();
            for (var i = 0; i < report.Columns.Count; i++)
                Text(row.Cells[i].AddParagraph(), fonts, Cap(i < cells.Length ? cells[i] : "", MaxCell));
        }
    }

    private static void Transcript(Section section, PdfFonts fonts, Report report)
    {
        foreach (var cells in report.Rows)
        {
            string Cell(int i) => i < cells.Length ? cells[i] ?? "" : "";
            var head = section.AddParagraph();
            head.Format.SpaceBefore = Unit.FromPoint(8);
            head.Format.Borders.Top.Width = 0.5;
            head.Format.Borders.Top.Color = Rule;
            head.Format.Borders.Distance = Unit.FromPoint(3);
            head.Format.KeepWithNext = true;
            var who = head.AddFormattedText();
            who.Bold = true;
            Text(who, fonts, Cell(1));
            var when = head.AddFormattedText();
            when.Color = Muted;
            Text(when, fonts, "   " + Cell(0));

            var text = Cap(Cell(2), MaxTranscriptText);
            foreach (var para in text.Split("\n\n"))
            {
                if (para.Trim().Length == 0) continue;
                Text(section.AddParagraph(), fonts, para);
            }

            if (Cell(3).Length > 0)
            {
                var note = section.AddParagraph();
                note.Format.Shading.Color = NoteShade;
                note.Format.Borders.Distance = Unit.FromPoint(3);
                Text(note, fonts, Cell(3));
            }
        }
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + " … [cut short in the PDF; the CSV has the full text]";

    /// <summary>Adds plain text, choosing a font for each run of characters and keeping line breaks.</summary>
    private static void Text(Paragraph p, PdfFonts fonts, string text) => AddRuns(fonts, text, p.AddText, p.AddFormattedText, p.AddLineBreak);

    private static void Text(FormattedText f, PdfFonts fonts, string text) => AddRuns(fonts, text, f.AddText, f.AddFormattedText, f.AddLineBreak);

    private static void AddRuns(PdfFonts fonts, string text, Func<string, MigraDoc.DocumentObjectModel.Text> plain,
        Func<string, FormattedText> formatted, Action lineBreak)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) lineBreak();
            foreach (var (run, family) in fonts.Runs(lines[i]))
            {
                if (family is null) plain(run);
                else formatted(run).Font.Name = family;
            }
        }
    }
}
