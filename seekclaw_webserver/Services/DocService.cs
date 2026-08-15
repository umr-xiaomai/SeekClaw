namespace seekclaw_webserver.Services;

public sealed record DocSummary(string Slug, string Title, string RelativePath);
public sealed record DocPage(string Slug, string Title, string Markdown, string Html, IReadOnlyList<HeadingOutline> Outline);
public sealed record DocSearchResult(string Language, string Slug, string Title, string Snippet);

public sealed class DocService
{
    private readonly string _docsRoot;
    private readonly MarkdownService _markdown;

    public DocService(IWebHostEnvironment environment, MarkdownService markdown)
    {
        _docsRoot = Path.Combine(environment.ContentRootPath, "Content", "docs");
        _markdown = markdown;
    }

    public IReadOnlyList<DocSummary> List(string language)
    {
        var directory = LanguageDirectory(language);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<DocSummary>();
        }

        return Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(file =>
            {
                var slug = Path.GetFileNameWithoutExtension(file);
                var title = ReadTitle(file) ?? Humanize(slug);
                return new DocSummary(slug, title, $"{LanguageCode(language)}/{Path.GetFileName(file)}");
            })
            .OrderBy(doc => doc.Slug == "index" ? 0 : 1)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DocPage? Get(string language, string slug)
    {
        var directory = LanguageDirectory(language);
        var file = ResolveFile(directory, slug);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        var markdown = File.ReadAllText(file);
        var rendered = _markdown.Render(markdown);
        return new DocPage(slug, ReadTitle(file) ?? Humanize(slug), markdown, rendered.Html, rendered.Outline);
    }

    public IReadOnlyList<DocSearchResult> Search(string query)
    {
        var needle = query.Trim();
        if (needle.Length < 2)
        {
            return Array.Empty<DocSearchResult>();
        }

        var results = new List<DocSearchResult>();
        foreach (var language in new[] { "zh", "en" })
        {
            var directory = LanguageDirectory(language);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                var markdown = File.ReadAllText(file);
                var lower = markdown.ToLowerInvariant();
                var lowerNeedle = needle.ToLowerInvariant();
                if (!lower.Contains(lowerNeedle, StringComparison.Ordinal))
                {
                    continue;
                }

                var slug = Path.GetFileNameWithoutExtension(file);
                results.Add(new DocSearchResult(
                    language,
                    slug,
                    ReadTitle(file) ?? Humanize(slug),
                    ExtractSnippet(markdown, needle)));

                if (results.Count >= 40)
                {
                    return results;
                }
            }
        }

        return results;
    }

    private string LanguageDirectory(string language) =>
        Path.Combine(_docsRoot, LanguageCode(language));

    private static string LanguageCode(string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

    private static string? ResolveFile(string directory, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(slug))
        {
            return null;
        }

        var file = Path.Combine(directory, $"{slug}.md");
        var fullPath = Path.GetFullPath(file);
        var directoryFullPath = Path.GetFullPath(directory);

        return fullPath.StartsWith(directoryFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static string? ReadTitle(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var title = line[2..].Trim();
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                break;
            }
        }

        return null;
    }

    private static string ExtractSnippet(string content, string query, int radius = 80)
    {
        var normalized = content.Replace("\r", " ").Replace("\n", " ");
        var index = normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = 0;
        }

        var start = Math.Max(0, index - radius);
        var length = Math.Min(normalized.Length - start, radius * 2 + query.Length);
        var snippet = normalized.Substring(start, length).Trim();
        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < normalized.Length)
        {
            snippet += "…";
        }

        return snippet;
    }

    private static string Humanize(string slug)
    {
        var words = slug.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? slug
            : string.Join(' ', words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
