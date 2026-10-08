namespace seekclaw_webserver.Models;

public sealed record SkillVersionSummary(
    int Id,
    int SkillId,
    string Version,
    string Changelog,
    string? PackageFileName,
    int DownloadCount,
    long CreatedAt);

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
    string ReviewStatus,
    string? ReviewComment,
    int DownloadCount,
    int ViewCount,
    int VersionCount,
    bool HasPackage,
    long UpdatedAt,
    string? Homepage = null);

public sealed record SkillPackageResult(
    byte[] Data,
    string ContentType,
    string FileName);

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
    string ReviewStatus,
    string? ReviewComment,
    int DownloadCount,
    int ViewCount,
    bool HasPackage,
    string? PackageFileName,
    long CreatedAt,
    long UpdatedAt,
    IReadOnlyList<SkillVersionSummary> Versions);

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
    public string Changelog { get; set; } = "初始版本发布";
}

public sealed class SkillVersionInput
{
    public string Version { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
}

public sealed class ReviewSkillRequest
{
    public string Status { get; set; } = SkillReviewStatus.Approved;
    public string? Comment { get; set; }
}

public sealed class UserSkillUpdateInput
{
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ReadmeMarkdown { get; set; } = string.Empty;
    public string? Homepage { get; set; }
}

public sealed record DailyTrendPoint(
    string Date,
    string Label,
    int Views,
    int Downloads,
    double Ctr
);

public sealed record TopSkillMetricDto(
    int Id,
    string Name,
    string Slug,
    bool IsOfficial,
    int Views,
    int Downloads,
    double Ctr
);

public sealed record SkillTagMetricDto(
    int Rank,
    string Tag,
    int SkillCount,
    int TotalDownloads,
    double AvgCtr
);

public sealed record MetricsOverviewDto(
    string TimeRange,
    string RangeDescription,
    int TotalViews,
    int TotalDownloads,
    double ConversionRate,
    int TotalSkills,
    int OfficialCount,
    int OfficialRatio,
    int CommunityCount,
    int CommunityRatio,
    int HasPackageCount,
    int HasPackageRatio,
    int MultiVersionCount,
    int MultiVersionRatio,
    List<DailyTrendPoint> Trends,
    List<TopSkillMetricDto> TopSkills,
    List<SkillTagMetricDto> TopTags
);
