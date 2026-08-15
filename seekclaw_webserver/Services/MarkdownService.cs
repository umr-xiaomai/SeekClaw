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

    public MarkdownService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
    }

    public string ToHtml(string? markdown) => Markdown.ToHtml(markdown ?? string.Empty, _pipeline);

    public RenderedMarkdown Render(string? markdown)
    {
        var html = ToHtml(markdown);
        return new RenderedMarkdown(html, ExtractOutline(html));
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
