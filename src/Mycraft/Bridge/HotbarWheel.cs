using System;

namespace Mycraft.Bridge;

public sealed class HotbarWheel
{
    private double _remainder;
    public int? Apply(int selected, double delta)
    {
        if(selected<0 || selected>8 || double.IsNaN(delta) || double.IsInfinity(delta))
            throw new ArgumentOutOfRangeException(nameof(selected));
        _remainder+=Math.Max(-1080,Math.Min(1080,delta));
        var notches=(int)(_remainder/120);
        if(notches==0)return null;
        _remainder-=notches*120;
        return ((selected-notches)%9+9)%9;
    }
    public void Clear()=>_remainder=0;
}
