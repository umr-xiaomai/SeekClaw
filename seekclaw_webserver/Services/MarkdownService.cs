using System.Net;
using System.Text.RegularExpressions;
using Markdig;

namespace seekclaw_webserver.Services;

public sealed record HeadingOutline(int Level, string Id, string Title);
public sealed record RenderedMarkdown(string Html, IReadOnlyList<HeadingOutline> Outline);

public sealed class MarkdownService
{
    private readonly MarkdownPipeline _pipeline;

    private static readonly Regex HeadingRegex = new(
        @"<h(?<level>[1-4])[^>]*\bid=""(?<id>[^""]+)""[^>]*>(?<title>.*?)</h\k<level>>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CalloutRegex = new(
        @"<blockquote>\s*<p>\s*\[!(?<type>NOTE|TIP|IMPORTANT|WARNING|CAUTION)\](?<content>.*?)</p>\s*(?<rest>.*?)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public MarkdownService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
    }

    public string ToHtml(string? markdown)
    {
        var rawHtml = Markdown.ToHtml(markdown ?? string.Empty, _pipeline);
        rawHtml = rawHtml.Replace("class=\"markdown-alert markdown-alert-", "class=\"markdown-alert vp-custom-block vp-callout vp-callout-");
        rawHtml = rawHtml.Replace("class=\"markdown-alert-title\"", "class=\"markdown-alert-title vp-callout-title\"");
        return TransformCallouts(rawHtml);
    }

    public RenderedMarkdown Render(string? markdown)
    {
        var html = ToHtml(markdown);
        return new RenderedMarkdown(html, ExtractOutline(html));
    }

    private static string TransformCallouts(string html)
    {
        if (!html.Contains("[!"))
        {
            return html;
        }

        return CalloutRegex.Replace(html, match =>
        {
            var type = match.Groups["type"].Value.ToLowerInvariant();
            var firstLine = match.Groups["content"].Value.TrimStart();
            var rest = match.Groups["rest"].Value;

            var title = type switch
            {
                "note" => "Note",
                "tip" => "Tip",
                "important" => "Important",
                "warning" => "Warning",
                "caution" => "Caution",
                _ => char.ToUpperInvariant(type[0]) + type[1..]
            };

            var icon = type switch
            {
                "note" => "bi-info-circle",
                "tip" => "bi-lightbulb",
                "important" => "bi-exclamation-circle",
                "warning" => "bi-exclamation-triangle",
                "caution" => "bi-shield-exclamation",
                _ => "bi-info-circle"
            };

            var contentHtml = string.IsNullOrWhiteSpace(firstLine)
                ? rest
                : $"<p>{firstLine}</p>{rest}";

            return $"""
                <div class="vp-custom-block vp-callout vp-callout-{type}">
                    <p class="vp-callout-title"><i class="bi {icon}"></i> {title}</p>
                    <div class="vp-callout-body">{contentHtml}</div>
                </div>
                """;
        });
    }

    private static IReadOnlyList<HeadingOutline> ExtractOutline(string html)
    {
        var headings = new List<HeadingOutline>();
        foreach (Match match in HeadingRegex.Matches(html))
        {
            var title = WebUtility.HtmlDecode(Regex.Replace(match.Groups["title"].Value, "<[^>]+>", string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            headings.Add(new HeadingOutline(
                int.Parse(match.Groups["level"].Value),
                match.Groups["id"].Value,
                title));
        }

        return headings;
    }
}
