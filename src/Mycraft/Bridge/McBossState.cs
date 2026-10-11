using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

public sealed class McBossState
{
    public readonly string Id,Name;
    public readonly double Hp,MaxHp;
    public double Fraction=>Math.Max(0,Math.Min(1,Hp/MaxHp));
    public McBossState(Dictionary<string,object> state)
    {
        Id=DemoProtocol.S(state,"entityId");Name=DemoProtocol.S(state,"bossName");
        if(string.IsNullOrEmpty(Name) || Name=="Wither")Name="凋零";
        Hp=DemoProtocol.D(state,"hp");MaxHp=DemoProtocol.D(state,"maxHp");
        if(double.IsNaN(Hp) || double.IsInfinity(Hp) || double.IsNaN(MaxHp) || double.IsInfinity(MaxHp) || Hp<0 || MaxHp<=0 || Hp>MaxHp)
            throw new ArgumentException("Invalid native boss life");
    }
}
