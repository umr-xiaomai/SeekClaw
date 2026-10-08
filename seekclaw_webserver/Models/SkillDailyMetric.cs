namespace seekclaw_webserver.Models;

public sealed class SkillDailyMetric
{
    public int Id { get; set; }
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd
    public int SkillId { get; set; }
    public int ViewCount { get; set; } = 0;
    public int DownloadCount { get; set; } = 0;

    public Skill? Skill { get; set; }
}
