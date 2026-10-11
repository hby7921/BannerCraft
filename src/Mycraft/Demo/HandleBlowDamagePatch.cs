using System;
using System.Collections.Generic;
using System.Diagnostics;

using HarmonyLib;

using Mycraft.Patches;

using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// The selected native commander keeps their real armor outside the bridge.
// Minecraft applies its own armor after receiving the native incoming blow.
[HarmonyPatch(typeof(Agent), nameof(Agent.GetBaseArmorEffectivenessForBodyPart))]
internal static class MinecraftPlayerArmorPatch
{
    static void Postfix(Agent __instance, ref float __result)
    {
        if(DemoBridgeMissionBehavior.Current?.OwnsPlayer(__instance)==true)__result=0;
    }
}

/// <summary>
/// Damage arbitration for the demo bridge (API_EVIDENCE.md conclusion A):
/// Only MC-confirmed synthetic blows may damage mirrored enemies. Native player
/// collisions are suppressed. Enemy damage is forwarded before BL can deduct
/// player health; zero damage returns early from HandleBlow after its sound path.
/// </summary>
[HarmonyPatch(typeof(Agent), "HandleBlow")]
internal static class HandleBlowDamagePatch
{
    static void Prefix(Agent __instance, ref Blow b, in AttackCollisionData collisionData)
    {
        try
        {
            var army=McArmyMissionBehavior.Current;
            if(__instance!=null && army?.OwnsMission(__instance.Mission)==true && NativeCreatureScene.OwnsArmyAgent(__instance))
            {
                // These original enemy soldiers already count toward native
                // deployment and victory before the MC session has engaged.
                // A hidden pending slot cannot die to a native arrow meanwhile.
                if(army.IsPending(__instance)){b.InflictedDamage=0;return;}
                if(b.InflictedDamage>0)army.RecordAttacker(__instance,b.OwnerId);
            }
            var demo = DemoBridgeMissionBehavior.Current;
            if (demo == null || !demo.OwnsCombat || __instance == null)
            {
                if(NativeCreatureScene.OwnsArmyAgent(__instance))b.InflictedDamage=0;
                return;
            }
            if(NativeCreatureScene.OwnsAgent(__instance))
            {
                var sourceKind=b.WeaponRecord.IsMissile ?
                    (b.WeaponRecord.WeaponClass==WeaponClass.Arrow || b.WeaponRecord.WeaponClass==WeaponClass.Bolt ? "arrow":"projectile") : "melee";
                demo.ForwardCreatureHurt(__instance,b.OwnerId,b.InflictedDamage,sourceKind);
                b.InflictedDamage=0;
                return;
            }
            if (demo.Player!=null && !ReferenceEquals(__instance,demo.Player) && __instance.IsEnemyOf(demo.Player))
            {
                    // Synthetic MC damage enters the native attribution/death path once.
                if (demo.Player != null && b.OwnerId == demo.Player.Index)
                {
                    if (demo.ApplyingMcHit)
                    {
                        return;
                    }
                    b.InflictedDamage = 0;
                }
            }
            else if (ReferenceEquals(__instance, demo.Player))
            {
                // Blow against the player from the demo enemy: MC owns player health.
                if (b.InflictedDamage > 0)
                {
                    // HandleBlow plays body-hit sound before the zero-damage early return.
                    // The corresponding current-collision postfix supplies shield feedback.
                    var attacker=Mission.Current?.FindAgentWithIndex(b.OwnerId);
                    if(MinecraftShieldFeedbackPatch.IsCandidate(in collisionData) && demo.CanPredictShieldBlock(attacker))
                    {
                        b.BlowFlag|=BlowFlags.NoSound;
                        // Couple suppression to its replacement even if the final native
                        // reaction later elects to continue collision checking.
                        demo.NotifyShieldImpact(attacker!,collisionData.CollisionGlobalPosition);
                    }
                    demo.ForwardPlayerHurt(b.InflictedDamage,b.OwnerId);
                    b.InflictedDamage = 0;
                }
            }
        }
        catch (Exception e)
        {
            b.InflictedDamage = 0;
            MinecraftHeroState.LogFailure("HandleBlow damage arbitration", e);
            DemoBridgeMissionBehavior.Current?.Abort("damage arbitration failed");
        }
    }
}
