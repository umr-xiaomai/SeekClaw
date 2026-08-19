namespace seekclaw_webserver.Models;

public sealed class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Username { get => Email; set => Email = value; }
    public string Password { get; set; } = string.Empty;
}

public sealed class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Username { get => Email; set => Email = value; }
    public string Password { get; set; } = string.Empty;
}

public sealed class SetupRequest
{
    public string AdminEmail { get; set; } = "admin@seekclaw.org";
    public string AdminUsername { get => AdminEmail; set => AdminEmail = value; }
    public string AdminPassword { get; set; } = string.Empty;
    public string SiteName { get; set; } = "SeekClaw";
    public bool AllowRegistration { get; set; } = true;
    public bool SeedSampleSkills { get; set; } = true;
}

public sealed class DatabaseStatusResult
{
    public bool Connected { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public long DatabaseSizeBytes { get; set; }
    public int UserCount { get; set; }
    public int SkillCount { get; set; }
    public bool IsInitialized { get; set; }
    public string? ErrorMessage { get; set; }
    public string ServerTime { get; set; } = string.Empty;
}

public sealed class AuthResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool IsSuperAdmin { get; init; }
}
