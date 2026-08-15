namespace seekclaw_webserver.Models;

public sealed class Skill
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ReadmeMarkdown { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string? Homepage { get; set; }
    public string? PackageFileName { get; set; }
    public string? PackageContentType { get; set; }
    public byte[]? PackageData { get; set; }
    public bool Enabled { get; set; } = true;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

