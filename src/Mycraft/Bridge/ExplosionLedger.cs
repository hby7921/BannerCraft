using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

// Reliable TNT messages may retry; visuals and each actor's damage are committed at most once.
public sealed class ExplosionLedger
{
    public enum Result { Accepted,Duplicate,UnknownBlast,Full }
    private readonly Dictionary<string,long> _events=new Dictionary<string,long>();
    private readonly Dictionary<string,long> _blasts=new Dictionary<string,long>();
    private const int Capacity=4096;
    private void Expire(long now)
    {
        var old=new List<string>();
        foreach(var pair in _events)if(pair.Value<now)old.Add(pair.Key);
        foreach(var key in old)_events.Remove(key);old.Clear();
        foreach(var pair in _blasts)if(pair.Value<now)old.Add(pair.Key);
        foreach(var key in old)_blasts.Remove(key);
    }
    public Result Register(string eventId,string blastId,bool explosion,long now)
    {
        Expire(now);
        if(_events.ContainsKey(eventId))return Result.Duplicate;
        if(!explosion&&!_blasts.ContainsKey(blastId))return Result.UnknownBlast;
        if(_events.Count>=Capacity || explosion&&_blasts.Count>=256)return Result.Full;
        _events[eventId]=now+10000;
        if(explosion)_blasts[blastId]=now+10000;
        return Result.Accepted;
    }
    public void Clear(){_events.Clear();_blasts.Clear();}
}
