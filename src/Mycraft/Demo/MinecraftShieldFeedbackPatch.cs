using System;
using HarmonyLib;
using Mycraft.Patches;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// The callback's ref reaction is returned directly to the native engine. Do not use
// NextBlowCollisionReactionOverride: it can affect another agent's next collision.
[HarmonyPatch(typeof(Mission),"MeleeHitCallback")]
internal static class MinecraftShieldFeedbackPatch
{
    internal static bool IsCandidate(in AttackCollisionData collisionData)=>collisionData.IsColliderAgent &&
        collisionData.CollisionResult==CombatCollisionResult.StrikeAgent && !collisionData.IsMissile &&
        !collisionData.IsAlternativeAttack && !collisionData.IsHorseCharge && !collisionData.IsFallDamage;

    static void Postfix(ref AttackCollisionData collisionData,Agent attacker,Agent victim,
        ref MeleeCollisionReaction colReaction,ref HitParticleResultData hitParticleResultData,bool crushedThroughWithoutAgentCollision)
    {
        try
        {
            var bridge=DemoBridgeMissionBehavior.Current;
            if(bridge==null || !ReferenceEquals(victim,bridge.Player) || !IsCandidate(in collisionData) ||
               crushedThroughWithoutAgentCollision || colReaction==MeleeCollisionReaction.ContinueChecking ||
               !bridge.CanPredictShieldBlock(attacker))return;
            // Native body damage was already forwarded by HandleBlow. Only the visual/
            // momentum response is predicted here, from a fresh MC blocking state.
            colReaction=MeleeCollisionReaction.Bounced;
            hitParticleResultData.Reset();
            bridge.NotifyShieldImpact(attacker,collisionData.CollisionGlobalPosition);
        }
        catch(Exception e){MinecraftHeroState.LogFailure("MC shield collision feedback",e);}
    }
}
