using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using seekclaw_webserver.Data;
using seekclaw_webserver.Models;

namespace seekclaw_webserver.Services;

public sealed class AuthService(AppDbContext db)
{
    public const string AllowRegistrationKey = "allow_registration";
    public const string SiteNameKey = "site_name";
    public const string InitializedKey = "system_initialized";

    private static readonly PasswordHasher<User> PasswordHasher = new();

    public async Task<bool> AnyUserAsync()
    {
        return await db.Users.AnyAsync();
    }

    public async Task<bool> IsInitializedAsync()
    {
        return await db.Users.AnyAsync(x => x.IsSuperAdmin);
    }

    public async Task<DatabaseStatusResult> TestDatabaseConnectionAsync()
    {
        var result = new DatabaseStatusResult
        {
            ServerTime = DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        };

        try
        {
            var canConnect = await db.Database.CanConnectAsync();
            result.Connected = canConnect;

            var connection = db.Database.GetDbConnection();
            result.DatabasePath = connection.DataSource;

            if (File.Exists(result.DatabasePath))
            {
                result.DatabaseSizeBytes = new FileInfo(result.DatabasePath).Length;
            }

            if (canConnect)
            {
                result.UserCount = await db.Users.CountAsync();
                result.SkillCount = await db.Skills.CountAsync();
                result.IsInitialized = await db.Users.AnyAsync(x => x.IsSuperAdmin);
            }
        }
        catch (Exception ex)
        {
            result.Connected = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<AuthResult> InitializeSystemAsync(SetupRequest request)
    {
        if (await IsInitializedAsync())
        {
            return new AuthResult { Success = false, Error = "系统已完成初始化，无法重复执行。" };
        }

        var email = !string.IsNullOrWhiteSpace(request.AdminEmail) ? request.AdminEmail : request.AdminUsername;
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized))
        {
            return new AuthResult { Success = false, Error = "请输入有效的超级管理员邮箱（例如：admin@seekclaw.org）。" };
        }

        if (string.IsNullOrEmpty(request.AdminPassword) || request.AdminPassword.Length < 6)
        {
            return new AuthResult { Success = false, Error = "密码长度至少需要 6 位。" };
        }

        var adminUser = new User
        {
            Username = normalized,
            IsSuperAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        adminUser.PasswordHash = PasswordHasher.HashPassword(adminUser, request.AdminPassword);
        db.Users.Add(adminUser);

        // Site settings
        await SetSettingInternalAsync(SiteNameKey, string.IsNullOrWhiteSpace(request.SiteName) ? "SeekClaw" : request.SiteName.Trim());
        await SetSettingInternalAsync(AllowRegistrationKey, request.AllowRegistration ? "true" : "false");
        await SetSettingInternalAsync(InitializedKey, "true");

        await db.SaveChangesAsync();

        return new AuthResult { Success = true, IsSuperAdmin = true };
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

    public async Task<string> GetSiteNameAsync()
    {
        var setting = await db.SiteSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == SiteNameKey);
        return string.IsNullOrWhiteSpace(setting?.Value) ? "SeekClaw" : setting.Value;
    }

    public async Task<AuthResult> RegisterAsync(string email, string password)
    {
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized))
        {
            return new AuthResult { Success = false, Error = "请输入有效的邮箱地址（例如：user@example.com）。" };
        }

        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            return new AuthResult { Success = false, Error = "密码至少需要 6 位。" };
        }

        if (await db.Users.AnyAsync(x => x.Username == normalized))
        {
            return new AuthResult { Success = false, Error = "该邮箱已被注册。" };
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

    public async Task<User?> ValidateAsync(string email, string password)
    {
        var normalized = NormalizeEmail(email);
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

    public async Task<bool> DeleteUserAsync(int id)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id);
        if (user is null)
        {
            return false;
        }

        // Prevent deleting the last SuperAdmin
        if (user.IsSuperAdmin)
        {
            var adminCount = await db.Users.CountAsync(x => x.IsSuperAdmin);
            if (adminCount <= 1)
            {
                throw new InvalidOperationException("无法删除系统中唯一的超级管理员。");
            }
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResetPasswordAsync(int id, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            throw new ArgumentException("新密码至少需要 6 位。", nameof(newPassword));
        }

        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = PasswordHasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetSuperAdminAsync(int id, bool isSuperAdmin)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id);
        if (user is null)
        {
            return false;
        }

        if (!isSuperAdmin && user.IsSuperAdmin)
        {
            var adminCount = await db.Users.CountAsync(x => x.IsSuperAdmin);
            if (adminCount <= 1)
            {
                throw new InvalidOperationException("系统中必须至少保留一个超级管理员。");
            }
        }

        user.IsSuperAdmin = isSuperAdmin;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> IsRegistrationAllowedAsync()
    {
        return await IsRegistrationEnabledAsync();
    }

    public async Task<string> GetSettingAsync(string key, string fallback = "")
    {
        var setting = await db.SiteSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key);
        return setting?.Value ?? fallback;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await SetSettingInternalAsync(key, value);
        await db.SaveChangesAsync();
    }

    public async Task SetRegistrationEnabledAsync(bool enabled)
    {
        await SetSettingInternalAsync(AllowRegistrationKey, enabled ? "true" : "false");
        await db.SaveChangesAsync();
    }

    public async Task SetSiteNameAsync(string name)
    {
        await SetSettingInternalAsync(SiteNameKey, string.IsNullOrWhiteSpace(name) ? "SeekClaw" : name.Trim());
        await db.SaveChangesAsync();
    }

    private async Task SetSettingInternalAsync(string key, string value)
    {
        var setting = await db.SiteSettings.SingleOrDefaultAsync(x => x.Key == key);
        if (setting is null)
        {
            setting = new SiteSetting { Key = key, Value = value };
            db.SiteSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
        }
    }

    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256)
        {
            return false;
        }

        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address.Equals(email, StringComparison.OrdinalIgnoreCase) && email.Contains('.') && !email.EndsWith('.');
        }
        catch
        {
            return false;
        }
    }
}

