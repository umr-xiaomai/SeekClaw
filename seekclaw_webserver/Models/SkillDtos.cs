namespace seekclaw_webserver.Models;

public sealed record SkillSummary(
    int Id,
    string Name,
    string Slug,
    string Summary,
    string Author,
    string Version,
    bool IsOfficial,
    int? AuthorUserId,
    string? AuthorUsername,
    bool Enabled,
    bool HasPackage,
    long UpdatedAt);

public sealed record SkillDetailModel(
    int Id,
    string Name,
    string Slug,
    string Summary,
    string ReadmeMarkdown,
    string Author,
    string Version,
    string? Homepage,
    bool IsOfficial,
    int? AuthorUserId,
    string? AuthorUsername,
    bool Enabled,
    bool HasPackage,
    string? PackageFileName,
    long CreatedAt,
    long UpdatedAt);

public sealed class SkillInput
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ReadmeMarkdown { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string? Homepage { get; set; }
    public bool IsOfficial { get; set; } = false;
    public bool Enabled { get; set; } = true;
}


