using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

internal static class NativeMcDeath
{
    internal static void Apply(Mission mission,Agent victim,Agent? source)
    {
        if(!victim.IsActive())return;
        // CustomBattleMoraleModel dereferences the affector's Formation even
        // for environmental deaths. Use the victim for an unattributed MC
        // death, rather than an absent native index or an unrelated enemy.
        var owner=source!=null&&ReferenceEquals(source.Mission,mission)&&
            ReferenceEquals(mission.FindAgentWithIndex(source.Index),source)&&source.IsActive()
            ?source:victim;
        var blow=new Blow(owner.Index) {DamageType=DamageTypes.Blunt,GlobalPosition=victim.Position,
            InflictedDamage=(int)Math.Max(1,victim.Health),DamageCalculated=true};
        blow.WeaponRecord.FillAsMeleeBlow(null,null,-1,-1);
        DemoLog.Diagnostic("MC native death: victim="+victim.Index+", owner="+owner.Index+", self="+ReferenceEquals(owner,victim));
        victim.Die(blow);
    }
}
