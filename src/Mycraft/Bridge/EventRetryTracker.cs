using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

/// <summary>Game-thread-owned reliable event outbox. Transport supplies a fresh seq on every send.</summary>
public sealed class EventRetryTracker
{
    private sealed class Pending
    {
        public Dictionary<string, object> Message = null!;
        public long NextDueMs;
        public int Retries;
    }

    private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
    private readonly int _capacity;
    public int Count => _pending.Count;
    public EventRetryTracker(int capacity = DemoProtocol.DedupCapacity) { _capacity = Math.Max(1, capacity); }

    /// <summary>Track after the initial send succeeds. Returns false on duplicate IDs or exhausted capacity.</summary>
    public bool Track(string eventId, Dictionary<string, object> message, long nowMs)
    {
        if (string.IsNullOrEmpty(eventId) || message == null || !DemoProtocol.ValidateMessage(message)
            || DemoProtocol.S(message, "eventId") != eventId || DemoProtocol.S(message, "sid").Length == 0
            || (DemoProtocol.S(message, "type") != "hit_actor" && DemoProtocol.S(message, "type") != "player_hurt" && DemoProtocol.S(message,"type")!="mob_hurt")
            || _pending.Count >= _capacity || _pending.ContainsKey(eventId)) return false;
        _pending.Add(eventId, new Pending
        {
            Message = new Dictionary<string, object>(message),
            NextDueMs = nowMs + DemoProtocol.RetryIntervalMs
        });
        return true;
    }

    public bool Acknowledge(string eventId) => eventId != null && _pending.Remove(eventId);

    /// <summary>Up to three retries, then reports exhaustion after the last retry's ACK window expires.</summary>
    public List<Dictionary<string, object>> TakeDue(long nowMs, out bool exhausted)
    {
        var due = new List<Dictionary<string, object>>();
        exhausted = false;
        foreach (var entry in _pending.Values)
        {
            if (nowMs < entry.NextDueMs) continue;
            if (entry.Retries >= DemoProtocol.RetryCount) { exhausted = true; continue; }
            entry.Retries++;
            entry.NextDueMs = nowMs + DemoProtocol.RetryIntervalMs;
            due.Add(new Dictionary<string, object>(entry.Message));
        }
        return due;
    }

    public void Clear() => _pending.Clear();
}
