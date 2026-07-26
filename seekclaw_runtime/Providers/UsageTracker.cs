using System.Text.Json;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Runtime.Providers;

public interface IUsageTracker
{
    void Record(UsageEntry entry);
    IReadOnlyList<UsageEntry> ReadAll(DateTimeOffset? since = null);
    IReadOnlyList<UsageAggregate> Aggregate(DateTimeOffset? since = null);
}

/// <summary>Append-only JSONL usage log with in-memory aggregation.</summary>
public sealed class UsageTracker(IEventBus eventBus, string? filePath = null) : IUsageTracker
{
    private readonly object _gate = new();
    private readonly string _file = filePath ?? SeekClawPaths.UsageFile;

    public void Record(UsageEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var line = JsonSerializer.Serialize(entry, SeekClawJsonContext.Compact.UsageEntry);
            File.AppendAllText(_file, line + Environment.NewLine);
        }

        eventBus.Publish(new UsageRecordedEvent(
            entry.Provider, entry.Model, entry.InputTokens, entry.OutputTokens,
            entry.Cost, TimeSpan.FromMilliseconds(entry.ElapsedMs)));
    }

    public IReadOnlyList<UsageEntry> ReadAll(DateTimeOffset? since = null)
    {
        lock (_gate)
        {
            if (!File.Exists(_file)) return [];
            var entries = new List<UsageEntry>();
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize(line, SeekClawJsonContext.Compact.UsageEntry);
                    if (entry is not null && (since is null || entry.Timestamp >= since))
                        entries.Add(entry);
                }
                catch (JsonException) { /* skip corrupt line */ }
            }
            return entries;
        }
    }

    public IReadOnlyList<UsageAggregate> Aggregate(DateTimeOffset? since = null) =>
        ReadAll(since)
            .GroupBy(e => (e.Provider, e.Model))
            .Select(g => new UsageAggregate
            {
                Provider = g.Key.Provider,
                Model = g.Key.Model,
                Calls = g.Count(),
                Failures = g.Count(e => !e.Success),
                InputTokens = g.Sum(e => e.InputTokens),
                OutputTokens = g.Sum(e => e.OutputTokens),
                Cost = g.Sum(e => e.Cost),
                AvgLatencyMs = g.Average(e => e.ElapsedMs),
            })
            .OrderByDescending(a => a.Cost)
            .ToList();
}
