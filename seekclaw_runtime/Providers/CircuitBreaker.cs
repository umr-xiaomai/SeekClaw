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
        public bool ProbeInFlight;
    }

    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOpen(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return false;
        lock (circuit)
        {
            if (circuit.ConsecutiveFailures < config.CircuitBreakThreshold) return false;
            return DateTimeOffset.UtcNow - circuit.OpenedAt < TimeSpan.FromSeconds(config.CircuitCooldownSeconds)
                || circuit.ProbeInFlight;
        }
    }

    public bool TryBeginHalfOpenProbe(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return false;
        lock (circuit)
        {
            if (circuit.ConsecutiveFailures < config.CircuitBreakThreshold) return false;
            if (DateTimeOffset.UtcNow - circuit.OpenedAt < TimeSpan.FromSeconds(config.CircuitCooldownSeconds))
                return false;
            if (circuit.ProbeInFlight) return false;
            circuit.ProbeInFlight = true;
            return true;
        }
    }

    public void EndProbe(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return;
        lock (circuit)
        {
            circuit.ProbeInFlight = false;
        }
    }

    public void RecordSuccess(string modelRef)
    {
        if (_circuits.TryRemove(modelRef, out var removed))
        {
            lock (removed)
            {
                removed.ConsecutiveFailures = 0;
                removed.ProbeInFlight = false;
            }
        }
    }

    public void RecordFailure(string modelRef)
    {
        var circuit = _circuits.GetOrAdd(modelRef, _ => new Circuit());
        lock (circuit)
        {
            circuit.ProbeInFlight = false;
            circuit.ConsecutiveFailures++;
            if (circuit.ConsecutiveFailures >= config.CircuitBreakThreshold)
                circuit.OpenedAt = DateTimeOffset.UtcNow;
        }
    }

    public int FailureCount(string modelRef)
    {
        if (!_circuits.TryGetValue(modelRef, out var circuit)) return 0;
        lock (circuit)
        {
            return circuit.ConsecutiveFailures;
        }
    }
}
