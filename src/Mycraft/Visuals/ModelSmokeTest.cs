using System;
using System.Linq;
using Mycraft.Demo;
using Mycraft.Patches;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.CustomBattle.CustomBattle;
using TaleWorlds.ScreenSystem;

namespace Mycraft.Visuals;

// Explicit diagnostic launch flag; normal player launches never enter this path.
internal static class ModelSmokeTest
{
    private static readonly bool Enabled = Environment.GetCommandLineArgs().Contains("--mycraft-model-smoke");
    internal static bool RenderPlayerModel => Enabled && !Environment.GetCommandLineArgs().Contains("--mycraft-command-only-smoke") && !Environment.GetCommandLineArgs().Contains("--mycraft-entity-scene-smoke");
    private static float _time;
    private static bool _gameStarted;
    private static bool _battleStarted;

    public static void Tick(float dt)
    {
        if (!Enabled || !SubModule.PatchesReady || _battleStarted) return;
        _time += dt;
        if (_time < 5 || ScreenManager.TopScreen == null) return;
        try
        {
            if (!_gameStarted && Game.Current == null)
            {
                _gameStarted = true;
                DemoLog.Diagnostic("MODEL smoke: starting custom game loader");
                MBGameManager.StartNewGame(new CustomGameManager());
                return;
            }
            var game = Game.Current;
            if (!_gameStarted || game == null || !(game.GameType is CustomGame) ||
                !(game.GameStateManager.ActiveState is CustomBattleState)) return;
            var player = game.ObjectManager.GetObject<BasicCharacterObject>(Environment.GetCommandLineArgs().Contains("--mycraft-command-only-smoke")?"commander_3":"commander_1");
            var enemy = game.ObjectManager.GetObject<BasicCharacterObject>("commander_2");
            if (player == null || enemy == null) throw new InvalidOperationException("Smoke test characters missing");
            var playerParty = new CustomBattleCombatant(new TextObject("Model test player"),player.Culture,new Banner());
            playerParty.Side = BattleSideEnum.Attacker; playerParty.AddCharacter(player,1); playerParty.SetGeneral(player);
            var enemyParty = new CustomBattleCombatant(new TextObject("Model test enemy"),enemy.Culture,new Banner());
            enemyParty.Side = BattleSideEnum.Defender; enemyParty.AddCharacter(enemy,1); enemyParty.SetGeneral(enemy);
            var data = new CustomBattleData
            {
                PlayerCharacter=player, PlayerParty=playerParty, EnemyParty=enemyParty,
                IsPlayerGeneral=true, GameTypeStringId="Battle", SceneId="battle_terrain_biome_096",
                SceneLevel="", SeasonId="summer", TimeOfDay=12f
            };
            _battleStarted=true;
            DemoLog.Diagnostic("MODEL smoke: opening 1v1 mission");
            CustomBattleHelper.StartGame(data);
        }
        catch(Exception e) { _battleStarted=true; MinecraftHeroState.LogFailure("model smoke",e); }
    }
}
