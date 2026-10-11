using System;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.CustomBattle.CustomBattle;
using TaleWorlds.ScreenSystem;

namespace Mycraft.Demo;

/// <summary>Starts the requested battle through the original custom-battle loader and mission.</summary>
public static class FiansWitherPresetLaunch
{
    public const string CultureId = "bannercraft_mc";
    public const string WitherTroopId = "bannercraft_mc_wither";
    public const string FianTroopId = "battanian_fian_champion";
    public const int FianCount = 200;
    public const int WitherCount = 3;
    public const string LaunchFlag = "--mycraft-fians-wither";
    public const string TenWithersLaunchFlag = "--mycraft-fians-wither-10";
    public const string SixHundredLaunchFlag = "--mycraft-600-fians-wither-10";
    public const string ThreeHundredLaunchFlag = "--mycraft-300-fians-wither-3";
    public const string ThreeHundredFiveLaunchFlag = "--mycraft-300-fians-wither-5";
    private static int _requestedWitherCount = Array.Exists(Environment.GetCommandLineArgs(), argument => argument == TenWithersLaunchFlag || argument == SixHundredLaunchFlag) ? 10 :
        Array.Exists(Environment.GetCommandLineArgs(), argument => argument == ThreeHundredFiveLaunchFlag) ? 5 : WitherCount;
    private static int _requestedFianCount = Array.Exists(Environment.GetCommandLineArgs(), argument => argument == SixHundredLaunchFlag) ? 600 :
        Array.Exists(Environment.GetCommandLineArgs(), argument => argument == ThreeHundredLaunchFlag || argument == ThreeHundredFiveLaunchFlag) ? 300 : FianCount;
    private static bool _pending = Array.Exists(Environment.GetCommandLineArgs(), argument => argument == LaunchFlag || argument == TenWithersLaunchFlag || argument == SixHundredLaunchFlag || argument == ThreeHundredLaunchFlag || argument == ThreeHundredFiveLaunchFlag);
    private static bool _loading;
    private static float _elapsed;

    /// <summary>Queue work; a console callback must not mutate game/screen collections.</summary>
    public static string Request(int witherCount=WitherCount,int fianCount=FianCount)
    {
        if(witherCount<1 || witherCount>16)return "凋零数量必须为 1 到 16。";
        if(fianCount<1 || fianCount>600)return "巴弓数量必须为 1 到 600。";
        if (GameNetwork.IsMultiplayer) return "这个预设只支持单人自定义野战。";
        if (Mission.Current != null) return "请先退出当前战斗，再在主菜单或自定义战斗选择页输入 mycraft.fians_wither。";
        var game = Game.Current;
        if (game != null && (!(game.GameType is CustomGame) || !(game.GameStateManager.ActiveState is CustomBattleState)))
            return "请回到主菜单或自定义战斗选择页，再输入 mycraft.fians_wither。";
        if (_pending) return _requestedFianCount+" 巴弓对 "+_requestedWitherCount+" 凋零的战局正在准备。";
        _requestedWitherCount=witherCount;
        _requestedFianCount=fianCount;
        _pending = true;
        _loading = false;
        _elapsed = 0;
        return "已准备创建战局：你指挥 "+fianCount+" 名巴丹尼亚费奥纳冠军，对阵 "+witherCount+" 个凋零。进入后在 MC 执行 /mycraft connect。";
    }

    /// <summary>Call once from SubModule.OnApplicationTick, after Harmony is ready.</summary>
    public static void Tick(float dt)
    {
        if (!_pending || !SubModule.PatchesReady || ScreenManager.TopScreen == null) return;
        if (!float.IsNaN(dt) && !float.IsInfinity(dt)) _elapsed += Math.Min(1f, Math.Max(0f, dt));
        if (_elapsed < 5f) return;
        try
        {
            if (_elapsed > 120f) throw new InvalidOperationException("等待自定义游戏加载超时，请回主菜单后重试 mycraft.fians_wither。");
            if (GameNetwork.IsMultiplayer || Mission.Current != null)
                throw new InvalidOperationException("预设只能从主菜单或自定义战斗选择页创建；当前战斗保留。");
            var game = Game.Current;
            if (game == null)
            {
                if (!_loading)
                {
                    _loading = true;
                    DemoLog.Diagnostic("FIANS_WITHER_PRESET_LOADING: original CustomGameManager");
                    MBGameManager.StartNewGame(new CustomGameManager());
                }
                return;
            }
            if (!(game.GameType is CustomGame))
                throw new InvalidOperationException("当前游戏不是自定义战斗，请返回主菜单后重试。");
            if (!(game.GameStateManager.ActiveState is CustomBattleState)) return;
            var data = CreateBattleData(game,_requestedWitherCount,_requestedFianCount);
            _pending = false;
            _loading = false;
            DemoLog.Diagnostic("FIANS_WITHER_PRESET_START: commander + "+_requestedFianCount+" battanian_fian_champion; enemy "+_requestedWitherCount+" bannercraft_mc_wither; no enemy human commander");
            CustomBattleHelper.StartGame(data);
        }
        catch (Exception exception)
        {
            _pending = false;
            _loading = false;
            DemoLog.Diagnostic("FIANS_WITHER_PRESET_FAILED: " + exception);
            TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage("BannerCraft：" + exception.Message));
        }
    }

    /// <summary>Uses real registered troops and validates exact member counts before native start.</summary>
    public static CustomBattleData CreateBattleData(Game game,int witherCount=WitherCount,int fianCount=FianCount)
    {
        if(witherCount<1 || witherCount>16)throw new ArgumentOutOfRangeException(nameof(witherCount));
        if(fianCount<1 || fianCount>600)throw new ArgumentOutOfRangeException(nameof(fianCount));
        if (game == null || !(game.GameType is CustomGame)) throw new InvalidOperationException("尚未加载自定义游戏。");
        var player = RequireTroop(game, "commander_1");
        var enemyGeneral = RequireTroop(game, "commander_2");
        var fians = RequireTroop(game, FianTroopId);
        var wither = RequireTroop(game, WitherTroopId);
        var culture = game.ObjectManager.GetObject<BasicCultureObject>(CultureId)
            ?? throw new InvalidOperationException("缺少 MC 自定义阵营数据，请检查 BannerCraft 模组安装。");
        if (fians.Culture?.StringId != "battania" || !fians.IsSoldier || wither.Culture != culture || !wither.IsSoldier)
            throw new InvalidOperationException("预设兵种的阵营或士兵数据不正确。");
        var playerParty = new CustomBattleCombatant(new TextObject("{=!}"+fianCount+" 巴弓"), fians.Culture,
            new Banner(fians.Culture.Banner, fians.Culture.Color, fians.Culture.Color2));
        playerParty.Side = BattleSideEnum.Attacker;
        playerParty.SetGeneral(player);
        playerParty.AddCharacter(player, 1);
        playerParty.AddCharacter(fians, fianCount);
        var enemyParty = new CustomBattleCombatant(new TextObject("{=!}MC："+witherCount+" 个凋零"), culture,
            new Banner(culture.Banner, culture.Color, culture.Color2));
        enemyParty.Side = BattleSideEnum.Defender;
        // Native metadata expects a General, but no human commander joins this enemy roster.
        enemyParty.SetGeneral(enemyGeneral);
        enemyParty.AddCharacter(wither, witherCount);
        if (playerParty.NumberOfHealthyMembers != fianCount + 1 || enemyParty.NumberOfHealthyMembers != witherCount ||
            playerParty.Characters.Count(character => character.StringId == FianTroopId) != fianCount ||
            enemyParty.Characters.Any(character => character.StringId != WitherTroopId))
            throw new InvalidOperationException("预设人数检查失败，已停止创建战斗。");
        return new CustomBattleData
        {
            PlayerCharacter = player,
            PlayerParty = playerParty,
            EnemyParty = enemyParty,
            IsPlayerGeneral = true,
            IsPlayerAttacker = true,
            GameTypeStringId = "Battle",
            SceneId = "battle_terrain_biome_096",
            SceneLevel = "",
            SeasonId = "summer",
            TimeOfDay = 12f
        };
    }

    private static BasicCharacterObject RequireTroop(Game game, string id) =>
        game.ObjectManager.GetObject<BasicCharacterObject>(id)
        ?? throw new InvalidOperationException("缺少预设兵种：" + id);
}
