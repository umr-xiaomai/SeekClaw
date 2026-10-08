using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Services;

public sealed class SkillService(AppDbContext db)
{
    public const long MaxPackageSizeBytes = 10 * 1024 * 1024; // 10MB 硬限制
    public const long MaxZipUncompressedBytes = 50 * 1024 * 1024; // 50MB 解压膨胀上限

    private static readonly HashSet<string> AllowedPackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".json", ".md", ".txt"
    };

    private static readonly HashSet<string> DangerousFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bat", ".cmd", ".sh", ".vbs", ".ps1", ".scr", ".msi", ".jar", ".com"
    };

    public async Task<List<SkillSummary>> ListAsync(
        bool includeDisabled,
        string? typeFilter = null,
        string? search = null,
        string? reviewStatus = null,
        string? sortBy = null)
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

        if (!string.IsNullOrWhiteSpace(reviewStatus) && !string.Equals(reviewStatus, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(skill => skill.ReviewStatus == reviewStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(skill =>
                skill.Name.Contains(term) ||
                skill.Slug.Contains(term) ||
                skill.Summary.Contains(term));
        }

        query = sortBy?.ToLowerInvariant() switch
        {
            "downloads" or "popular" => query.OrderByDescending(s => s.DownloadCount).ThenByDescending(s => s.UpdatedAt),
            "views" => query.OrderByDescending(s => s.ViewCount).ThenByDescending(s => s.UpdatedAt),
            "updated" or "recent" => query.OrderByDescending(s => s.UpdatedAt),
            _ => query.OrderByDescending(s => s.IsOfficial).ThenByDescending(s => s.DownloadCount).ThenByDescending(s => s.UpdatedAt)
        };

        return await query
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
                skill.ReviewStatus,
                skill.ReviewComment,
                skill.DownloadCount,
                skill.ViewCount,
                skill.Versions.Count,
                (skill.PackageData != null && skill.PackageData.Length > 0) || !string.IsNullOrWhiteSpace(skill.ReadmeMarkdown) || !string.IsNullOrWhiteSpace(skill.Summary),
                skill.UpdatedAt,
                skill.Homepage))
            .ToListAsync();
    }

    public async Task<List<SkillSummary>> ListUserSkillsAsync(int userId)
    {
        return await db.Skills.AsNoTracking()
            .Where(skill => skill.AuthorUserId == userId)
            .OrderByDescending(skill => skill.UpdatedAt)
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
                skill.ReviewStatus,
                skill.ReviewComment,
                skill.DownloadCount,
                skill.ViewCount,
                skill.Versions.Count,
                (skill.PackageData != null && skill.PackageData.Length > 0) || !string.IsNullOrWhiteSpace(skill.ReadmeMarkdown) || !string.IsNullOrWhiteSpace(skill.Summary),
                skill.UpdatedAt,
                skill.Homepage))
            .ToListAsync();
    }

    public async Task<SkillDetailModel?> GetDetailAsync(int id)
    {
        var skill = await db.Skills
            .Include(x => x.Versions)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id);
        return skill is null ? null : ToDetail(skill);
    }

    public async Task<SkillDetailModel?> GetDetailBySlugAsync(string slug)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var skill = await db.Skills
            .Include(x => x.Versions)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == normalized);
        return skill is null ? null : ToDetail(skill);
    }

    public async Task IncrementViewAsync(int skillId)
    {
        await db.Skills
            .Where(x => x.Id == skillId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewCount, x => x.ViewCount + 1));
    }

    public async Task IncrementDownloadAsync(int skillId, string? version = null)
    {
        await db.Skills
            .Where(x => x.Id == skillId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DownloadCount, x => x.DownloadCount + 1));

        if (!string.IsNullOrWhiteSpace(version))
        {
            var normalizedVer = version.Trim();
            await db.SkillVersions
                .Where(x => x.SkillId == skillId && x.Version == normalizedVer)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DownloadCount, x => x.DownloadCount + 1));
        }
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
                ReviewStatus = SkillReviewStatus.Approved,
                DownloadCount = 128,
                ViewCount = 650,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# .NET 10 架构与重构专家 (dotnet-dev)

为 SeekClaw 运行时量身定制的 C# / .NET 10 专业开发助手技能。

## ✨ 特性

- 遵循 SOLID 原则与清洁架构 (Clean Architecture)
- 自动化代码审计与坏味道检测
- 推荐使用 .NET 10 与现代 C# 语法（如集合表达式、主构造函数、Native AOT 友好设计）
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
                ReviewStatus = SkillReviewStatus.Approved,
                DownloadCount = 96,
                ViewCount = 420,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# Git 工作流助手 (git-flow)

规范化 Git 操作，自动生成符合 Conventional Commits 规范的高质量提交信息。

## ✨ 特性

- 自动识别暂存区变更并生成精准 commit 消息
- 支持自动生成语义化版本更新日志 (Changelog.md)
- 交互式分支管理指引与冲突解决策略

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
                Name = "Docker 与云原生容器编排",
                Slug = "docker-cloud",
                Summary = "自动化多阶段 Dockerfile 编写、docker-compose 服务编排与容器镜像精简专家。",
                Author = "SeekClaw Official",
                Version = "1.0.0",
                IsOfficial = true,
                Enabled = true,
                ReviewStatus = SkillReviewStatus.Approved,
                DownloadCount = 85,
                ViewCount = 380,
                Homepage = "https://github.com/umr-xiaomai/SeekClaw",
                ReadmeMarkdown = """
# Docker 与云原生容器编排 (docker-cloud)

极速构建现代微服务容器化流水线。

## ✨ 特性

- .NET 10 与 Node.js 最佳多阶段构建实践（极小化镜像体积）
- 自动生成具备健康检查机制的 docker-compose.yml
- 非 root 用户最小特权安全运行基线

## 🚀 安装方式

```bash
seekclaw skill install docker-cloud
```
""",
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        foreach (var skill in seeds)
        {
            var zipData = GenerateSkillZipPackage(skill);
            skill.PackageData = zipData;
            skill.PackageFileName = $"{skill.Slug}.zip";
            skill.PackageContentType = "application/zip";

            skill.Versions.Add(new SkillVersion
            {
                Version = skill.Version,
                Changelog = "官方初始版本发布",
                PackageData = zipData,
                PackageFileName = $"{skill.Slug}.zip",
                PackageContentType = "application/zip",
                DownloadCount = skill.DownloadCount,
                CreatedAt = now
            });

            db.Skills.Add(skill);
        }

        await db.SaveChangesAsync();
    }

    public async Task<Skill> CreateAsync(
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType,
        bool isOfficial,
        int? authorUserId,
        string? authorUsername)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("技能名称不能为空。", nameof(input));
        }

        var contentType = ValidateAndSanitizePackage(packageData, packageFileName);
        var baseSlug = Slugify(input.Slug, input.Name);
        var uniqueSlug = await EnsureUniqueSlugAsync(baseSlug);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 审核流规则：官方技能或由超级管理员提交的技能直接通过；普通社区用户提交则进入待审核状态
        var reviewStatus = isOfficial ? SkillReviewStatus.Approved : SkillReviewStatus.Pending;

        var skill = new Skill
        {
            Name = input.Name.Trim(),
            Slug = uniqueSlug,
            Summary = input.Summary?.Trim() ?? string.Empty,
            ReadmeMarkdown = input.ReadmeMarkdown?.Trim() ?? string.Empty,
            Author = string.IsNullOrWhiteSpace(input.Author) ? (authorUsername ?? "SeekClaw Community") : input.Author.Trim(),
            Version = NormalizeVersion(input.Version),
            Homepage = NormalizeNull(input.Homepage),
            IsOfficial = isOfficial,
            AuthorUserId = authorUserId,
            AuthorUsername = authorUsername,
            PackageFileName = packageFileName,
            PackageContentType = contentType,
            PackageData = packageData,
            Enabled = input.Enabled,
            ReviewStatus = reviewStatus,
            CreatedAt = now,
            UpdatedAt = now
        };

        // 自动初始化第一代版本历史 (Initial Version)
        skill.Versions.Add(new SkillVersion
        {
            Version = skill.Version,
            Changelog = string.IsNullOrWhiteSpace(input.Changelog) ? "初始版本发布" : input.Changelog.Trim(),
            PackageFileName = packageFileName,
            PackageContentType = contentType,
            PackageData = packageData,
            CreatedAt = now
        });

        db.Skills.Add(skill);
        await db.SaveChangesAsync();

        return skill;
    }

    public async Task<SkillVersion> AddVersionAsync(
        int skillId,
        SkillVersionInput input,
        byte[]? packageData,
        string? packageFileName,
        bool isSuperAdmin,
        int? currentUserId)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skill = await db.Skills.Include(s => s.Versions).SingleOrDefaultAsync(x => x.Id == skillId);
        if (skill is null)
        {
            throw new InvalidOperationException("未找到要更新版本的技能。");
        }

        if (!isSuperAdmin && skill.AuthorUserId != currentUserId)
        {
            throw new UnauthorizedAccessException("您没有权限为该技能发布新版本。");
        }

        var newVersion = NormalizeVersion(input.Version);
        if (skill.Versions.Any(v => v.Version.Equals(newVersion, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"版本号 '{newVersion}' 已经存在，请指定更新的版本号。");
        }

        var contentType = ValidateAndSanitizePackage(packageData, packageFileName);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var versionEntity = new SkillVersion
        {
            SkillId = skill.Id,
            Version = newVersion,
            Changelog = string.IsNullOrWhiteSpace(input.Changelog) ? "版本更新" : input.Changelog.Trim(),
            PackageFileName = packageFileName,
            PackageContentType = contentType,
            PackageData = packageData,
            CreatedAt = now
        };

        db.SkillVersions.Add(versionEntity);

        // 同步技能主表的当前版本信息
        skill.Version = newVersion;
        skill.UpdatedAt = now;
        if (packageData != null)
        {
            skill.PackageData = packageData;
            skill.PackageFileName = packageFileName;
            skill.PackageContentType = contentType;
        }

        // 普通用户发布新版本后，将审核状态重置为待审核
        if (!isSuperAdmin && !skill.IsOfficial)
        {
            skill.ReviewStatus = SkillReviewStatus.Pending;
            skill.ReviewComment = null;
        }

        await db.SaveChangesAsync();
        return versionEntity;
    }

    public async Task ReviewSkillAsync(int skillId, string newStatus, string? comment)
    {
        if (!SkillReviewStatus.IsValid(newStatus))
        {
            throw new ArgumentException("无效的审核状态。", nameof(newStatus));
        }

        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == skillId);
        if (skill is null)
        {
            throw new InvalidOperationException("未找到目标技能。");
        }

        skill.ReviewStatus = newStatus;
        skill.ReviewComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (newStatus == SkillReviewStatus.Approved)
        {
            skill.Enabled = true;
        }

        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
    }

    public async Task<bool> UpdateUserSkillAsync(int skillId, int userId, UserSkillUpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == skillId && x.AuthorUserId == userId);
        if (skill is null)
        {
            return false;
        }

        skill.Name = string.IsNullOrWhiteSpace(input.Name) ? skill.Name : input.Name.Trim();
        skill.Summary = input.Summary?.Trim() ?? string.Empty;
        skill.ReadmeMarkdown = input.ReadmeMarkdown?.Trim() ?? string.Empty;
        skill.Homepage = NormalizeNull(input.Homepage);
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Skill?> UpdateAsync(
        int id,
        SkillInput input,
        byte[]? packageData,
        string? packageFileName,
        string? packageContentType)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null)
        {
            return null;
        }

        var contentType = ValidateAndSanitizePackage(packageData, packageFileName);

        if (!string.Equals(skill.Slug, input.Slug, StringComparison.OrdinalIgnoreCase))
        {
            var newSlug = Slugify(input.Slug, input.Name);
            skill.Slug = await EnsureUniqueSlugAsync(newSlug, exceptId: skill.Id);
        }

        skill.Name = input.Name.Trim();
        skill.Summary = input.Summary?.Trim() ?? string.Empty;
        skill.ReadmeMarkdown = input.ReadmeMarkdown?.Trim() ?? string.Empty;
        skill.Author = string.IsNullOrWhiteSpace(input.Author) ? skill.Author : input.Author.Trim();
        skill.Version = NormalizeVersion(input.Version);
        skill.Homepage = NormalizeNull(input.Homepage);
        skill.IsOfficial = input.IsOfficial;
        skill.Enabled = input.Enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (packageData is not null)
        {
            skill.PackageData = packageData;
            skill.PackageFileName = packageFileName;
            skill.PackageContentType = contentType;
        }

        await db.SaveChangesAsync();
        return skill;
    }

    public async Task<bool> SetOfficialAsync(int id, bool isOfficial)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null) return false;

        skill.IsOfficial = isOfficial;
        if (isOfficial)
        {
            skill.ReviewStatus = SkillReviewStatus.Approved;
        }
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null) return false;

        skill.Enabled = enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var skill = await db.Skills.SingleOrDefaultAsync(x => x.Id == id);
        if (skill is null) return false;

        db.Skills.Remove(skill);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<SkillPackageResult?> GetDownloadPackageAsync(string slugOrId, string? version = null)
    {
        Skill? skill = null;
        if (int.TryParse(slugOrId, out var id))
        {
            skill = await db.Skills.Include(s => s.Versions).SingleOrDefaultAsync(x => x.Id == id);
        }

        if (skill is null)
        {
            var normalizedSlug = slugOrId.Trim().ToLowerInvariant();
            skill = await db.Skills.Include(s => s.Versions).SingleOrDefaultAsync(x => x.Slug == normalizedSlug);
        }

        if (skill is null || !skill.Enabled || skill.ReviewStatus != SkillReviewStatus.Approved)
        {
            return null;
        }

        // 累积下载次数
        await IncrementDownloadAsync(skill.Id, version);

        // 如果请求了特定版本
        if (!string.IsNullOrWhiteSpace(version))
        {
            var targetVer = version.Trim();
            var verEntity = skill.Versions.FirstOrDefault(v => v.Version.Equals(targetVer, StringComparison.OrdinalIgnoreCase));
            if (verEntity != null && verEntity.PackageData != null && verEntity.PackageData.Length > 0)
            {
                return new SkillPackageResult(
                    verEntity.PackageData,
                    verEntity.PackageContentType ?? "application/zip",
                    verEntity.PackageFileName ?? $"{skill.Slug}-{verEntity.Version}.zip");
            }
        }

        // 默认返回当前主包
        if (skill.PackageData is not null && skill.PackageData.Length > 0)
        {
            return new SkillPackageResult(
                skill.PackageData,
                skill.PackageContentType ?? "application/octet-stream",
                skill.PackageFileName ?? $"{skill.Slug}.zip");
        }

        // 动态生成标准 Zip 归档
        var packageBytes = GenerateSkillZipPackage(skill);
        return new SkillPackageResult(packageBytes, "application/zip", $"{skill.Slug}.zip");
    }

    private static byte[] GenerateSkillZipPackage(Skill skill)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var promptContent = !string.IsNullOrWhiteSpace(skill.ReadmeMarkdown)
                ? skill.ReadmeMarkdown
                : skill.Summary;
            var promptEntry = archive.CreateEntry("prompt.txt", CompressionLevel.Optimal);
            using (var entryStream = promptEntry.Open())
            using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
            {
                writer.Write(promptContent);
            }

            var yamlContent = $"""
            name: "{EscapeYaml(skill.Slug)}"
            description: "{EscapeYaml(skill.Summary)}"
            version: "{EscapeYaml(skill.Version)}"
            prompt: "prompt.txt"
            """;
            var yamlEntry = archive.CreateEntry("skill.yaml", CompressionLevel.Optimal);
            using (var entryStream = yamlEntry.Open())
            using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
            {
                writer.Write(yamlContent);
            }

            if (!string.IsNullOrWhiteSpace(skill.ReadmeMarkdown))
            {
                var readmeEntry = archive.CreateEntry("README.md", CompressionLevel.Optimal);
                using (var entryStream = readmeEntry.Open())
                using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                {
                    writer.Write(skill.ReadmeMarkdown);
                }
            }
        }

        return memoryStream.ToArray();
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");

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

    /// <summary>
    /// 服务端上传包硬限制与深度安全校验：
    /// 1. 10MB 硬上限拦截
    /// 2. 文件扩展名安全白名单
    /// 3. Zip Slip（目录穿越）防御
    /// 4. 解压炸弹（Zip Bomb）防御
    /// 5. 危险可执行文件拦截（.exe/.dll/.sh 等）
    /// </summary>
    public static string ValidateAndSanitizePackage(byte[]? packageData, string? packageFileName)
    {
        if (packageData is null || packageData.Length == 0)
        {
            return string.Empty;
        }

        if (packageData.Length > MaxPackageSizeBytes)
        {
            throw new InvalidOperationException($"技能包大小超过系统限制（最大允许 {MaxPackageSizeBytes / (1024 * 1024)}MB，当前文件大小为 {packageData.Length / 1024.0:F1}KB）。");
        }

        if (string.IsNullOrWhiteSpace(packageFileName))
        {
            throw new InvalidOperationException("技能包文件名不能为空。");
        }

        var extension = Path.GetExtension(packageFileName);
        if (!AllowedPackageExtensions.Contains(extension))
        {
            throw new InvalidOperationException("技能包仅支持 .zip、.json、.md 或 .txt 格式。");
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // 校验 Zip 文件魔数
            if (packageData.Length < 4 || packageData[0] != 0x50 || packageData[1] != 0x4B)
            {
                throw new InvalidOperationException("上传的 .zip 文件不是有效的 Zip 归档格式。");
            }

            try
            {
                using var ms = new MemoryStream(packageData);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                long totalUncompressedBytes = 0;

                foreach (var entry in archive.Entries)
                {
                    // 1. Zip Slip 目录穿越攻击拦截
                    var entryName = entry.FullName.Replace('\\', '/');
                    if (entryName.Contains("../") || entryName.StartsWith('/') || entryName.Contains(".."))
                    {
                        throw new InvalidOperationException($"检测到潜在的目录穿越恶意路径：'{entry.FullName}'。");
                    }

                    // 2. 危险可执行文件拦截
                    var entryExt = Path.GetExtension(entry.Name);
                    if (DangerousFileExtensions.Contains(entryExt))
                    {
                        throw new InvalidOperationException($"技能包中包含被禁止的高危可执行文件类型：'{entry.Name}'。");
                    }

                    // 3. Zip Bomb 解压膨胀炸弹防御
                    totalUncompressedBytes += entry.Length;
                    if (totalUncompressedBytes > MaxZipUncompressedBytes)
                    {
                        throw new InvalidOperationException("技能包解压后体积超出安全阈值 (50MB)，已被拒绝。");
                    }
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"解析 Zip 压缩包时发生错误：{ex.Message}");
            }
        }

        return extension.ToLowerInvariant() switch
        {
            ".zip" => "application/zip",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
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
            skill.ReviewStatus,
            skill.ReviewComment,
            skill.DownloadCount,
            skill.ViewCount,
            (skill.PackageData != null && skill.PackageData.Length > 0) || !string.IsNullOrWhiteSpace(skill.ReadmeMarkdown) || !string.IsNullOrWhiteSpace(skill.Summary),
            skill.PackageFileName,
            skill.CreatedAt,
            skill.UpdatedAt,
            skill.Versions
                .OrderByDescending(v => v.CreatedAt)
                .Select(v => new SkillVersionSummary(
                    v.Id,
                    v.SkillId,
                    v.Version,
                    v.Changelog,
                    v.PackageFileName,
                    v.DownloadCount,
                    v.CreatedAt))
                .ToList());
}
