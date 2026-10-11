using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

// Raw sequence/envelope validation stays in the socket reader. This buffer
// removes obsolete visual samples, never combat receipts or their retries.
internal sealed class SessionInboundBuffer
{
    internal const int ReliableCapacity = 4096;
    private const int LifecycleCapacity = 4096;
    private readonly struct Key : IEquatable<Key>
    {
        internal readonly string Sid, Type, Id;
        internal Key(string sid, string type, string id) { Sid = sid; Type = type; Id = id; }
        public bool Equals(Key other) => Sid == other.Sid && Type == other.Type && Id == other.Id;
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode()
        {
            unchecked { return ((Sid.GetHashCode() * 397) ^ Type.GetHashCode()) * 397 ^ Id.GetHashCode(); }
        }
    }
    private sealed class Entry
    {
        internal readonly Dictionary<string, object> Message;
        internal readonly Key? Snapshot;
        internal Entry(Dictionary<string, object> message, Key? snapshot) { Message = message; Snapshot = snapshot; }
    }
    private readonly object _gate = new object();
    private readonly int _snapshotCapacity, _reliableCapacity;
    private readonly LinkedList<Entry> _ordered = new LinkedList<Entry>();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _latest = new Dictionary<Key, LinkedListNode<Entry>>();
    private readonly HashSet<Key> _firstStates = new HashSet<Key>();
    private readonly HashSet<Key> _dead = new HashSet<Key>(), _removed = new HashSet<Key>();
    private readonly Queue<Key> _deadOrder = new Queue<Key>(), _removedOrder = new Queue<Key>();
    private readonly HashSet<string> _ended = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> _endedOrder = new Queue<string>();
    private int _reliableCount;
    private long _coalesced;

    internal SessionInboundBuffer(int snapshotCapacity = DemoProtocol.InQueueCapacity, int reliableCapacity = ReliableCapacity)
    {
        if (snapshotCapacity < 1 || reliableCapacity < 1) throw new ArgumentOutOfRangeException();
        _snapshotCapacity = snapshotCapacity; _reliableCapacity = reliableCapacity;
    }
    internal int Count { get { lock (_gate) return _ordered.Count; } }
    internal int SnapshotCount { get { lock (_gate) return _latest.Count; } }
    internal int ReliableCount { get { lock (_gate) return _reliableCount; } }
    internal long CoalescedCount { get { lock (_gate) return _coalesced; } }

    internal bool TryEnqueue(Dictionary<string, object> message)
    {
        lock (_gate)
        {
            string type = DemoProtocol.S(message, "type"), sid = DemoProtocol.S(message, "sid");
            if (type == "heartbeat") return true; // LastRx was already refreshed by the reader.
            if (type == "end_session")
            {
                if (_reliableCount >= _reliableCapacity) return false;
                foreach (var key in new List<Key>(_latest.Keys)) if (key.Sid == sid) RemoveLatest(key);
                _firstStates.RemoveWhere(key => key.Sid == sid);
                RememberEnded(sid);
                AddReliable(message);
                return true;
            }
            if (type == "mob_remove" || type == "boat_remove")
            {
                if (_reliableCount >= _reliableCapacity) return false;
                var key = new Key(sid, type == "mob_remove" ? "mob_state" : "boat_state", DemoProtocol.S(message, "entityId"));
                RemoveLatest(key);
                _firstStates.Remove(key);
                Remember(key, _removed, _removedOrder);
                AddReliable(message);
                return true;
            }
            if (!TrySnapshotKey(message, out var snapshot))
            {
                if (_reliableCount >= _reliableCapacity) return false;
                AddReliable(message);
                return true;
            }
            // Retired entities/sessions cannot be revived by delayed visual samples.
            bool terminal = message.ContainsKey("alive") && !BridgeJson.B(message, "alive");
            if (_ended.Contains(sid) || _removed.Contains(snapshot) || _dead.Contains(snapshot) && !terminal)
            { _coalesced++; return true; }
            bool first = (type == "mob_state" || type == "player_state") && !_firstStates.Contains(snapshot);
            if (first || terminal && !_dead.Contains(snapshot))
            {
                if (_reliableCount >= _reliableCapacity || first && _firstStates.Count >= LifecycleCapacity) return false;
                if (first) _firstStates.Add(snapshot);
                if (terminal) { RemoveLatest(snapshot); Remember(snapshot, _dead, _deadOrder); }
                // First mob state binds the native army slot before a subsequent
                // hit/remove. First death is irreversible gameplay, not animation.
                AddReliable(message);
                return true;
            }
            if (!_latest.ContainsKey(snapshot) && _latest.Count >= _snapshotCapacity) return false;
            RemoveLatest(snapshot);
            var node = _ordered.AddLast(new Entry(message, snapshot));
            _latest.Add(snapshot, node);
            return true;
        }
    }

    internal bool TryDequeue(out Dictionary<string, object> message)
    {
        lock (_gate)
        {
            var node = _ordered.First;
            if (node == null) { message = null!; return false; }
            _ordered.RemoveFirst();
            if (node.Value.Snapshot is Key key) _latest.Remove(key);
            else _reliableCount--;
            message = node.Value.Message;
            return true;
        }
    }
    internal void Clear()
    {
        lock (_gate)
        {
            _ordered.Clear(); _latest.Clear(); _firstStates.Clear(); _dead.Clear(); _removed.Clear();
            _deadOrder.Clear(); _removedOrder.Clear(); _ended.Clear(); _endedOrder.Clear();
            _reliableCount = 0; _coalesced = 0;
        }
    }
    private void AddReliable(Dictionary<string, object> message)
    { _ordered.AddLast(new Entry(message, null)); _reliableCount++; }
    private void RemoveLatest(Key key)
    {
        if (!_latest.TryGetValue(key, out var node)) return;
        _ordered.Remove(node); _latest.Remove(key); _coalesced++;
    }
    private static void Remember(Key key, HashSet<Key> set, Queue<Key> order)
    {
        if (!set.Add(key)) return;
        order.Enqueue(key);
        while (order.Count > LifecycleCapacity) set.Remove(order.Dequeue());
    }
    private void RememberEnded(string sid)
    {
        if (!_ended.Add(sid)) return;
        _endedOrder.Enqueue(sid);
        while (_endedOrder.Count > LifecycleCapacity) _ended.Remove(_endedOrder.Dequeue());
    }
    private static bool TrySnapshotKey(Dictionary<string, object> message, out Key key)
    {
        string type = DemoProtocol.S(message, "type"), sid = DemoProtocol.S(message, "sid");
        switch (type)
        {
            case "mob_state": case "boat_state": case "projectile_state": case "tnt_state": case "lightning_state":
                key = new Key(sid, type, DemoProtocol.S(message, "entityId")); return true;
            case "player_state": case "player_motion":
                key = new Key(sid, type, ""); return true;
            default: key = default; return false;
        }
    }
}
