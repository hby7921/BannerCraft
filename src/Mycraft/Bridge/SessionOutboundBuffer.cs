using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Mycraft.Bridge;

// Only already validated, serialized frames enter this buffer. Actor retirement
// is a lifetime barrier: unlike a deleted MC entity, a host actor may re-enter.
internal sealed class SessionOutboundBuffer
{
    internal const int SnapshotCapacity = 1024, ReliableCapacity = 4096;
    internal sealed class Frame
    {
        internal readonly string Line, CloseReason;
        internal Frame(string line, string closeReason = "") { Line = line; CloseReason = closeReason; }
    }
    private readonly struct Key : IEquatable<Key>
    {
        internal readonly string Sid, Type;
        internal readonly long Actor;
        internal Key(string sid, string type, long actor = 0) { Sid = sid; Type = type; Actor = actor; }
        public bool Equals(Key other) => Sid == other.Sid && Type == other.Type && Actor == other.Actor;
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode() { unchecked { return ((Sid.GetHashCode()*397)^Type.GetHashCode())*397^Actor.GetHashCode(); } }
    }
    private sealed class Entry
    {
        internal readonly Frame Frame;
        internal Key? Snapshot;
        internal Entry(Frame frame, Key? snapshot) { Frame = frame; Snapshot = snapshot; }
    }
    private sealed class HurtReceipt
    {
        internal readonly string Signature;
        internal readonly long Received;
        internal HurtReceipt(string signature, long received) { Signature = signature; Received = received; }
    }
    private readonly object _gate = new object();
    private readonly LinkedList<Entry> _ordered = new LinkedList<Entry>();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _latest = new Dictionary<Key, LinkedListNode<Entry>>();
    private readonly HashSet<Key> _seen = new HashSet<Key>();
    private readonly Dictionary<Key, HurtReceipt> _hurts = new Dictionary<Key, HurtReceipt>();
    private readonly Queue<(Key Key, long Received)> _hurtOrder = new Queue<(Key, long)>();
    private readonly int _snapshotLimit, _reliableLimit;
    private int _reliableCount;
    private bool _terminated;
    private long _coalesced;
    internal SessionOutboundBuffer(int snapshotLimit = SnapshotCapacity, int reliableLimit = ReliableCapacity)
    {
        if(snapshotLimit<1 || reliableLimit<1)throw new ArgumentOutOfRangeException();
        _snapshotLimit=snapshotLimit;_reliableLimit=reliableLimit;
    }
    internal int Count { get { lock(_gate)return _ordered.Count; } }
    internal int SnapshotCount { get { lock(_gate)return _latest.Count; } }
    internal int ReliableCount { get { lock(_gate)return _reliableCount; } }
    internal long CoalescedCount { get { lock(_gate)return _coalesced; } }

    internal bool TryEnqueue(Dictionary<string,object> message,string line,out string error)
    {
        lock(_gate)
        {
            error="";
            if(_terminated){error="outbound stream terminated";return false;}
            string sid=DemoProtocol.S(message,"sid"),type=DemoProtocol.S(message,"type");
            Key? snapshot=type=="player_pose"?new Key(sid,type):
                type=="actor_upsert" && BridgeJson.B(message,"alive")?new Key(sid,type,DemoProtocol.L(message,"eid")):(Key?)null;
            var frame=new Frame(line);
            if(snapshot is Key key)
            {
                if(!_seen.Contains(key))
                {
                    if(_seen.Count>=_snapshotLimit || _reliableCount>=_reliableLimit)
                    {error="outbound birth budget exceeded";return false;}
                    _seen.Add(key);AddReliable(frame);return true;
                }
                if(!_latest.ContainsKey(key) && _latest.Count>=_snapshotLimit)
                {error="outbound distinct snapshot budget exceeded";return false;}
                RemoveLatest(key);
                _latest.Add(key,_ordered.AddLast(new Entry(frame,key)));
                return true;
            }
            var playerKey=new Key(sid,"player_pose");
            Key? sourceKey=null;
            bool hurt=type=="mob_hurt";
            Key hurtKey=default;
            string signature="";
            long now=Stopwatch.GetTimestamp();
            if(hurt)
            {
                PruneHurts(now);
                sourceKey=new Key(sid,"actor_upsert",DemoProtocol.L(message,"sourceEid"));
                hurtKey=new Key(sid,DemoProtocol.S(message,"eventId"));
                signature=HurtSignature(message);
                bool exactRetry=_hurts.TryGetValue(hurtKey,out var receipt) && receipt.Signature==signature;
                if(!_seen.Contains(sourceKey.Value) && !exactRetry)
                {error="outbound creature damage source actor was not announced";return false;}
                if(!_hurts.ContainsKey(hurtKey) && _hurts.Count>=ReliableCapacity)
                {error="outbound creature retry budget exceeded";return false;}
            }
            else if(type=="player_hurt" && DemoProtocol.L(message,"sourceEid")>0)
                sourceKey=new Key(sid,"actor_upsert",DemoProtocol.L(message,"sourceEid"));
            int additional=1+(_latest.ContainsKey(playerKey)?1:0)+(sourceKey is Key source && _latest.ContainsKey(source)?1:0);
            if(_reliableCount+additional>_reliableLimit)
            {error="outbound reliable budget exceeded";return false;}
            PinLatest(playerKey);
            if(sourceKey is Key dependency)PinLatest(dependency);
            if(type=="actor_remove" || type=="actor_upsert")
            {
                var retired=new Key(sid,"actor_upsert",DemoProtocol.L(message,"eid"));
                RemoveLatest(retired);_seen.Remove(retired);
            }
            if(type=="end_session")
            {
                foreach(var expiredKey in new List<Key>(_latest.Keys))if(expiredKey.Sid==sid)RemoveLatest(expiredKey);
                _seen.RemoveWhere(key=>key.Sid==sid);
                foreach(var expiredKey in new List<Key>(_hurts.Keys))if(expiredKey.Sid==sid)_hurts.Remove(expiredKey);
                int queued=_hurtOrder.Count;
                for(int i=0;i<queued;i++)
                {
                    var old=_hurtOrder.Dequeue();if(old.Key.Sid!=sid)_hurtOrder.Enqueue(old);
                }
            }
            if(hurt && !_hurts.ContainsKey(hurtKey))
            { _hurts.Add(hurtKey,new HurtReceipt(signature,now));_hurtOrder.Enqueue((hurtKey,now)); }
            AddReliable(frame);return true;
        }
    }
    internal bool TryDequeue(out Frame frame)
    {
        lock(_gate)
        {
            var node=_ordered.First;
            if(node==null){frame=null!;return false;}
            _ordered.RemoveFirst();
            if(node.Value.Snapshot is Key key)_latest.Remove(key);else _reliableCount--;
            frame=node.Value.Frame;return true;
        }
    }
    internal void Terminate(string errorLine,string reason)
    {
        lock(_gate)
        {
            if(_terminated)return;
            ClearPending();_terminated=true;
            AddReliable(new Frame(errorLine,reason));
        }
    }
    internal void Clear() { lock(_gate){ClearPending();_terminated=true;} }
    private void ClearPending()
    { _ordered.Clear();_latest.Clear();_seen.Clear();_hurts.Clear();_hurtOrder.Clear();_reliableCount=0; }
    private void AddReliable(Frame frame) { _ordered.AddLast(new Entry(frame,null));_reliableCount++; }
    private void RemoveLatest(Key key)
    {
        if(!_latest.TryGetValue(key,out var node))return;
        _ordered.Remove(node);_latest.Remove(key);_coalesced++;
    }
    private void PinLatest(Key key)
    {
        if(!_latest.TryGetValue(key,out var node))return;
        _latest.Remove(key);node.Value.Snapshot=null;_reliableCount++;
    }
    private void PruneHurts(long now)
    {
        while(_hurtOrder.Count>0 && (now-_hurtOrder.Peek().Received)/(double)Stopwatch.Frequency>=10)
        {
            var old=_hurtOrder.Dequeue();
            if(_hurts.TryGetValue(old.Key,out var current) && current.Received==old.Received)_hurts.Remove(old.Key);
        }
    }
    private static string HurtSignature(Dictionary<string,object> message)=>
        DemoProtocol.S(message,"entityId")+"\0"+DemoProtocol.L(message,"sourceEid").ToString(CultureInfo.InvariantCulture)+"\0"+
        DemoProtocol.D(message,"damage").ToString("R",CultureInfo.InvariantCulture)+"\0"+DemoProtocol.S(message,"sourceKind","melee");
}
