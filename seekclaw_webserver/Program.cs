using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Auth;
using seekclaw_webserver.Components;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;
using seekclaw_webserver.Services;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "seekclaw.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".SeekClaw.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }

                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("SuperAdmin"));
});
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddSingleton<MarkdownService>();
builder.Services.AddSingleton<DocService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Route Aliases for Docs
app.MapGet("/docs", () => Results.Redirect("/doc", permanent: false));
app.MapGet("/docs/{slug}", (string slug) => Results.Redirect($"/doc/{slug}", permanent: false));
app.MapGet("/en/docs", () => Results.Redirect("/en/doc", permanent: false));
app.MapGet("/en/docs/{slug}", (string slug) => Results.Redirect($"/en/doc/{slug}", permanent: false));

// System Setup & Diagnosis APIs
app.MapGet("/api/setup/status", async (AuthService auth) =>
{
    var isInitialized = await auth.IsInitializedAsync();
    return Results.Json(new { isInitialized });
});

app.MapGet("/api/setup/test-db", async (AuthService auth) =>
{
    var status = await auth.TestDatabaseConnectionAsync();
    return Results.Json(status);
}).RequireAuthorization("SuperAdminOnly");

app.MapPost("/api/setup/initialize", async (HttpContext context, SetupRequest request, AuthService auth, SkillService skills) =>
{
    if (await auth.IsInitializedAsync())
    {
        return Results.Json(new { success = false, error = "系统已完成初始化，无法重复配置。" });
    }

    var result = await auth.InitializeSystemAsync(request);
    if (!result.Success)
    {
        return Results.Json(new { success = false, error = result.Error });
    }

    if (request.SeedSampleSkills)
    {
        await skills.SeedOfficialSkillsAsync();
    }

    var admin = await auth.ValidateAsync(request.AdminUsername, request.AdminPassword);
    if (admin is not null)
    {
        await SignInAsync(context, admin);
    }

    return Results.Json(new { success = true });
});

// Authentication APIs
app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var user = context.User;
    if (user.Identity?.IsAuthenticated != true)
    {
        return Results.Json(new { authenticated = false });
    }

    return Results.Json(new
    {
        authenticated = true,
        userId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0,
        username = user.Identity.Name,
        isSuperAdmin = user.IsInRole("SuperAdmin")
    });
});

app.MapPost("/api/auth/register", async (HttpContext context, RegisterRequest request, AuthService auth) =>
{
    var email = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : request.Username;
    var result = await auth.RegisterAsync(email, request.Password);
    if (!result.Success)
    {
        return Results.Json(new { success = false, error = result.Error });
    }

    var user = await auth.ValidateAsync(email, request.Password);
    if (user is null)
    {
        return Results.Json(new { success = false, error = "注册后自动登录失败。" });
    }

    await SignInAsync(context, user);
    return Results.Json(new { success = true, isSuperAdmin = user.IsSuperAdmin });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AuthService auth) =>
{
    var email = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : request.Username;
    var user = await auth.ValidateAsync(email, request.Password);
    if (user is null)
    {
        return Results.Json(new { success = false, error = "邮箱或密码错误。" });
    }

    await SignInAsync(context, user);
    return Results.Json(new { success = true, isSuperAdmin = user.IsSuperAdmin });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Json(new { success = true });
});

app.MapGet("/api/auth/registration", async (AuthService auth) =>
    Results.Json(new { enabled = await auth.IsRegistrationEnabledAsync() }));

// Skill Package Downloads
app.MapGet("/api/skills/{id:int}/download", async (int id, SkillService skills) =>
{
    var skill = await skills.GetPackageAsync(id);
    if (skill is null || skill.PackageData is null || skill.PackageData.Length == 0)
    {
        return Results.NotFound();
    }

    return Results.File(
        skill.PackageData,
        skill.PackageContentType ?? "application/octet-stream",
        skill.PackageFileName ?? $"{skill.Slug}.zip");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task SignInAsync(HttpContext context, User user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.IsSuperAdmin ? "SuperAdmin" : "User")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        });
}
