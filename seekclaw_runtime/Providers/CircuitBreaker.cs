using System.Collections.Concurrent;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

/// <summary>Per-model circuit breaker: opens after N consecutive failures, half-opens after a cooldown.</summary>
public sealed class CircuitBreaker(RetryConfig config)
{
    private sealed class Circuit
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenedAt;
    }

    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOpen(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return false;
        if (circuit.ConsecutiveFailures < config.CircuitBreakThreshold) return false;
        // Half-open after cooldown: allow one probe through.
        return DateTimeOffset.UtcNow - circuit.OpenedAt < TimeSpan.FromSeconds(config.CircuitCooldownSeconds);
    }

    public void RecordSuccess(string modelRef) => _circuits.TryRemove(modelRef, out _);

    public void RecordFailure(string modelRef)
    {
        var circuit = _circuits.GetOrAdd(modelRef, _ => new Circuit());
        lock (circuit)
        {
            circuit.ConsecutiveFailures++;
            if (circuit.ConsecutiveFailures >= config.CircuitBreakThreshold)
                circuit.OpenedAt = DateTimeOffset.UtcNow;
        }
    }

    public int FailureCount(string modelRef) =>
        _circuits.TryGetValue(modelRef, out var circuit) ? circuit.ConsecutiveFailures : 0;
}
