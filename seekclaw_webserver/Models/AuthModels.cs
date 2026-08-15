namespace seekclaw_webserver.Models;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class AuthResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool IsSuperAdmin { get; init; }
}
