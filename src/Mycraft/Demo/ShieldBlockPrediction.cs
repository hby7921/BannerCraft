using System;

namespace Mycraft.Demo;

// This only predicts the current host melee reaction. Minecraft still settles damage/durability.
public static class ShieldBlockPrediction
{
    public static bool CanBounce(bool engaged,bool held,bool ready,long stateAgeMs,
        double lookX,double lookY,double sourceX,double sourceY)
    {
        if(!engaged || !held || !ready || stateAgeMs<0 || stateAgeMs>350)return false;
        if(!CombatEventLedger.IsFinite(lookX)||!CombatEventLedger.IsFinite(lookY)||
           !CombatEventLedger.IsFinite(sourceX)||!CombatEventLedger.IsFinite(sourceY))return false;
        if(lookX*lookX+lookY*lookY<.000001 || sourceX*sourceX+sourceY*sourceY<.000001)return false;
        // Strictly front-facing; the exact side boundary is left to the authoritative result.
        return lookX*sourceX+lookY*sourceY>0;
    }
}
