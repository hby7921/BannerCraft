using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

// Native projectile/kinetic damage can arrive long after a click and hit multiple actors.
public sealed class WeaponHitLedger
{
    public enum Result { Accepted, Duplicate, Invalid, Full }
    private readonly Dictionary<string,long> _events=new Dictionary<string,long>();
    public Result Register(string id,double damage,long now)
    {
        if(string.IsNullOrEmpty(id)||id.Length>128||double.IsNaN(damage)||double.IsInfinity(damage)||damage<=0||damage>10000)
            return Result.Invalid;
        var expired=new List<string>();
        foreach(var pair in _events)if(pair.Value<now)expired.Add(pair.Key);
        foreach(var key in expired)_events.Remove(key);
        if(_events.ContainsKey(id))return Result.Duplicate;
        if(_events.Count>=4096)return Result.Full;
        _events.Add(id,now+10000);
        return Result.Accepted;
    }
    public void Clear()=>_events.Clear();
}
