using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;
using seekclaw_webserver.Services;
using Xunit;

namespace seekclaw_tests;

public sealed class WebServerTests
{
    private static (AppDbContext Db, SqliteConnection Connection) CreateInMemoryDbContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "seekclaw_webserver";
        public string WebRootPath { get; set; } = FindWebRootPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        private static string FindWebRootPath()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "seekclaw_webserver", "wwwroot");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
            return AppContext.BaseDirectory;
        }
    }

    [Fact]
    public async Task AuthService_InitialSetup_Workflow()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var auth = new AuthService(db);

            // 1. Initial state: not initialized
            Assert.False(await auth.IsInitializedAsync());
            Assert.False(await auth.AnyUserAsync());

            // 2. Reject invalid email
            var invalidSetup = new SetupRequest
            {
                AdminEmail = "not-an-email",
                AdminPassword = "SecurePassword123"
            };
            var invalidInit = await auth.InitializeSystemAsync(invalidSetup);
            Assert.False(invalidInit.Success);

            // 3. Initialize system with valid email
            var setup = new SetupRequest
            {
                AdminEmail = "admin@seekclaw.org",
                AdminPassword = "SecurePassword123",
                SiteName = "My SeekClaw Hub",
                AllowRegistration = false
            };

            var initResult = await auth.InitializeSystemAsync(setup);
            Assert.True(initResult.Success);
            Assert.True(initResult.IsSuperAdmin);

            // 4. Verify initialized
            Assert.True(await auth.IsInitializedAsync());
            Assert.True(await auth.AnyUserAsync());
            Assert.False(await auth.IsRegistrationAllowedAsync());
            Assert.Equal("My SeekClaw Hub", await auth.GetSettingAsync(AuthService.SiteNameKey));

            // 5. Cannot re-initialize
            var secondInit = await auth.InitializeSystemAsync(setup);
            Assert.False(secondInit.Success);

            // 6. Validation
            var validUser = await auth.ValidateAsync("admin@seekclaw.org", "SecurePassword123");
            Assert.NotNull(validUser);
            Assert.True(validUser.IsSuperAdmin);

            var invalidUser = await auth.ValidateAsync("admin@seekclaw.org", "WrongPassword");
            Assert.Null(invalidUser);

            // 7. Registration when disabled
            var regResult = await auth.RegisterAsync("normaluser@example.com", "NormalPass123");
            Assert.False(regResult.Success);

            // 8. Enable registration & register
            await auth.SetRegistrationEnabledAsync(true);
            Assert.True(await auth.IsRegistrationAllowedAsync());

            var regSuccess = await auth.RegisterAsync("normaluser@example.com", "NormalPass123");
            Assert.True(regSuccess.Success);
            Assert.False(regSuccess.IsSuperAdmin);
        }
    }

    [Fact]
    public async Task SkillService_OfficialAndCommunity_Works()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var skillService = new SkillService(db);

            // 1. Seed official starter skills
            await skillService.SeedOfficialSkillsAsync();

            // 2. Lookup by slug and verify official flag
            var dotnetSkill = await skillService.GetDetailBySlugAsync("dotnet-dev");
            Assert.NotNull(dotnetSkill);
            Assert.True(dotnetSkill.IsOfficial);
            Assert.Equal("dotnet-dev", dotnetSkill.Slug);
            Assert.Contains(".NET", dotnetSkill.Name);

            // 3. User creates a Community skill
            var communityInput = new SkillInput
            {
                Name = "Community Vue Assistant",
                Slug = "community-vue",
                Summary = "Vue 3 tool for frontend devs",
                ReadmeMarkdown = "# Vue Assistant",
                Author = "alice",
                IsOfficial = false,
                Enabled = true
            };
            var userSkill = await skillService.CreateAsync(
                communityInput,
                packageData: null,
                packageFileName: null,
                packageContentType: null,
                isOfficial: false,
                authorUserId: 42,
                authorUsername: "alice");

            Assert.False(userSkill.IsOfficial);
            Assert.Equal(42, userSkill.AuthorUserId);
            Assert.Equal("alice", userSkill.AuthorUsername);

            // 4. Test Official vs Community filtering
            var officialList = await skillService.ListAsync(includeDisabled: false, typeFilter: "official");
            Assert.All(officialList, s => Assert.True(s.IsOfficial));
            Assert.Contains(officialList, s => s.Slug == "dotnet-dev");
            Assert.DoesNotContain(officialList, s => s.Slug == "community-vue");

            var communityList = await skillService.ListAsync(includeDisabled: false, typeFilter: "community");
            Assert.All(communityList, s => Assert.False(s.IsOfficial));
            Assert.Contains(communityList, s => s.Slug == "community-vue");
            Assert.DoesNotContain(communityList, s => s.Slug == "dotnet-dev");

            // 5. Admin promotes community skill to official
            await skillService.SetOfficialAsync(userSkill.Id, true);
            var promoted = await skillService.GetDetailAsync(userSkill.Id);
            Assert.NotNull(promoted);
            Assert.True(promoted.IsOfficial);
        }
    }

    [Fact]
    public void DocService_GroupsAndOutline_MatchVitePress()
    {
        var markdownService = new MarkdownService();
        var env = new FakeWebHostEnvironment();
        var docService = new DocService(env, markdownService);

        var zhGroups = docService.GetGroups("zh");
        Assert.Equal(4, zhGroups.Count);
        Assert.Equal("起步与概览", zhGroups[0].Title);
        Assert.Equal("核心功能与交互", zhGroups[1].Title);
        Assert.Equal("实战与最佳实践", zhGroups[2].Title);
        Assert.Equal("运行时进阶机制", zhGroups[3].Title);

        var quickstart = docService.Get("zh", "quickstart");
        Assert.NotNull(quickstart);
        Assert.NotEmpty(quickstart.Html);
        Assert.NotEmpty(quickstart.Outline);

        // Test search
        var searchResults = docService.Search("Provider");
        Assert.NotEmpty(searchResults);
    }

    [Fact]
    public void MarkdownService_GitHubCallouts_TransformedToVitePressAlerts()
    {
        var markdownService = new MarkdownService();
        var markdown = "> [!NOTE]\n> This is an important note message.";

        var html = markdownService.ToHtml(markdown);
        Assert.Contains("vp-callout-note", html);
        Assert.Contains("Note", html, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateZipPackage(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task SkillService_ReviewWorkflow_PendingToApprovedOrRejected()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var skillService = new SkillService(db);

            // 1. Community user creates a skill -> should be Pending
            var input = new SkillInput
            {
                Name = "Code Refactor Tool",
                Slug = "code-refactor",
                Summary = "Automated refactoring",
                ReadmeMarkdown = "# Refactor",
                Author = "bob",
                Version = "1.0.0",
                Changelog = "Initial release"
            };

            var created = await skillService.CreateAsync(
                input,
                packageData: null,
                packageFileName: null,
                packageContentType: null,
                isOfficial: false,
                authorUserId: 101,
                authorUsername: "bob");

            Assert.Equal(SkillReviewStatus.Pending, created.ReviewStatus);

            // 2. Default public list (Approved only) should NOT contain pending skill
            var publicSkills = await skillService.ListAsync(includeDisabled: false, reviewStatus: SkillReviewStatus.Approved);
            Assert.DoesNotContain(publicSkills, s => s.Slug == "code-refactor");

            // 3. User's own skills list SHOULD contain it
            var userSkills = await skillService.ListUserSkillsAsync(101);
            Assert.Contains(userSkills, s => s.Slug == "code-refactor" && s.ReviewStatus == SkillReviewStatus.Pending);

            // 4. Admin rejects with comments
            await skillService.ReviewSkillAsync(created.Id, SkillReviewStatus.Rejected, "需要更详细的 README 示例");
            
            var rejectedDetail = await skillService.GetDetailAsync(created.Id);
            Assert.NotNull(rejectedDetail);
            Assert.Equal(SkillReviewStatus.Rejected, rejectedDetail.ReviewStatus);
            Assert.Equal("需要更详细的 README 示例", rejectedDetail.ReviewComment);

            // 5. Admin approves
            await skillService.ReviewSkillAsync(created.Id, SkillReviewStatus.Approved, "审核通过");
            
            var approvedDetail = await skillService.GetDetailAsync(created.Id);
            Assert.NotNull(approvedDetail);
            Assert.Equal(SkillReviewStatus.Approved, approvedDetail.ReviewStatus);

            // 6. Now visible in public marketplace
            publicSkills = await skillService.ListAsync(includeDisabled: false, reviewStatus: SkillReviewStatus.Approved);
            Assert.Contains(publicSkills, s => s.Slug == "code-refactor");
        }
    }

    [Fact]
    public async Task SkillService_MultiVersion_And_DownloadCounting()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var skillService = new SkillService(db);

            var v1Pkg = CreateZipPackage(("prompt.txt", "v1 prompt"));
            var input = new SkillInput
            {
                Name = "Multi Version Skill",
                Slug = "multi-ver",
                Summary = "Testing multiple versions",
                ReadmeMarkdown = "# Multi Version",
                Author = "alice",
                Version = "1.0.0",
                Changelog = "Initial 1.0.0 release"
            };

            var skill = await skillService.CreateAsync(
                input,
                packageData: v1Pkg,
                packageFileName: "multi-ver-1.0.0.zip",
                packageContentType: "application/zip",
                isOfficial: true,
                authorUserId: 1,
                authorUsername: "admin");

            // Verify initial version was created
            var detail = await skillService.GetDetailAsync(skill.Id);
            Assert.NotNull(detail);
            Assert.Single(detail.Versions);
            Assert.Equal("1.0.0", detail.Versions[0].Version);
            Assert.Equal("Initial 1.0.0 release", detail.Versions[0].Changelog);

            // Publish version 1.1.0
            var v2Pkg = CreateZipPackage(("prompt.txt", "v2 prompt"));
            var addedVer = await skillService.AddVersionAsync(
                skill.Id,
                new SkillVersionInput
                {
                    Version = "1.1.0",
                    Changelog = "Added new features and optimized prompts"
                },
                packageData: v2Pkg,
                packageFileName: "multi-ver-1.1.0.zip",
                isSuperAdmin: true,
                currentUserId: 1);

            Assert.NotNull(addedVer);

            // Verify both versions exist and skill's latest version is updated
            detail = await skillService.GetDetailAsync(skill.Id);
            Assert.NotNull(detail);
            Assert.Equal("1.1.0", detail.Version);
            Assert.Equal(2, detail.Versions.Count);

            // Download v1.0.0 specifically
            var pkgV1 = await skillService.GetDownloadPackageAsync("multi-ver", "1.0.0");
            Assert.NotNull(pkgV1);
            Assert.Equal("multi-ver-1.0.0.zip", pkgV1.FileName);

            // Download latest version
            var pkgLatest = await skillService.GetDownloadPackageAsync("multi-ver");
            Assert.NotNull(pkgLatest);

            // Check download counts
            detail = await skillService.GetDetailAsync(skill.Id);
            Assert.NotNull(detail);
            Assert.Equal(2, detail.DownloadCount);
            var v1Record = detail.Versions.First(v => v.Version == "1.0.0");
            Assert.Equal(1, v1Record.DownloadCount);
        }
    }

    [Fact]
    public async Task SkillService_PackageValidation_SecurityChecks()
    {
        // 1. Valid package passes
        var validZip = CreateZipPackage(("prompt.txt", "valid prompt"), ("skill.yaml", "name: test"));
        var mime = SkillService.ValidateAndSanitizePackage(validZip, "skill.zip");
        Assert.Equal("application/zip", mime);

        // 2. Size limit (> 10MB)
        var oversizedData = new byte[SkillService.MaxPackageSizeBytes + 1];
        Assert.Throws<InvalidOperationException>(() =>
            SkillService.ValidateAndSanitizePackage(oversizedData, "large.zip"));

        // 3. Zip Slip traversal defense
        var zipSlipData = CreateZipPackage(("../evil.txt", "malicious payload"));
        var exSlip = Assert.Throws<InvalidOperationException>(() =>
            SkillService.ValidateAndSanitizePackage(zipSlipData, "slip.zip"));
        Assert.Contains("目录穿越", exSlip.Message);

        // 4. Dangerous executable blacklist (.exe)
        var exeData = CreateZipPackage(("malware.exe", "MZ..."));
        var exExe = Assert.Throws<InvalidOperationException>(() =>
            SkillService.ValidateAndSanitizePackage(exeData, "exe.zip"));
        Assert.Contains("高危可执行文件", exExe.Message);
    }

    [Fact]
    public async Task AuthService_ChangePassword_And_Caching()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
            var auth = new AuthService(db, memoryCache);

            // 1. Setup admin
            await auth.InitializeSystemAsync(new SetupRequest
            {
                AdminEmail = "user1@seekclaw.org",
                AdminPassword = "OldPassword123"
            });

            var user = await auth.ValidateAsync("user1@seekclaw.org", "OldPassword123");
            Assert.NotNull(user);

            // 2. Reject short password (< 6 chars)
            await Assert.ThrowsAsync<ArgumentException>(() =>
                auth.ChangePasswordAsync(user.Id, "OldPassword123", "123"));

            // 3. Reject wrong old password
            var wrongOld = await auth.ChangePasswordAsync(user.Id, "WrongOldPass", "NewPassword456");
            Assert.False(wrongOld);

            // 4. Successfully change password
            var changeOk = await auth.ChangePasswordAsync(user.Id, "OldPassword123", "NewPassword456");
            Assert.True(changeOk);

            // Verify old password no longer works
            Assert.Null(await auth.ValidateAsync("user1@seekclaw.org", "OldPassword123"));
            // Verify new password works
            Assert.NotNull(await auth.ValidateAsync("user1@seekclaw.org", "NewPassword456"));

            // 5. Verify caching behavior
            Assert.True(await auth.IsInitializedAsync());
            await auth.SetSiteNameAsync("Custom Claws");
            Assert.Equal("Custom Claws", await auth.GetSiteNameAsync());
        }
    }

    [Fact]
    public async Task SkillService_GetDownloadPackageAsync_FiltersDisabledAndUnapprovedSkills()
    {
        var (db, connection) = CreateInMemoryDbContext();
        using (connection)
        using (db)
        {
            var skillService = new SkillService(db);

            // 1. Create a community skill (defaults to Pending review status)
            var pendingInput = new SkillInput
            {
                Name = "Pending Skill",
                Slug = "pending-skill",
                Summary = "A pending community skill",
                Version = "1.0.0"
            };
            var pendingSkill = await skillService.CreateAsync(
                pendingInput,
                packageData: null,
                packageFileName: null,
                packageContentType: null,
                isOfficial: false,
                authorUserId: 2,
                authorUsername: "bob");

            // Public download should return null because review status is Pending
            var pendingPkg = await skillService.GetDownloadPackageAsync("pending-skill");
            Assert.Null(pendingPkg);

            // 2. Approve the skill
            await skillService.ReviewSkillAsync(pendingSkill.Id, SkillReviewStatus.Approved, "LGTM");
            var approvedPkg = await skillService.GetDownloadPackageAsync("pending-skill");
            Assert.NotNull(approvedPkg);

            // 3. Disable the skill
            await skillService.SetEnabledAsync(pendingSkill.Id, false);
            var disabledPkg = await skillService.GetDownloadPackageAsync("pending-skill");
            Assert.Null(disabledPkg);
        }
    }
}
