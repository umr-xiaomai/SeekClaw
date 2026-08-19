using System.Text;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Services;

public sealed class SkillService(AppDbContext db)
{
    public async Task<List<SkillSummary>> ListAsync(bool includeDisabled, string? typeFilter = null)
    {
        var query = db.Skills.AsNoTracking();
        if (!includeDisabled)
        {
            query = query.Where(skill => skill.Enabled);
        }

        if (string.Equals(typeFilter, "official", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(skill => skill.IsOfficial);
        }
        else if (string.Equals(typeFilter, "community", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(skill => !skill.IsOfficial);
        }

        var skills = await query.ToListAsync();
        return skills
            .OrderByDescending(skill => skill.IsOfficial)
            .ThenByDescending(skill => skill.UpdatedAt)
            .Select(skill => new SkillSummary(
                skill.Id,
                skill.Name,
                skill.Slug,
                skill.Summary,
                skill.Author,
                skill.Version,
                skill.IsOfficial,
                skill.AuthorUserId,
                skill.AuthorUsername,
                skill.Enabled,
                skill.PackageData != null && skill.PackageData.Length > 0,
                skill.UpdatedAt))
            .ToList();
    }

    public async Task<SkillDetailModel?> GetDetailAsync(int id)
    {
        var skill = await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return skill is null ? null : ToDetail(skill);
    }

    public async Task<SkillDetailModel?> GetDetailBySlugAsync(string slug)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var skill = await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == normalized);
        return skill is null ? null : ToDetail(skill);
    }

    public async Task SeedOfficialSkillsAsync()
    {
        if (await db.Skills.AnyAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seeds = new List<Skill>
        {
            new()
            {
                Name = ".NET 10 架构与重构专家",
                Slug = "dotnet-dev",
                Summary = "精通现代 C# 14 / .NET 10.0 清洁架构、依赖注入与异步编程的开发专家技能。",
                Author = "SeekClaw Official",
                Version = "1.0.0",
                IsOfficial = true,
                Enabled = true,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# .NET 10 架构与重构专家 (dotnet-dev)

为 SeekClaw 运行时量身定制的 C# / .NET 10 专业开发助手技能。

## ✨ 特性

- 遵循 SOLID 原则与清洁架构 (Clean Architecture)
- 自动化代码审计与代码坏味道检测
- 推荐使用 .NET 10 新特性与现代 C# 语法（如集合表达式、主构造函数、Native AOT 友好设计）
- 自动编写 xUnit / FluentAssertions 单元测试

## 🚀 安装方式

在终端中执行：
```bash
seekclaw skill install dotnet-dev
```

## 💻 使用方法

在对话中输入：
```bash
seekclaw "重构 UserService 采用依赖注入与仓储模式"
```
""",
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Name = "Git 工作流与语义化提交",
                Slug = "git-flow",
                Summary = "自动化 Git 分支规范、Conventional Commits 提交信息生成与变更日志提炼。",
                Author = "SeekClaw Official",
                Version = "1.0.0",
                IsOfficial = true,
                Enabled = true,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# Git 工作流助手 (git-flow)

规范化 Git 操作，自动生成符合 Conventional Commits 规范的高质量提交信息。

## ✨ 特性

- 自动识别暂存区变更并生成精准 commit 消息
- 支持自动生成语义化版本更新日志 (Changelog.md)
- 解决分支合并冲突建议

## 🚀 安装方式

```bash
seekclaw skill install git-flow
```
""",
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Name = "Web 深度检索与信息提炼",
                Slug = "web-researcher",
                Summary = "结合网络搜索与网页正文提取，进行多源交叉验证与结构化报告输出。",
                Author = "SeekClaw Official",
                Version = "1.0.0",
                IsOfficial = true,
                Enabled = true,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# Web 深度检索助手 (web-researcher)

高效利用 WebSearch 与 WebFetch 工具，进行深度技术调研与资讯总结。

## ✨ 特性

- 自动规划多步搜索关键词
- 智能清洗提取正文核心内容
- 输出带引用来源 Markdown 研究备忘录

## 🚀 安装方式

```bash
seekclaw skill install web-researcher
```
""",
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Name = "代码质量审查与安全卫士",
                Slug = "code-reviewer",
                Summary = "静态代码缺陷分析、OWASP 安全合规检查与性能瓶颈诊断。",
                Author = "SeekClaw Official",
                Version = "1.0.0",
                IsOfficial = true,
                Enabled = true,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# 代码审查卫士 (code-reviewer)

工业级代码审查技能，识别内存泄漏、安全漏洞与并发竞争隐患。

## ✨ 特性

- 检测 SQL 注入、XSS、敏感信息硬编码
- 评估异步方法调用是否可能发生死锁
- 输出格式化的 Review 报告

## 🚀 安装方式

```bash
seekclaw skill install code-reviewer
```
""",
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        db.Skills.AddRange(seeds);
        await db.SaveChangesAsync();
    }

    public async Task<Skill?> GetAsync(int id)
    {
        return await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<Skill> CreateAsync(
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType,
        bool isOfficial = false,
        int? authorUserId = null,
        string? authorUsername = null)
    {
        var slug = await EnsureUniqueSlugAsync(Slugify(input.Slug, input.Name));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skill = new Skill
        {
            Name = input.Name.Trim(),
            Slug = slug,
            Summary = input.Summary.Trim(),
            ReadmeMarkdown = input.ReadmeMarkdown,
            Author = string.IsNullOrWhiteSpace(input.Author) ? (authorUsername ?? "Community") : input.Author.Trim(),
            Version = NormalizeVersion(input.Version),
            Homepage = NormalizeNull(input.Homepage),
            IsOfficial = isOfficial,
            AuthorUserId = authorUserId,
            AuthorUsername = authorUsername,
            Enabled = input.Enabled,
            PackageData = packageData,
            PackageFileName = packageFileName,
            PackageContentType = packageContentType,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Skills.Add(skill);
        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<Skill?> UpdateAsync(
        int id,
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType,
        bool replacePackage,
        bool? isOfficial = null,
        int? currentUserId = null,
        bool isSuperAdmin = false)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return null;
        }

        if (!isSuperAdmin && skill.AuthorUserId.HasValue && skill.AuthorUserId.Value != currentUserId)
        {
            throw new UnauthorizedAccessException("无权修改其他作者发布的技能插件。");
        }

        var slug = await EnsureUniqueSlugAsync(Slugify(input.Slug, input.Name), id);
        skill.Name = input.Name.Trim();
        skill.Slug = slug;
        skill.Summary = input.Summary.Trim();
        skill.ReadmeMarkdown = input.ReadmeMarkdown;
        skill.Author = string.IsNullOrWhiteSpace(input.Author) ? skill.Author : input.Author.Trim();
        skill.Version = NormalizeVersion(input.Version);
        skill.Homepage = NormalizeNull(input.Homepage);
        skill.Enabled = input.Enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (isSuperAdmin && isOfficial.HasValue)
        {
            skill.IsOfficial = isOfficial.Value;
        }

        if (replacePackage)
        {
            skill.PackageData = packageData;
            skill.PackageFileName = packageFileName;
            skill.PackageContentType = packageContentType;
        }

        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<bool> DeleteAsync(int id, int? currentUserId = null, bool isSuperAdmin = false)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return false;
        }

        if (!isSuperAdmin && skill.AuthorUserId.HasValue && skill.AuthorUserId.Value != currentUserId)
        {
            throw new UnauthorizedAccessException("无权删除其他作者发布的技能插件。");
        }

        db.Skills.Remove(skill);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return false;
        }

        skill.Enabled = enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetOfficialAsync(int id, bool isOfficial)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return false;
        }

        skill.IsOfficial = isOfficial;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Skill?> GetPackageAsync(int id)
    {
        return await db.Skills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.PackageData != null);
    }

    private async Task<string> EnsureUniqueSlugAsync(string baseSlug, int? exceptId = null)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (await db.Skills.AnyAsync(x => x.Slug == slug && (!exceptId.HasValue || x.Id != exceptId.Value)))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string Slugify(string requested, string name)
    {
        var source = string.IsNullOrWhiteSpace(requested) ? name : requested;
        var builder = new StringBuilder();
        foreach (var ch in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "skill" : slug;
    }

    private static string NormalizeVersion(string version)
    {
        var value = version.Trim();
        return string.IsNullOrWhiteSpace(value) ? "1.0.0" : value;
    }

    private static string? NormalizeNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static SkillDetailModel ToDetail(Skill skill) =>
        new(
            skill.Id,
            skill.Name,
            skill.Slug,
            skill.Summary,
            skill.ReadmeMarkdown,
            skill.Author,
            skill.Version,
            skill.Homepage,
            skill.IsOfficial,
            skill.AuthorUserId,
            skill.AuthorUsername,
            skill.Enabled,
            skill.PackageData != null && skill.PackageData.Length > 0,
            skill.PackageFileName,
            skill.CreatedAt,
            skill.UpdatedAt);
}



