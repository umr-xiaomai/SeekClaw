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
}
