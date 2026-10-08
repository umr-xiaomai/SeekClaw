namespace seekclaw_webserver.Models;

public static class SkillReviewStatus
{
    public const string Approved = "Approved";
    public const string Pending = "Pending";
    public const string Rejected = "Rejected";

    public static bool IsValid(string status) =>
        status is Approved or Pending or Rejected;
}

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
    public bool IsOfficial { get; set; } = false;
    public int? AuthorUserId { get; set; }
    public string? AuthorUsername { get; set; }
    public string? PackageFileName { get; set; }
    public string? PackageContentType { get; set; }
    public byte[]? PackageData { get; set; }
    public bool Enabled { get; set; } = true;
    public string ReviewStatus { get; set; } = SkillReviewStatus.Approved;
    public string? ReviewComment { get; set; }
    public int DownloadCount { get; set; } = 0;
    public int ViewCount { get; set; } = 0;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ICollection<SkillVersion> Versions { get; set; } = new List<SkillVersion>();
}
