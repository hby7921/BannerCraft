using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

/// <summary>Session deduplication. Capacity exhaustion fails safely without forgetting applied events.</summary>
public sealed class DedupSet
{
    private readonly HashSet<string> _seen;
    private readonly int _capacity;

    public DedupSet(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _seen = new HashSet<string>();
    }

    public bool IsFull => _seen.Count >= _capacity;
    public bool Contains(string eventId) => eventId != null && _seen.Contains(eventId);

    /// <summary>False for a duplicate, invalid ID or full set; caller must fail a session when full.</summary>
    public bool TryMark(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
            return false;
        if (_seen.Contains(eventId))
            return false;
        if (IsFull) return false;
        _seen.Add(eventId);
        return true;
    }

    public void Clear()
    {
        _seen.Clear();
    }
}
