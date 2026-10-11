using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Native combat maps wheel up/down to primary/offhand WieldNextWeapon.
// MC selection is handled once by the battle input reader, not by each native hand.
[HarmonyPatch(typeof(Agent),nameof(Agent.WieldNextWeapon))]
internal static class MinecraftWheelPatch
{
    static bool Prefix(Agent __instance)
    {
        var bridge=DemoBridgeMissionBehavior.Current;
        return bridge==null || !bridge.OwnsHotbarWheel || !ReferenceEquals(__instance,bridge.Player);
    }
}
