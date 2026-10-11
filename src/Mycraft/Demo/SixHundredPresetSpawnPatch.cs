using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.MissionSpawnHandlers;

namespace Mycraft.Demo;

// Native custom battles normally allocate only the user's BattleSize (currently
// 400). The exact 600 Fian / 10 Wither roster needs all 611 original troops in
// its initial deployment, without changing the user's other battles/settings.
[HarmonyPatch(typeof(DefaultBattleMissionAgentSpawnLogic), nameof(DefaultBattleMissionAgentSpawnLogic.InitWithSinglePhase))]
internal static class SixHundredPresetSpawnPatch
{
    private static readonly FieldInfo CombatantsField = AccessTools.Field(typeof(MissionCombatantsLogic), "BattleCombatants")
        ?? throw new InvalidOperationException("Native custom battle combatant roster is unavailable");

    static void Prefix(DefaultBattleMissionAgentSpawnLogic __instance,
        int defenderTotalSpawn, int attackerTotalSpawn, int defenderInitialSpawn, int attackerInitialSpawn,
        bool spawnDefenders, bool spawnAttackers, ref MissionSpawnSettings spawnSettings)
    {
        var mission = __instance.Mission;
        if (GameNetwork.IsMultiplayer || Game.Current?.GameType is not TaleWorlds.MountAndBlade.CustomBattle.CustomGame ||
            mission == null || !mission.IsFieldBattle || mission.PlayerTeam == null || !spawnDefenders || !spawnAttackers ||
            mission.GetMissionBehavior<CustomBattleMissionSpawnHandler>() == null)
            return;
        var combatants = mission.GetMissionBehavior<MissionCombatantsLogic>();
        if (combatants == null || CombatantsField.GetValue(combatants) is not IEnumerable<IBattleCombatant> roster)
            return;
        var original = roster.ToArray();
        if (original.Length != 2 || original.Any(party => party is not CustomBattleCombatant)) return;
        var parties = original.Cast<CustomBattleCombatant>().ToArray();
        var players = parties.Where(party => party.Side == mission.PlayerTeam.Side).ToArray();
        var enemies = parties.Where(party => party.Side == mission.PlayerTeam.Side.GetOppositeSide()).ToArray();
        if (players.Length != 1 || enemies.Length != 1) return;
        var player = players[0]; var enemy = enemies[0];
        var playerTroops = player.Characters.ToArray(); var enemyTroops = enemy.Characters.ToArray();
        if (player.General?.StringId != "commander_1" || playerTroops.Length != 601 || enemyTroops.Length != 10 ||
            playerTroops.Count(troop => troop.StringId == "commander_1") != 1 ||
            playerTroops.Count(troop => troop.StringId == "battanian_fian_champion") != 600 ||
            enemyTroops.Any(troop => troop.StringId != "bannercraft_mc_wither"))
            return;
        int defenders = player.Side == BattleSideEnum.Defender ? 601 : 10;
        int attackers = player.Side == BattleSideEnum.Attacker ? 601 : 10;
        if (defenderTotalSpawn != defenders || attackerTotalSpawn != attackers ||
            defenderInitialSpawn != defenders || attackerInitialSpawn != attackers)
            return;
        int nativeLimit = DefaultBattleMissionAgentSpawnLogic.MaxNumberOfTroopsForMission;
        if (nativeLimit < 611)
            throw new InvalidOperationException("Native agent capacity cannot deploy the requested 611 troops");

        var previous = spawnSettings;
        DemoLog.Diagnostic("MC_600_FULL_DEPLOYMENT_BEFORE: player601 (commander1 + Fian600), enemy10; " +
            "requestedDefenders=" + defenders + ", requestedAttackers=" + attackers +
            ", configuredBattleSize=" + __instance.BattleSize + ", nativeTroopLimit=" + nativeLimit +
            ", method=" + previous.InitialTroopsSpawnMethod);
        // Copy every current 1.4.8 spawn setting. Only initial allocation changes;
        // the native supplier, origins, formations, timing and casualty path stay.
        spawnSettings = new MissionSpawnSettings(
            MissionSpawnSettings.InitialSpawnMethod.FreeAllocation,
            previous.ReinforcementTroopsTimingMethod,
            previous.ReinforcementTroopsSpawnMethod,
            previous.GlobalReinforcementInterval,
            previous.ReinforcementBatchPercentage,
            previous.DesiredReinforcementPercentage,
            previous.ReinforcementWavePercentage,
            previous.MaximumReinforcementWaveCount,
            previous.DefenderReinforcementBatchPercentage,
            previous.AttackerReinforcementBatchPercentage,
            previous.DefenderAdvantageFactor,
            previous.MaximumBattleSideRatio);
        DemoLog.Diagnostic("MC_600_FULL_DEPLOYMENT_PLAN: method=" + spawnSettings.InitialTroopsSpawnMethod +
            ", originalInitialDefenders=" + defenderInitialSpawn + ", originalInitialAttackers=" + attackerInitialSpawn +
            "; native deployment still supplies all original troop origins; global BattleSize unchanged");
    }
}
