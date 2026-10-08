namespace seekclaw_webserver.Models;

public sealed class SkillVersion
{
    public int Id { get; set; }
    public int SkillId { get; set; }
    public Skill? Skill { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string Changelog { get; set; } = string.Empty;
    public string? PackageFileName { get; set; }
    public string? PackageContentType { get; set; }
    public byte[]? PackageData { get; set; }
    public int DownloadCount { get; set; } = 0;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
