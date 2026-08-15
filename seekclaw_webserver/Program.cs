using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
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

builder.Services.AddAuthorization();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddSingleton<MarkdownService>();
builder.Services.AddSingleton<DocService>();

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
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

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
    var result = await auth.RegisterAsync(request.Username, request.Password);
    if (!result.Success)
    {
        return Results.Json(new { success = false, error = result.Error });
    }

    var user = await auth.ValidateAsync(request.Username, request.Password);
    if (user is null)
    {
        return Results.Json(new { success = false, error = "注册后自动登录失败。" });
    }

    await SignInAsync(context, user);
    return Results.Json(new { success = true, isSuperAdmin = user.IsSuperAdmin });
});

app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AuthService auth) =>
{
    var user = await auth.ValidateAsync(request.Username, request.Password);
    if (user is null)
    {
        return Results.Json(new { success = false, error = "用户名或密码错误。" });
    }

    await SignInAsync(context, user);
    return Results.Json(new { success = true, isSuperAdmin = user.IsSuperAdmin });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Json(new { success = true });
});

app.MapGet("/api/auth/registration", async (AuthService auth) =>
    Results.Json(new { enabled = await auth.IsRegistrationEnabledAsync() }));

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
