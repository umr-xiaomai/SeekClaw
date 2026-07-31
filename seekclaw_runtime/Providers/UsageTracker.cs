using System.Text.Json;
using System.Collections.Concurrent;
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
    private readonly string _file = filePath ?? SeekClawPaths.UsageFile;
    // Isolated concurrent turn runtimes share the append-only usage file. A process-wide
    // lock prevents two instances from opening/writing the same file at the same time.
    private static readonly ConcurrentDictionary<string, Lock> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private Lock Gate => FileGates.GetOrAdd(_file, static _ => new Lock());

    public void Record(UsageEntry entry)
    {
        lock (Gate)
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
        lock (Gate)
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
                // Older JSONL entries predate TotalInputTokens; fall back without rewriting
                // the user's append-only history.
                TotalInputTokens = g.Sum(e => e.TotalInputTokens > 0 ? e.TotalInputTokens : e.InputTokens),
                CachedInputTokens = g.Sum(e => e.CachedInputTokens),
                CacheCreationInputTokens = g.Sum(e => e.CacheCreationInputTokens),
                OutputTokens = g.Sum(e => e.OutputTokens),
                Cost = g.Sum(e => e.Cost),
                AvgLatencyMs = g.Average(e => e.ElapsedMs),
            })
            .OrderByDescending(a => a.Cost)
            .ToList();
}
