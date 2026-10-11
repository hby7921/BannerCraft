using System;
using System.Collections.Generic;

namespace Mycraft.Demo;

// A hit is accepted only for an attack issued by this battle, and only once.
public sealed class CombatEventLedger
{
    public enum Resolution { Accepted, Duplicate, UnknownAttack, Invalid, CapacityExceeded }
    private readonly Dictionary<string, long> _pending = new Dictionary<string, long>();
    private readonly Dictionary<string, long> _events = new Dictionary<string, long>();
    private const int PendingCapacity = 64;
    private const int EventCapacity = 256;
    private const long AttackLifetimeMs = 3000;
    private const long RetryRetentionMs = 10000;

    public bool TryIssue(string attackId, long nowMs)
    {
        var expired = new List<string>();
        foreach (var attack in _pending)
            if (attack.Value < nowMs) expired.Add(attack.Key);
        foreach (var attack in expired) _pending.Remove(attack);
        if (string.IsNullOrEmpty(attackId) || _pending.Count >= PendingCapacity || _pending.ContainsKey(attackId))
            return false;
        _pending.Add(attackId, nowMs + AttackLifetimeMs);
        return true;
    }

    public Resolution Resolve(string attackId, string eventId, double damage, long nowMs)
    {
        if (string.IsNullOrEmpty(eventId) || !IsFinite(damage) || damage <= 0 || damage > 10000)
            return Resolution.Invalid;
        var expired = new List<string>();
        foreach (var entry in _events)
            if (entry.Value < nowMs) expired.Add(entry.Key);
        foreach (var id in expired) _events.Remove(id);
        if (_events.ContainsKey(eventId))
            return Resolution.Duplicate;
        if (!_pending.TryGetValue(attackId, out var deadline) || deadline < nowMs)
            return Resolution.UnknownAttack;
        if (_events.Count >= EventCapacity)
            return Resolution.CapacityExceeded;
        _pending.Remove(attackId);
        // Consuming the issued attack also prevents a retired event from applying again.
        // Keep acknowledgements longer than the transport's 3 x 500ms retry horizon.
        _events.Add(eventId, nowMs + RetryRetentionMs);
        return Resolution.Accepted;
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public static int ToBannerlordDamage(double damage, double factor)
    {
        var scaled = damage * factor;
        if (!IsFinite(scaled) || scaled <= 0 || scaled > 100000)
            throw new ArgumentOutOfRangeException(nameof(damage));
        return Math.Max(1, (int)Math.Round(scaled));
    }

    public void Clear() { _pending.Clear(); _events.Clear(); }
}
