using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Services;

public sealed class AuthService(AppDbContext db)
{
    public const string AllowRegistrationKey = "allow_registration";

    private static readonly PasswordHasher<User> PasswordHasher = new();

    public async Task<bool> AnyUserAsync()
    {
        return await db.Users.AnyAsync();
    }

    public async Task<bool> IsRegistrationEnabledAsync()
    {
        if (!await db.Users.AnyAsync())
        {
            return true;
        }

        var setting = await db.SiteSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == AllowRegistrationKey);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AuthResult> RegisterAsync(string username, string password)
    {
        var normalized = NormalizeUsername(username);
        if (!IsValidUsername(normalized))
        {
            return new AuthResult { Success = false, Error = "用户名需为 3-32 位字母、数字、下划线或短横线。" };
        }

        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            return new AuthResult { Success = false, Error = "密码至少需要 6 位。" };
        }

        if (await db.Users.AnyAsync(x => x.Username == normalized))
        {
            return new AuthResult { Success = false, Error = "该用户名已被注册。" };
        }

        var hasUsers = await db.Users.AnyAsync();
        if (hasUsers && !await IsRegistrationEnabledAsync())
        {
            return new AuthResult { Success = false, Error = "管理员当前未开放注册。" };
        }

        var user = new User
        {
            Username = normalized,
            IsSuperAdmin = !hasUsers,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new AuthResult { Success = true, IsSuperAdmin = user.IsSuperAdmin };
    }

    public async Task<User?> ValidateAsync(string username, string password)
    {
        var normalized = NormalizeUsername(username);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == normalized);
        if (user is null)
        {
            return null;
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Success ? user : null;
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<User>> ListUsersAsync()
    {
        return await db.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    public async Task SetRegistrationEnabledAsync(bool enabled)
    {
        var setting = await db.SiteSettings.SingleOrDefaultAsync(x => x.Key == AllowRegistrationKey);
        if (setting is null)
        {
            setting = new SiteSetting { Key = AllowRegistrationKey };
            db.SiteSettings.Add(setting);
        }

        setting.Value = enabled ? "true" : "false";
        await db.SaveChangesAsync();
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static bool IsValidUsername(string username)
    {
        if (username.Length is < 3 or > 32)
        {
            return false;
        }

        return username.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    }
}

