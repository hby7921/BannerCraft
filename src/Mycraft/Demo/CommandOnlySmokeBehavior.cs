using System;
using System.Diagnostics;
using Mycraft.UI;
using Mycraft.Visuals;
using TaleWorlds.Core;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace Mycraft.Demo;

// Installed only under --mycraft-command-only-smoke. The loopback peer supplies
// synthetic authority; this checks the actual native ownership/restore lifecycle.
public sealed class CommandOnlySmokeBehavior : MissionBehavior
{
    private static Mission? _pendingEnd;
    private static WeakReference<Mission>? _monitorMission;
    private static bool _monitorActive, _endObserved;
    private static long _endAt, _observeAt;
    private readonly Stopwatch _clock = new Stopwatch();
    private Agent? _player, _horse, _other;
    private BasicCharacterObject? _character;
    private readonly string[] _weapons = new string[5];
    private Widget? _nativeHud;
    private bool _nativeHudVisible;
    private float _armor, _otherArmor;
    private int _phase, _restoreFrames;
    private long _phaseAt;
    private bool _finished;

    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

    public override void AfterStart()
    {
        base.AfterStart();
        _clock.Restart();
        DemoLog.Diagnostic("COMMAND_ONLY_SMOKE_BEGIN: native commander_3; synthetic loopback authority; no user save");
    }

    public override void OnMissionTick(float dt)
    {
        if (_finished || !_clock.IsRunning) return;
        try
        {
            if (_clock.ElapsedMilliseconds > 75000)
                throw new InvalidOperationException("Command-only mission diagnostic exceeded 75 seconds");
            var player = Mission.MainAgent;
            if (player == null || !player.IsActive() || Mission.Mode != MissionMode.Battle) return;
            var model = Mission.GetMissionBehavior<MinecraftPlayerModelBehavior>();
            var hud = Mission.GetMissionBehavior<MinecraftHotbarMissionBehavior>();
            var demo = Mission.GetMissionBehavior<DemoBridgeMissionBehavior>();
            Require(model != null && hud != null && demo != null, "Required production behaviors missing");
            if (_phase == 0)
            {
                if (_clock.ElapsedMilliseconds < 2000) return;
                Require(Game.Current.ObjectManager.GetObject<BasicCharacterObject>("mc_steve") == null &&
                    Game.Current.ObjectManager.GetObject<BasicCharacterObject>("mc_demo_soldier") == null,
                    "Removed MC characters remain registered in native object roster");
                Require(player.Character.StringId == "commander_3", "Diagnostic loader did not select native commander_3");
                Require(player.AgentVisuals.GetVisible(), "Native body hidden before connection");
                Require(!model!.IsModelInstalled && !hud!.IsHotbarVisible && !demo!.OwnsPlayer(player),
                    "MC model/HUD/ownership appeared before command connection");
                Require(player.Equipment[EquipmentIndex.Weapon0].CurrentUsageItem?.WeaponClass == WeaponClass.Bow,
                    "Native commander_3 slot zero must remain its real bow before connection");
                _player = player; _character = player.Character; _horse = player.MountAgent;
                Require(_horse != null && _horse.IsActive(), "Native commander_3 did not spawn mounted");
                for (var i = 0; i < 5; i++) _weapons[i] = WeaponFingerprint(player.Equipment[(EquipmentIndex)i]);
                _armor = player.GetBaseArmorEffectivenessForBodyPart(BoneBodyPartType.Chest);
                Require(_armor > 0, "Native chest armor was disabled before connection");
                foreach (var agent in Mission.Agents)
                    if (agent.IsActive() && agent.IsHuman && !ReferenceEquals(agent, player)) { _other = agent; break; }
                Require(_other != null, "Diagnostic enemy missing");
                _otherArmor = _other!.GetBaseArmorEffectivenessForBodyPart(BoneBodyPartType.Chest);
                _nativeHud = FindNativeHud();
                Require(_nativeHud != null, "Native AgentStatus HUD could not be observed");
                _nativeHudVisible = _nativeHud!.IsVisible;
                Require(_nativeHudVisible, "Native AgentStatus HUD hidden before connection");
                _phase = 1; _phaseAt = _clock.ElapsedMilliseconds;
                DemoLog.Diagnostic("COMMAND_ONLY_PRECONNECT_PASS: native roster clean; body/bow/horse/HUD visible; armor=" + _armor +
                    "; five slots=" + string.Join(";", _weapons));
            }
            else if (_phase == 1)
            {
                if (_clock.ElapsedMilliseconds - _phaseAt > 45000)
                    throw new InvalidOperationException("Loopback authority did not complete takeover within 45 seconds");
                if (!demo!.OwnsPlayer(player) || !model!.IsModelInstalled || !hud!.IsHotbarVisible) return;
                Require(ReferenceEquals(player, _player) && ReferenceEquals(player.Character, _character),
                    "Takeover replaced native player identity");
                Require(player.MountAgent == null && _horse!.IsActive(), "Takeover did not naturally dismount while retaining the real horse");
                var carrier = player.Equipment[EquipmentIndex.Weapon0];
                Require(!carrier.IsEmpty && carrier.CurrentUsageItem.WeaponClass == WeaponClass.OneHandedSword,
                    "Diamond sword did not install an independent native one-handed carrier");
                Require(carrier.Item.StringId != _weapons[0].Split('|')[0], "Native bow was reused as a melee carrier");
                Require(player.GetBaseArmorEffectivenessForBodyPart(BoneBodyPartType.Chest) == 0,
                    "Connected owned player still receives native armor reduction");
                Require(_other!.IsActive() && !demo.OwnsPlayer(_other) &&
                    Math.Abs(_other.GetBaseArmorEffectivenessForBodyPart(BoneBodyPartType.Chest) - _otherArmor) < .001f,
                    "MC armor ownership affected the ordinary enemy");
                // The display tick hides this node after the hotbar has installed.
                if (_nativeHud!.IsVisible) return;
                DemoLog.Diagnostic("COMMAND_ONLY_CONNECT_PASS: same native identity; real horse retained; MC body/hotbar; independent sword carrier=" +
                    carrier.Item.StringId + "; owned armor=0; enemy armor=" + _otherArmor);
                demo.EndCommandOnlySmokeSession();
                _phase = 2; _phaseAt = _clock.ElapsedMilliseconds; _restoreFrames = 0;
            }
            else if (_phase == 2)
            {
                // Check on later mission/display frames, never within Teardown itself.
                if (++_restoreFrames < 3 || _clock.ElapsedMilliseconds - _phaseAt < 250) return;
                Require(ReferenceEquals(player, _player) && ReferenceEquals(player.Character, _character), "Restore replaced native player");
                Require(player.AgentVisuals.GetVisible(), "Restore did not show original native body");
                Require(!model!.IsModelInstalled && !hud!.IsHotbarVisible && !demo!.OwnsPlayer(player),
                    "MC ownership/model/hotbar survived session teardown");
                for (var i = 0; i < 5; i++)
                    Require(WeaponFingerprint(player.Equipment[(EquipmentIndex)i]) == _weapons[i], "Native weapon slot/amount/modifier/usage not restored: " + i);
                Require(Math.Abs(player.GetBaseArmorEffectivenessForBodyPart(BoneBodyPartType.Chest) - _armor) < .001f,
                    "Native armor did not restore after connection");
                Require(_nativeHud!.IsVisible == _nativeHudVisible, "Native AgentStatus HUD did not restore");
                DemoLog.Diagnostic("COMMAND_ONLY_RESTORE_PASS: native body/five weapon fingerprints and amounts/armor/HUD restored; MC model/HUD hidden");
                _finished = true;
                _pendingEnd = Mission; _monitorMission = new WeakReference<Mission>(Mission);
                _monitorActive = true; _endObserved = false; _endAt = _observeAt = 0;
            }
        }
        catch (Exception e) { Fail(e); }
    }

    private static string WeaponFingerprint(MissionWeapon weapon) => weapon.IsEmpty ? "empty" :
        weapon.Item.StringId + "|" + (weapon.ItemModifier?.StringId ?? "") + "|" + weapon.Amount + "|" + weapon.CurrentUsageIndex;

    private static Widget? FindNativeHud()
    {
        var screen = ScreenManager.TopScreen;
        if (screen == null) return null;
        foreach (var layer in screen.Layers)
        {
            if (!(layer is GauntletLayer native) || native.UIContext == null) continue;
            var band = native.UIContext.Root.GetFirstInChildrenAndThisRecursive(w => w.VisualDefinition?.Name == "AgentStatus");
            if (band != null) return band;
            var health = native.UIContext.Root.FindChild("HeroHealthWidget", true);
            if (health != null) return health.ParentWidget ?? health;
        }
        return null;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private void Fail(Exception error)
    {
        _finished = true;
        _monitorActive = false; _pendingEnd = null; _monitorMission = null;
        DemoLog.Diagnostic("COMMAND_ONLY_SMOKE_FAIL: " + error);
    }

    public static void ApplicationTick(float dt)
    {
        if (!_monitorActive) return;
        try
        {
            if (_pendingEnd != null)
            {
                var mission = _pendingEnd; _pendingEnd = null; _endAt = Stopwatch.GetTimestamp();
                DemoLog.Diagnostic("COMMAND_ONLY_TEARDOWN_REQUEST: normal Mission.EndMission on application tick");
                mission.EndMission(); return;
            }
            var now = Stopwatch.GetTimestamp();
            var over = _endObserved && (_monitorMission == null || !_monitorMission.TryGetTarget(out var observed) || observed.CurrentState == Mission.State.Over);
            if (!over)
            {
                if (_endAt != 0 && (now - _endAt) / (double)Stopwatch.Frequency > 30)
                    throw new InvalidOperationException("Normal native EndMission did not complete within 30 seconds");
                return;
            }
            if (_observeAt == 0)
            {
                _observeAt = now;
                DemoLog.Diagnostic("COMMAND_ONLY_TEARDOWN_OBSERVE: OnEndMission completed; State.Over");
                return;
            }
            if ((now - _observeAt) / (double)Stopwatch.Frequency < 10) return;
            DemoLog.Diagnostic("COMMAND_ONLY_FINAL_PASS: native lifecycle restored; normal EndMission/State.Over and ten seconds of application ticks survived");
            _monitorActive = false; _monitorMission = null;
        }
        catch (Exception e)
        {
            _monitorActive = false; _pendingEnd = null; _monitorMission = null;
            DemoLog.Diagnostic("COMMAND_ONLY_SMOKE_FAIL: application teardown " + e);
        }
    }

    protected override void OnEndMission()
    {
        if (_monitorActive) _endObserved = true;
        else if (!_finished) Fail(new InvalidOperationException("Native mission ended before command-only lifecycle verification"));
        base.OnEndMission();
    }
}
