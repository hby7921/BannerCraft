using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.CustomBattle.CustomBattle;

namespace Mycraft.Demo;

// 1.5.4 counts the commander inside the slider. GetTroopCounts subtracts one;
// zero produces negative troop counts. Restore vanilla bounds before dispatch.
[HarmonyPatch(typeof(CustomBattleVM), nameof(CustomBattleVM.ExecuteStart))]
internal static class DemoArmyMinimumPatch
{
    static void Prefix(CustomBattleVM __instance)
    {
        if(!DemoConfig.Load(DemoConfig.ModuleFolder).Enabled)return;
        var player=__instance.PlayerSide.CompositionGroup;
        var enemy=__instance.EnemySide.CompositionGroup;
        player.MinArmySize=__instance.GameTypeSelectionGroup.SelectedPlayerType==CustomBattlePlayerType.Commander?1:2;
        enemy.MinArmySize=1;
        player.ArmySize=Math.Max(player.ArmySize,player.MinArmySize);
        enemy.ArmySize=Math.Max(enemy.ArmySize,enemy.MinArmySize);
    }
}
