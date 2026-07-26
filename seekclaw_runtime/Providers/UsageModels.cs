namespace SeekClaw.Runtime.Providers;

/// <summary>One LLM invocation, appended to ~/.seekclaw/usage.jsonl.</summary>
public sealed class UsageEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public double ElapsedMs { get; set; }
    public bool Success { get; set; }
}

/// <summary>Aggregated statistics for a provider/model pair.</summary>
public sealed class UsageAggregate
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long Calls { get; set; }
    public long Failures { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public double AvgLatencyMs { get; set; }

    public long TotalTokens => InputTokens + OutputTokens;
    public double SuccessRate => Calls == 0 ? 1.0 : (double)(Calls - Failures) / Calls;
}
