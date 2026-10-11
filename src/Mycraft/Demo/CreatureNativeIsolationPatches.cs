using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers.Logic;

namespace Mycraft.Demo;

[HarmonyPatch(typeof(BattleEndLogic),"CheckIsEnemySideRetreatingOrOneSideDepleted")]
internal static class CreatureRetreatResultPatch
{
    // Mission checks its logic every 0.1 seconds, while this calculation only
    // runs every second. The MissionEnded prefix below also clears stale cached
    // victory immediately before the native mission can latch its result.
    static void Postfix(BattleEndLogic __instance,ref bool ____isEnemySideDepleted,ref bool ____isEnemySideRetreating)
    {
        if(!CreatureVictoryGuard.HoldsEnemyObjective(__instance.Mission))return;
        ____isEnemySideDepleted=false;____isEnemySideRetreating=false;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(Team),nameof(Team.IsEnemyOf),new[]{typeof(Team)});
        var replacement=AccessTools.Method(typeof(CreatureRetreatResultPatch),nameof(IsOriginalArmyEnemy));
        int count=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original))
            {
                var call=new CodeInstruction(instruction){opcode=OpCodes.Call,operand=replacement};
                count++;yield return call;
            }
            else yield return instruction;
        }
        if(count!=1)throw new InvalidOperationException("Native retreat team query changed");
    }
    private static bool IsOriginalArmyEnemy(Team team,Team player)=>!NativeCreatureScene.OwnsTeam(team) && team.IsEnemyOf(player);
}

internal static class CreatureVictoryGuard
{
    internal static bool HoldsEnemyObjective(Mission mission)
    {
        if(mission==null || mission.IsMissionEnding || mission.MissionEnded || GameNetwork.IsMultiplayer || !mission.IsFieldBattle)return false;
        var player=mission.MainAgent;
        if(player?.IsActive()!=true || player.Health<=0)return false;
        var bridge=DemoBridgeMissionBehavior.Current;
        if(bridge?.IsEngaged==true && bridge.McHp<=0)return false;
        var army=McArmyMissionBehavior.Current;
        if(army?.OwnsMission(mission)==true && army.HasEnemyObjective)return true;
        if(bridge?.IsEngaged!=true || !bridge.IsConnected || !ReferenceEquals(bridge.Mission,mission))return false;
        foreach(var boss in bridge.McBosses)
        {
            if(boss.Hp<=0)continue;
            bool friendly=false;
            if(army?.OwnsMission(mission)==true)
                foreach(var slot in army.Slots)
                    if(slot.PlayerSide && slot.EntityId==boss.Id){friendly=true;break;}
            if(!friendly)return true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(BattleEndLogic),nameof(BattleEndLogic.MissionEnded))]
internal static class CreatureVictoryCommitPatch
{
    static void Prefix(BattleEndLogic __instance,ref bool ____isEnemySideDepleted,ref bool ____isEnemySideRetreating)
    {
        if(!CreatureVictoryGuard.HoldsEnemyObjective(__instance.Mission))return;
        // Original logic still processes genuine player defeat/retreat and the
        // caller's manual exit. No completed MissionResult is rewritten.
        ____isEnemySideDepleted=false;____isEnemySideRetreating=false;
    }
}

[HarmonyPatch(typeof(BattleEndLogic),nameof(BattleEndLogic.PlayerVictory),MethodType.Getter)]
internal static class CreatureVictoryQueryPatch
{
    static void Postfix(BattleEndLogic __instance,ref bool __result)
    {
        if(__result && CreatureVictoryGuard.HoldsEnemyObjective(__instance.Mission))__result=false;
    }
}

[HarmonyPatch(typeof(BattlePowerCalculationLogic),nameof(BattlePowerCalculationLogic.GetTotalTeamPower))]
internal static class CreatureTeamPowerPatch
{
    static bool Prefix(Team team,ref float __result){if(!NativeCreatureScene.OwnsTeam(team))return true;__result=0;return false;}
}
[HarmonyPatch(typeof(BattleObserverMissionLogic),nameof(BattleObserverMissionLogic.OnAgentBuild))]
internal static class CreatureObserverBuildPatch
{
    static bool Prefix(Agent agent)=>!NativeCreatureScene.OwnsAmbientAgent(agent);
}
[HarmonyPatch(typeof(BattleObserverMissionLogic),nameof(BattleObserverMissionLogic.OnAgentTeamChanged))]
internal static class CreatureObserverTeamPatch
{
    static bool Prefix(Team newTeam)=>!NativeCreatureScene.OwnsTeam(newTeam);
}
[HarmonyPatch(typeof(BattleObserverMissionLogic),nameof(BattleObserverMissionLogic.OnAgentRemoved))]
internal static class CreatureObserverRemovedPatch
{
    static bool Prefix(Agent affectedAgent,ref Agent affectorAgent)
    {
        if(NativeCreatureScene.OwnsAmbientAgent(affectedAgent))return false;
        if(NativeCreatureScene.OwnsAmbientAgent(affectorAgent))affectorAgent=null!;
        return true;
    }
}
[HarmonyPatch(typeof(AgentMoraleInteractionLogic),nameof(AgentMoraleInteractionLogic.OnAgentRemoved))]
internal static class CreatureMoraleRemovedPatch
{
    static bool Prefix(Agent affectedAgent,Agent affectorAgent)
    {
        if(NativeCreatureScene.OwnsAmbientAgent(affectedAgent))return false;
        if(affectorAgent==null && NativeCreatureScene.IsMissingSourceVictim(affectedAgent))
        {NativeCreatureScene.ClearMissingSourceVictim(affectedAgent);return false;}
        return true;
    }
}
