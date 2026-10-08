using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace seekclaw_webserver.Services;

public sealed record DocSummary(string Slug, string Title, string RelativePath);
public sealed record DocGroup(string Title, IReadOnlyList<DocSummary> Items);
public sealed record DocNavSibling(string Slug, string Title, string Link);
public sealed record DocPage(
    string Slug,
    string Title,
    string Markdown,
    string Html,
    IReadOnlyList<HeadingOutline> Outline,
    DocNavSibling? Prev = null,
    DocNavSibling? Next = null);
public sealed record DocSearchResult(string Language, string Slug, string Title, string Snippet);

public sealed class DocService
{
    private readonly string _docsRoot;
    private readonly MarkdownService _markdown;
    private readonly ConcurrentDictionary<string, string> _markdownCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _titleCache = new(StringComparer.OrdinalIgnoreCase);

    // 内存全文倒排与权重检索引擎
    private readonly ConcurrentDictionary<string, List<IndexedDoc>> _searchIndex = new(StringComparer.OrdinalIgnoreCase);

    private sealed record IndexedDoc(
        string Language,
        string Slug,
        string Title,
        string FilePath,
        string Content,
        IReadOnlyList<string> Headings);

    // 标准有序侧边栏清单（与 seekclaw_website 规范同步）
    private static readonly (string GroupZh, string GroupEn, string[] Slugs)[] SidebarGroups =
    [
        ("起步与概览", "Getting Started", ["index", "quickstart", "desktop", "architecture"]),
        ("核心功能与交互", "Core Features", ["providers", "modes", "cli", "tools", "skills", "mcp"]),
        ("实战与最佳实践", "Best Practices", ["prompt-engineering", "local-models", "skill-development"]),
        ("运行时进阶机制", "Advanced Runtime", ["workspace", "verification", "scheduling", "security", "ci-cd", "configuration", "daemon", "faq"])
    ];

    public DocService(IWebHostEnvironment environment, MarkdownService markdown)
    {
        _docsRoot = ResolveDocsRoot(environment);
        _markdown = markdown;
    }

    private static string ResolveDocsRoot(IWebHostEnvironment environment)
    {
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, "Content", "docs"),
            Path.Combine(AppContext.BaseDirectory, "Content", "docs"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seekclaw_webserver", "Content", "docs"),
            Path.Combine(Directory.GetCurrentDirectory(), "seekclaw_webserver", "Content", "docs"),
            Path.Combine(Directory.GetCurrentDirectory(), "Content", "docs")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return Path.Combine(environment.ContentRootPath, "Content", "docs");
    }

    public IReadOnlyList<DocGroup> GetGroups(string language)
    {
        var isEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var allDocs = List(language).ToDictionary(d => d.Slug, StringComparer.OrdinalIgnoreCase);
        var groups = new List<DocGroup>();

        foreach (var (groupZh, groupEn, slugs) in SidebarGroups)
        {
            var items = new List<DocSummary>();
            foreach (var slug in slugs)
            {
                if (allDocs.TryGetValue(slug, out var doc))
                {
                    items.Add(doc);
                }
            }
            if (items.Count > 0)
            {
                groups.Add(new DocGroup(isEn ? groupEn : groupZh, items));
            }
        }

        return groups;
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
                var title = ReadTitleCached(file) ?? Humanize(slug);
                return new DocSummary(slug, title, $"{LanguageCode(language)}/{Path.GetFileName(file)}");
            })
            .OrderBy(doc => doc.Slug == "index" ? 0 : 1)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DocPage? Get(string language, string slug)
    {
        var directory = LanguageDirectory(language);
        var resolvedSlug = string.IsNullOrWhiteSpace(slug) ? "index" : slug;
        var file = ResolveFile(directory, resolvedSlug);
        if (file is null || !File.Exists(file))
        {
            return null;
        }

        var markdown = ReadMarkdown(file);
        var rendered = _markdown.Render(markdown);
        var title = ReadTitleCached(file) ?? Humanize(resolvedSlug);

        // Find Prev and Next docs based on standard manifest
        var isEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var prefix = isEn ? "/en/doc" : "/doc";
        var flatList = SidebarGroups
            .SelectMany(g => g.Slugs)
            .ToList();

        var index = flatList.FindIndex(s => string.Equals(s, resolvedSlug, StringComparison.OrdinalIgnoreCase));
        DocNavSibling? prev = null;
        DocNavSibling? next = null;

        var allDocs = List(language).ToDictionary(d => d.Slug, StringComparer.OrdinalIgnoreCase);

        if (index > 0)
        {
            var prevSlug = flatList[index - 1];
            if (allDocs.TryGetValue(prevSlug, out var prevDoc))
            {
                prev = new DocNavSibling(prevDoc.Slug, prevDoc.Title, $"{prefix}/{prevDoc.Slug}");
            }
        }

        if (index >= 0 && index < flatList.Count - 1)
        {
            var nextSlug = flatList[index + 1];
            if (allDocs.TryGetValue(nextSlug, out var nextDoc))
            {
                next = new DocNavSibling(nextDoc.Slug, nextDoc.Title, $"{prefix}/{nextDoc.Slug}");
            }
        }

        return new DocPage(resolvedSlug, title, markdown, rendered.Html, rendered.Outline, prev, next);
    }

    /// <summary>
    /// 升级版全文检索系统：
    /// 1. 内存倒排与预热分词索引
    /// 2. 多分词与智能匹配
    /// 3. 标题、Slug、层级标题、正文权重评分
    /// 4. 动态最佳相关度摘要切片与高亮
    /// </summary>
    public IReadOnlyList<DocSearchResult> Search(string query, string? languageFilter = null)
    {
        var needle = query.Trim();
        if (needle.Length < 2)
        {
            return Array.Empty<DocSearchResult>();
        }

        var terms = needle.Split([' ', '+', ',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return Array.Empty<DocSearchResult>();
        }

        var languages = string.IsNullOrWhiteSpace(languageFilter)
            ? ["zh", "en"]
            : new[] { LanguageCode(languageFilter) };

        var scoredResults = new List<(DocSearchResult Result, double Score)>();

        foreach (var lang in languages)
        {
            var docs = EnsureIndex(lang);
            foreach (var doc in docs)
            {
                double score = 0;
                int matchedTerms = 0;
                int bestSnippetOffset = -1;

                foreach (var term in terms)
                {
                    bool termMatched = false;

                    // 1. 标题完全包含 (权重最高 +100)
                    if (doc.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 100;
                        termMatched = true;
                    }

                    // 2. Slug 标识匹配 (+80)
                    if (doc.Slug.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 80;
                        termMatched = true;
                    }

                    // 3. 章节标题匹配 (+40)
                    foreach (var heading in doc.Headings)
                    {
                        if (heading.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 40;
                            termMatched = true;
                            break;
                        }
                    }

                    // 4. 正文词频统计与首现位置计算 (+5 每次出现，上限 40)
                    var count = 0;
                    var offset = 0;
                    while ((offset = doc.Content.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        if (bestSnippetOffset < 0)
                        {
                            bestSnippetOffset = offset;
                        }
                        count++;
                        offset += term.Length;
                        if (count >= 8) break;
                    }

                    if (count > 0)
                    {
                        score += count * 5;
                        termMatched = true;
                    }

                    if (termMatched)
                    {
                        matchedTerms++;
                    }
                }

                // 多关键词全部命中时赋予额外奖赏
                if (matchedTerms == terms.Length && terms.Length > 1)
                {
                    score += 50;
                }

                if (score > 0)
                {
                    var snippet = ExtractSnippet(doc.Content, needle, bestSnippetOffset >= 0 ? bestSnippetOffset : 0);
                    scoredResults.Add((new DocSearchResult(doc.Language, doc.Slug, doc.Title, snippet), score));
                }
            }
        }

        return scoredResults
            .OrderByDescending(x => x.Score)
            .Take(40)
            .Select(x => x.Result)
            .ToList();
    }

    private List<IndexedDoc> EnsureIndex(string language)
    {
        return _searchIndex.GetOrAdd(language, lang =>
        {
            var directory = LanguageDirectory(lang);
            var list = new List<IndexedDoc>();
            if (!Directory.Exists(directory))
            {
                return list;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                var content = ReadMarkdown(file);
                var title = ReadTitleCached(file) ?? Humanize(Path.GetFileNameWithoutExtension(file));
                var slug = Path.GetFileNameWithoutExtension(file);

                var headings = new List<string>();
                foreach (var line in content.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("## ") || trimmed.StartsWith("### "))
                    {
                        headings.Add(trimmed.TrimStart('#').Trim());
                    }
                }

                list.Add(new IndexedDoc(lang, slug, title, file, content, headings));
            }

            return list;
        });
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

    private string ReadMarkdown(string file)
    {
        return _markdownCache.GetOrAdd(file, File.ReadAllText);
    }

    private string? ReadTitleCached(string file)
    {
        return _titleCache.GetOrAdd(file, path => ReadTitle(path));
    }

    private static string ExtractSnippet(string content, string query, int targetIndex = -1, int radius = 80)
    {
        var normalized = content.Replace("\r", " ").Replace("\n", " ");
        var index = targetIndex >= 0 ? targetIndex : normalized.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = 0;
        }

        var start = Math.Max(0, index - radius);
        var length = Math.Min(normalized.Length - start, radius * 2 + Math.Max(query.Length, 10));
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
