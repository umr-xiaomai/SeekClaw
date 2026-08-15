namespace seekclaw_webserver.Models;

public sealed class SiteSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
