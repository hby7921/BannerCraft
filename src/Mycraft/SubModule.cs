using System;
using System.Diagnostics;

using HarmonyLib;
using Mycraft.Bridge;

using Mycraft.Demo;
using Mycraft.UI;
using Mycraft.Visuals;
using Mycraft.Building;

using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Mycraft;

/// <summary>
/// BannerCraft entrypoint: a normal custom battle connects to Minecraft on command.
/// Battle-scoped only; no save data, no campaign behaviors.
/// </summary>
public sealed class SubModule : MBSubModuleBase
{
    private static Harmony? _harmony;
    public static bool PatchesReady => _harmony != null;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        DemoLog.Initialize(GetModuleFolder());
        MinecraftHudSprites.Cache(GetModuleFolder());
        MinecraftModelAssets.Cache(GetModuleFolder());
        BattleBlockScene.Cache(GetModuleFolder());
        TntScene.Cache(GetModuleFolder());
        WitherSkeletonVisual.Cache(GetModuleFolder());
        BoatVisual.Cache(GetModuleFolder());
        DemoBridgeMissionBehavior.CacheTntDamageSmoke(GetModuleFolder());
        MycraftBridgeLog.InfoSink = DemoLog.Info;
        MycraftBridgeLog.FailureSink = DemoLog.Info;
        DemoLog.Info("module loaded");
    }

    private static string GetModuleFolder()
    {
        try
        {
            var assemblyLocation = typeof(SubModule).Assembly.Location;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyLocation) ?? ".", "..", ".."));
        }
        catch (Exception e)
        {
            Trace.TraceError("[Mycraft] module folder resolve failed: {0}", e.Message);
            return ".";
        }
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        if (_harmony != null)
            return;
        try
        {
            var harmony = new Harmony("com.calradiacraft.mycraft");
            try { harmony.PatchAll(typeof(SubModule).Assembly); }
            catch { harmony.UnpatchAll(harmony.Id); throw; }
            _harmony = harmony;
            DemoLog.Info("harmony patches applied");
        }
        catch (Exception e)
        {
            DemoLog.Info("harmony patching failed: " + e);
        }
    }

    public override void OnMissionBehaviorInitialize(Mission mission)
    {
        base.OnMissionBehaviorInitialize(mission);
        if (!PatchesReady || GameNetwork.IsMultiplayer ||
            !(Game.Current?.GameType is TaleWorlds.MountAndBlade.CustomBattle.CustomGame) ||
            mission.GetMissionBehavior<TaleWorlds.MountAndBlade.MissionSpawnHandlers.CustomBattleMissionSpawnHandler>()==null)
            return;
        mission.AddMissionBehavior(new MinecraftHotbarMissionBehavior());
        mission.AddMissionBehavior(new MinecraftPlayerModelBehavior());
        // MissionCombatantsLogic sets FieldBattle in EarlyStart, after this hook.
        mission.AddMissionBehavior(new McArmyMissionBehavior());
        if(Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-army-smoke"))
            mission.AddMissionBehavior(new McArmySmokeBehavior());
        if(Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-command-only-smoke"))
            mission.AddMissionBehavior(new CommandOnlySmokeBehavior());
        if(Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-entity-scene-smoke"))
            mission.AddMissionBehavior(new EntityCombatSmokeBehavior());
        // Demo bridge: self-gates to enabled custom field battles only.
        if(Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-tnt-scene-smoke"))
            mission.AddMissionBehavior(new TntSceneSmokeBehavior());
        else mission.AddMissionBehavior(new DemoBridgeMissionBehavior());
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        DemoBridgeMissionBehavior.PumpPausedBridge();
        ModelSmokeTest.Tick(dt);
        FiansWitherPresetLaunch.Tick(dt);
        McArmySmokeBehavior.ApplicationTick(dt);
        TntSceneSmokeBehavior.ApplicationTick(dt);
        CommandOnlySmokeBehavior.ApplicationTick(dt);
        EntityCombatSmokeBehavior.ApplicationTick(dt);
    }

    protected override void OnSubModuleUnloaded()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        DemoLog.Shutdown();
        base.OnSubModuleUnloaded();
    }
}
