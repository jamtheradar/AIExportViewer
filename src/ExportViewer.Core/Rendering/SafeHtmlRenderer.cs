using System.Net;
using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;

namespace ExportViewer.Core.Rendering;

/// <summary>
/// The only way export content becomes HTML. Markdown is rendered with raw HTML disabled, then sanitised with a tight
/// allowlist. Links lose their href (kept in data-href for the confirm-before-opening flow) and images are replaced
/// by a text placeholder, so nothing remote is ever loaded.
/// </summary>
public static class SafeHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseListExtras()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public static string Markdown(string? markdown, int maxChars = 200_000)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var (text, truncated) = Limit(markdown, maxChars);
        var html = Sanitizer.Sanitize(Markdig.Markdown.ToHtml(text, Pipeline));
        return truncated ? html + TruncatedNote(markdown.Length, maxChars) : html;
    }

    public static string PlainText(string? text, int maxChars = 200_000)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var (t, truncated) = Limit(text, maxChars);
        return "<pre class=\"plain\">" + WebUtility.HtmlEncode(t) + "</pre>" + (truncated ? TruncatedNote(text.Length, maxChars) : "");
    }

    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static (string Text, bool Truncated) Limit(string s, int max) =>
        s.Length > max ? (s[..max], true) : (s, false);

    private static string TruncatedNote(int total, int shown) =>
        $"<p class=\"truncated\">Showing the first {shown:N0} of {total:N0} characters.</p>";

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var t in (string[])["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "strong", "em", "b", "i", "u", "del", "s",
                     "ins", "mark", "sup", "sub", "code", "pre", "blockquote", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "a", "img", "span",
                     "div", "input", "dl", "dt", "dd", "kbd"])
            s.AllowedTags.Add(t);
        s.AllowedAttributes.Clear();
        foreach (var a in (string[])["class", "title", "align", "colspan", "rowspan", "start", "type", "checked", "disabled", "alt", "href", "src"])
            s.AllowedAttributes.Add(a);
        s.AllowedSchemes.Clear();
        foreach (var scheme in (string[])["http", "https", "mailto"]) s.AllowedSchemes.Add(scheme);
        s.AllowedCssProperties.Clear();
        s.AllowedAtRules.Clear();
        s.AllowDataAttributes = false;
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el) return;
            switch (el.TagName)
            {
                case "A":
                    var href = el.GetAttribute("href");
                    el.RemoveAttribute("href");
                    if (IsAllowedUrl(href))
                    {
                        el.SetAttribute("data-href", href!);
                        el.SetAttribute("title", href!);
                        el.SetAttribute("role", "link");
                        el.SetAttribute("tabindex", "0");
                        el.ClassList.Add("ext-link");
                    }
                    break;
                case "IMG":
                    var src = el.GetAttribute("src");
                    var alt = el.GetAttribute("alt");
                    var label = string.IsNullOrWhiteSpace(alt) ? "image" : "image: " + alt;
                    var host = Uri.TryCreate(src, UriKind.Absolute, out var u) ? " from " + u.Host : "";
                    var span = el.Owner!.CreateElement("span");
                    span.ClassList.Add("blocked-image");
                    span.TextContent = $"[{label}{host} — not loaded]";
                    el.Replace(span);
                    break;
                case "INPUT":
                    if (!string.Equals(el.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase)) el.Remove();
                    else el.SetAttribute("disabled", "disabled");
                    break;
            }
        };
        return s;
    }

    public static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto";
}
