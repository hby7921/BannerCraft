using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mycraft.Bridge;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace Mycraft.Demo;

// Explicit isolated host diagnostic only. Vanilla MC AI/damage is tested separately;
// this peer supplies synthetic authority to exercise real native army combat/counters.
public sealed class McArmySmokeBehavior : MissionBehavior
{
    private static readonly bool Enabled = Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "--mycraft-army-smoke");
    private static readonly FieldInfo BuiltField = AccessTools.Field(typeof(BattleObserverMissionLogic), "_builtAgentCountForSides");
    private static readonly FieldInfo RemovedField = AccessTools.Field(typeof(BattleObserverMissionLogic), "_removedAgentCountForSides");
    private static readonly FieldInfo SupplierField = AccessTools.Field(typeof(CustomBattleAgentOrigin), "_troopSupplier");
    private static Mission? _pendingDeployment, _pendingEnd;
    private static WeakReference<Mission>? _monitor;
    private static bool _monitorActive, _endObserved;
    private static long _endAt, _observeAt;
    private readonly Stopwatch _clock = new Stopwatch();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly List<Agent> _fians = new List<Agent>();
    private readonly HashSet<Agent> _markers = new HashSet<Agent>();
    private readonly Dictionary<int, Agent> _fianActors = new Dictionary<int, Agent>();
    private Team? _playerTeam, _enemyTeam;
    private Agent? _player, _returnVictim;
    private DefaultBattleMissionAgentSpawnLogic? _spawn;
    private BattleObserverMissionLogic? _observer;
    private BattleEndLogic? _end;
    private CustomBattleTroopSupplier? _enemySupplier;
    private int _phase, _returnVictimId, _bossDeaths, _soldierRemovals, _returnHits;
    private long _phaseAt, _lastProgress;
    private float _returnHp;
    private float _returnDamage;
    private bool _deploymentQueued, _finished;
    private string ControlPath => Path.Combine(DemoConfig.ModuleFolder, "Logs", "mc-army-smoke-control.json");
    private string PeerPath => Path.Combine(DemoConfig.ModuleFolder, "Logs", "mc-army-smoke-peer-result.json");
    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

    public override void AfterStart()
    {
        base.AfterStart();
        if (!Enabled) throw new InvalidOperationException("MC army diagnostics require their explicit launch flag");
        _clock.Restart();
        DemoLog.Diagnostic("MC_ARMY_SMOKE_BEGIN: original preset commander + 200 Fians versus 3 original enemy Wither slots; synthetic MC peer; player controller must remain Player");
    }

    public override void OnMissionTick(float dt)
    {
        if (!Enabled || _finished || !_clock.IsRunning) return;
        try
        {
            Require(_clock.ElapsedMilliseconds < 180000, "Native army smoke exceeded 180 seconds");
            var player = Mission.MainAgent;
            if (player?.IsActive() != true) return;
            var army = Mission.GetMissionBehavior<McArmyMissionBehavior>();
            var demo = Mission.GetMissionBehavior<DemoBridgeMissionBehavior>();
            Require(army != null && demo != null, "Production army/bridge behavior missing");
            if (_clock.ElapsedMilliseconds - _lastProgress >= 5000)
            {
                _lastProgress = _clock.ElapsedMilliseconds;
                DemoLog.Diagnostic("MC_ARMY_SMOKE_PROGRESS: phase=" + _phase + ", mode=" + Mission.Mode + ", humans=" + Mission.Agents.Count(a => a.IsActive() && a.IsHuman) + ", slots=" + army!.Slots.Count() + ", pending=" + army.PendingPlan);
            }
            if (_phase == 0)
            {
                var fians = Mission.Agents.Where(a => a.IsActive() && a.Character?.StringId == FiansWitherPresetLaunch.FianTroopId).ToArray();
                var markers = army!.Slots.ToArray();
                _spawn = Mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
                _observer = Mission.GetMissionBehavior<BattleObserverMissionLogic>();
                _end = Mission.GetMissionBehavior<BattleEndLogic>();
                if (fians.Length != 200 || markers.Length != 3 || _spawn?.IsInitialSpawnOver != true) return;
                if (Mission.Mode == MissionMode.Deployment)
                {
                    if (!_deploymentQueued)
                    {
                        _deploymentQueued = true; _pendingDeployment = Mission;
                        DemoLog.Diagnostic("MC_ARMY_SMOKE_DEPLOY_QUEUED: native DeploymentHandler.FinishDeployment from safe application tick");
                    }
                    return;
                }
                if (Mission.Mode != MissionMode.Battle || _observer?.BattleObserver == null) return;
                _player = player; _playerTeam = Mission.PlayerTeam; _enemyTeam = Mission.PlayerEnemyTeam;
                _fians.AddRange(fians);
                foreach (var slot in markers)
                {
                    _markers.Add(slot.Agent);
                    Require(slot.Agent.Origin is CustomBattleAgentOrigin && ReferenceEquals(slot.Agent.Team, _enemyTeam) && !slot.PlayerSide,
                        "Wither slot lost its original enemy army/origin");
                    var supplier = SupplierField.GetValue(slot.Agent.Origin) as CustomBattleTroopSupplier;
                    Require(supplier != null, "Wither origin has a null native supplier");
                    if (_enemySupplier == null) _enemySupplier = supplier;
                    Require(ReferenceEquals(_enemySupplier, supplier), "Original enemy slots use different native suppliers");
                    Require(slot.Agent.Controller == AgentControllerType.None && slot.Agent.Health == 300, "Pending Wither slot is not protected native placeholder");
                }
                Require(player.Character.StringId == "commander_1" && fians.All(a => ReferenceEquals(a.Team, _playerTeam) && a.Controller == AgentControllerType.AI), "Preset player/Fian native ownership mismatch");
                Require(_playerTeam.ActiveAgents.Count(a => a.IsHuman) == 201 && _enemyTeam.ActiveAgents.Count(a => a.IsHuman) == 3,
                    "Actual army counts must be 201 player humans and exactly 3 enemy slots");
                Require(_spawn.GetTotalNumberOfTroopsForSide(_playerTeam.Side) == 201 && _spawn.GetTotalNumberOfTroopsForSide(_enemyTeam.Side) == 3,
                    "Original spawn totals do not match preset");
                Require(Built(_enemyTeam) == 3 && Removed(_enemyTeam) == 0 && Built(_playerTeam) == 201 && Removed(_playerTeam) == 0, "Native observer initial army counts incorrect");
                CheckOrders(); CheckNoVictory();
                Require(army.PendingPlan && _enemySupplier!.NumRemovedTroops == 0, "MC pending objective vanished before peer connection");
                WriteControl("pending", demo!, army); Next(1);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_ROSTER_PASS: exact 200 armed AI Fians + native player; exact 3 enemy original slots/non-null supplier; observer201/3; native order owner retained");
            }
            else if (_phase == 1)
            {
                CheckNoVictory(); CheckOrders();
                Require(army!.PendingPlan && _enemyTeam!.ActiveAgents.Count(a => a.IsHuman) == 3, "Pending enemy army disappeared before MC connection");
                if (PhaseAge < 5000) return;
                foreach (var fian in _fians) _fianActors.Add(demo!.EntitySceneSmokeActorId(fian), fian);
                WriteControl("connect", demo!, army); Next(2);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_PENDING_PASS: original native battle stayed controllable and did not win for five seconds before synthetic authority");
            }
            else if (_phase == 2)
            {
                CheckNoVictory(); CheckOrders();
                Require(PhaseAge < 30000, "Peer did not bind all three real army slots/visual snapshots within 30 seconds");
                var peer = ReadPeer(); var mobs = demo!.EntitySceneSmoke?.Mobs.ToArray();
                if (!demo.IsEngaged || peer == null || army!.PendingPlan || mobs?.Length != 3 || mobs.Any(m => m.GenericVisual?.Ready != true)) return;
                Require(DemoProtocol.L(peer, "actorLimit") == 256 && DemoProtocol.L(peer, "actorCount") == 200, "200 native soldiers were not mirrored through negotiated actorLimit256");
                Require(mobs.All(m => m.ArmySlot > 0 && m.Agent != null && _markers.Contains(m.Agent) && ReferenceEquals(m.Agent.Team, _enemyTeam)), "MC states created extra proxies or changed original enemy team");
                Require(Mission.Teams.Count(t => t.Side == BattleSideEnum.None && NativeCreatureScene.OwnsTeam(t)) == 0, "Assigned Withers created an ambient None-side enemy team");
                var orders = _playerTeam!.PlayerOrderController;
                orders.SelectAllFormations(); orders.SetOrder(OrderType.Charge); orders.SetOrder(OrderType.FireAtWill);
                Require(orders.SelectedFormations.Any(f => f.CountOfUnits > 0) && orders.SelectedFormations.All(f => f.GetReadonlyMovementOrderReference().OrderEnum == MovementOrder.MovementOrderEnum.Charge),
                    "Original programmatic charge command did not reach selected formations");
                WriteControl("native_bows", demo, army); Next(3);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_BIND_AND_ORDERS_PASS: all3 actual Wither snapshots reuse original slots; 200 actor mirrors; Player controller/order owners intact; programmatic native SelectAll/Charge/FireAtWill (not physical F1/F3 input)");
            }
            else if (_phase == 3)
            {
                CheckNoVictory(); CheckOrders();
                Require(PhaseAge < 45000, "Real native Fian bows did not hit original MC enemy capsules within 45 seconds");
                var peer = ReadPeer();
                if (peer == null || DemoProtocol.L(peer, "nativeBowHurtCount") < 1 || DemoProtocol.L(peer, "hurtDuplicateCount") < 1) return;
                int sourceId = (int)DemoProtocol.L(peer, "nativeBowSource");
                Require(_fianActors.TryGetValue(sourceId, out _returnVictim), "Actual bow mob_hurt source is not an original Fian");
                Require(DemoProtocol.D(peer, "nativeBowDamage") > 0 && DemoProtocol.D(peer, "lowestMobHp") < 300, "Native arrow did not lower synthetic MC authority HP");
                Require(demo!.EntitySceneSmoke!.Mobs.Any(m => DemoProtocol.D(m.State, "hp") < 300), "Lower peer HP did not return to the native mob presentation");
                _returnVictimId = sourceId; _returnHp = _returnVictim!.Health;
                var damageConfig=DemoConfig.Load(DemoConfig.ModuleFolder);
                _returnDamage=CombatEventLedger.ToBannerlordDamage(7*damageConfig.McCreatureDamageFactor,damageConfig.McHitFactor);
                Require(_returnHp > _returnDamage && _returnVictim.IsActive(), "Chosen native Fian lacks life for nonfatal reverse receipt");
                WriteControl("return_hit", demo, army!); Next(4);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_NATIVE_BOW_PASS: original AI Fian actual HandleBlow -> mob_hurt arrow; source=" + sourceId + ", mcDamage=" + DemoProtocol.D(peer, "nativeBowDamage") + "; withheld first ACK yielded genuine retry, peer deducted same event only once");
            }
            else if (_phase == 4)
            {
                CheckNoVictory(); CheckOrders();
                Require(PhaseAge < 12000, "Synthetic reverse damage did not enter original native RegisterBlow once");
                var peer = ReadPeer();
                if (peer == null || !BridgeJson.B(peer, "returnHitAck") || DemoProtocol.L(peer, "returnHitSends") < 3 || PhaseAge < 1500) return;
                Require(_returnVictim!.IsActive() && Math.Abs(_returnHp - _returnVictim.Health - _returnDamage) < .05f && _returnHits == 1,
                    "Duplicate MC receipt repeated native RegisterBlow or callback: before=" + _returnHp + ", after=" + _returnVictim.Health + ", callbacks=" + _returnHits);
                Require(Removed(_playerTeam!) == 0 && Removed(_enemyTeam!) == 0, "Nonfatal hit changed native casualty totals");
                WriteControl("kill_soldier", demo!, army!); Next(5);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_MC_DAMAGE_PASS: original native RegisterBlow lowered Fian HP by exactly"+_returnDamage+"; one hit callback despite three sends; no casualty before lethal receipt");
            }
            else if (_phase == 5)
            {
                CheckNoVictory(); CheckOrders();
                Require(PhaseAge < 12000, "MC lethal receipt failed original native soldier casualty path");
                var peer = ReadPeer();
                if (peer == null || !BridgeJson.B(peer, "soldierDeathAck") || DemoProtocol.L(peer, "soldierDeathSends") < 3 || _soldierRemovals != 1) return;
                Require(Removed(_playerTeam!) == 1 && _playerTeam!.ActiveAgents.Count(a => a.IsHuman) == 200 &&
                    _playerTeam.ActiveAgents.Count(a => a.Character?.StringId == FiansWitherPresetLaunch.FianTroopId) == 199,
                    "MC lethal receipt skipped original army observer/member decrement");
                WriteControl("kill_boss_1", demo!, army!); Next(6);
                DemoLog.Diagnostic("MC_ARMY_SMOKE_NATIVE_CASUALTY_PASS: MC receipt removed exactlyone original Fian through native hit/death; Observer removed1; 199 Fians + player remain controllable");
            }
            else if (_phase == 6 || _phase == 7)
            {
                CheckNoVictory(); CheckOrders();
                int expectedDeaths = _phase - 5;
                Require(PhaseAge < 10000, "Native Wither death callback did not update original army counters");
                if (_bossDeaths != expectedDeaths) return;
                Require(Removed(_enemyTeam!) == expectedDeaths && _enemySupplier!.NumRemovedTroops == expectedDeaths &&
                    _enemyTeam!.ActiveAgents.Count(a => a.IsHuman) == 3 - expectedDeaths && !_spawn!.IsSideDepleted(_enemyTeam.Side),
                    "Native Wither death did not decrement exact enemy supplier/observer/active counts");
                if (PhaseAge < 2000) return;
                DemoLog.Diagnostic("MC_ARMY_SMOKE_PARTIAL_DEATH_PASS: removed=" + expectedDeaths + "; original enemy remaining=" + (3 - expectedDeaths) + "; no victory; player army orders retained");
                WriteControl(expectedDeaths == 1 ? "kill_boss_2" : "kill_boss_3", demo!, army!); Next(_phase + 1);
            }
            else if (_phase == 8)
            {
                Require(PhaseAge < 15000, "Original native battle did not declare victory after all three real enemy deaths");
                if (_bossDeaths != 3 || !_end!.PlayerVictory || Mission.MissionResult?.PlayerVictory != true) return;
                Require(Removed(_enemyTeam!) == 3 && _enemySupplier!.NumRemovedTroops == 3 && _spawn!.IsSideDepleted(_enemyTeam!.Side) &&
                    _spawn.NumberOfActiveDefenderTroops == 0 && _spawn.NumberOfRemainingDefenderTroops == 0,
                    "Native final victory did not match original supplier/spawn/observer exhaustion");
                Require(Math.Abs(_observer!.GetDeathToBuiltAgentRatioForSide(_enemyTeam!.Side) - 1f) < .001f, "Enemy observer death ratio is not3/3");
                Capture(); WriteControl("finish", demo!, army!);
                _finished = true; _pendingEnd = Mission; _monitor = new WeakReference<Mission>(Mission);
                _monitorActive = true; _endObserved = false; _endAt = _observeAt = 0;
                DemoLog.Diagnostic("MC_ARMY_SMOKE_NATIVE_VICTORY_PASS: all3 MC state deaths called actual native Agent.Die; original supplier removed3, Observer removed3, side depleted, native successful result; queuing EndMission");
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private long PhaseAge => _clock.ElapsedMilliseconds - _phaseAt;
    private void Next(int phase) { _phase = phase; _phaseAt = _clock.ElapsedMilliseconds; }
    private int Built(Team team) => ((int[])BuiltField.GetValue(_observer))[(int)team.Side];
    private int Removed(Team team) => ((int[])RemovedField.GetValue(_observer))[(int)team.Side];
    private void CheckNoVictory()
    {
        Require(_end != null && !_end.PlayerVictory && !Mission.MissionEnded && !Mission.IsMissionEnding && Mission.MissionResult == null,
            "Native battle ended/won while MC enemy objective was pending or alive");
    }
    private void CheckOrders()
    {
        Require(_player?.IsActive() == true && ReferenceEquals(Mission.MainAgent, _player) && _player.Controller == AgentControllerType.Player &&
            ReferenceEquals(Mission.PlayerTeam, _playerTeam) && ReferenceEquals(Mission.PlayerEnemyTeam, _enemyTeam), "Native Player controller or original army identity changed");
        var orders = _playerTeam!.PlayerOrderController;
        Require(orders != null && ReferenceEquals(orders.Owner, _player), "Original native order controller lost player owner");
        foreach (var formation in _playerTeam.FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0))
            Require(ReferenceEquals(formation.PlayerOwner, _player) && orders!.IsFormationSelectable(formation), "Original formation is no longer player controllable");
    }
    private void WriteControl(string phase, DemoBridgeMissionBehavior demo, McArmyMissionBehavior army)
    {
        var message = new Dictionary<string, object> { ["runId"] = _runId, ["phase"] = phase, ["targetEid"] = _returnVictimId,
            ["soldierDeathDamage"] = (double)(_returnVictim?.Health ?? 100) + 20, ["fianCount"] = _fianActors.Count };
        int i = 0;
        foreach (var pair in _fianActors) message["fian" + i++ + "Eid"] = pair.Key;
        i = 0;
        foreach (var slot in army.Slots.OrderBy(s => s.Id)) message["slot" + i++] = slot.Id;
        string temporary = ControlPath + ".tmp";
        File.WriteAllText(temporary, BridgeJson.Write(message));
        if (File.Exists(ControlPath)) File.Delete(ControlPath);
        File.Move(temporary, ControlPath);
    }
    private Dictionary<string, object>? ReadPeer()
    {
        try
        {
            if (!File.Exists(PeerPath) || !BridgeJson.TryParse(File.ReadAllText(PeerPath), out var result) || BridgeJson.S(result, "runId") != _runId) return null;
            Require(!BridgeJson.B(result, "failed"), "Synthetic peer failed: " + BridgeJson.S(result, "error"));
            return result;
        }
        catch (IOException) { return null; }
    }
    public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon, in Blow blow, in AttackCollisionData collision)
    {
        if (ReferenceEquals(affectedAgent, _returnVictim) && NativeCreatureScene.OwnsArmyAgent(affectorAgent) && _phase == 4 && blow.InflictedDamage > 0) _returnHits++;
        base.OnAgentHit(affectedAgent, affectorAgent, affectorWeapon, blow, collision);
    }
    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState state, KillingBlow blow)
    {
        if (_markers.Contains(affectedAgent))
        {
            if (state != AgentState.Killed) { Fail(new InvalidOperationException("Wither army slot removal was not native Killed")); return; }
            _bossDeaths++;
            DemoLog.Diagnostic("MC_ARMY_SMOKE_NATIVE_BOSS_CALLBACK: state=" + state + ", count=" + _bossDeaths);
        }
        if (ReferenceEquals(affectedAgent, _returnVictim))
        {
            _soldierRemovals++;
            DemoLog.Diagnostic("MC_ARMY_SMOKE_NATIVE_SOLDIER_CALLBACK: state=" + state + ", count=" + _soldierRemovals);
        }
        base.OnAgentRemoved(affectedAgent, affectorAgent, state, blow);
    }
    private static void Capture()
    {
        try { TaleWorlds.Engine.Utilities.TakeScreenshot(Path.Combine(DemoConfig.ModuleFolder, "Logs", "mc-army-native.bmp")); }
        catch (Exception error) { DemoLog.Diagnostic("MC_ARMY_SMOKE_CAPTURE_UNAVAILABLE: " + error.GetBaseException().Message); }
    }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private void Fail(Exception error)
    {
        _finished = true; _monitorActive = false; _pendingEnd = null; _pendingDeployment = null; _monitor = null;
        DemoLog.Diagnostic("MC_ARMY_SMOKE_FAIL: " + error);
    }
    public static void ApplicationTick(float dt)
    {
        if (!Enabled) return;
        try
        {
            if (_pendingDeployment != null)
            {
                var mission = _pendingDeployment; _pendingDeployment = null;
                var deployment = mission.GetMissionBehavior<DeploymentHandler>();
                Require(deployment != null && mission.Mode == MissionMode.Deployment, "Original deployment callback unavailable");
                deployment!.FinishDeployment();
                DemoLog.Diagnostic("MC_ARMY_SMOKE_DEPLOY_PASS: original deployment FinishDeployment executed without touching user controller");
            }
            if (!_monitorActive) return;
            if (_pendingEnd != null)
            {
                var mission = _pendingEnd; _pendingEnd = null; _endAt = Stopwatch.GetTimestamp();
                mission.EndMission(); return;
            }
            long now = Stopwatch.GetTimestamp();
            bool over = _endObserved && (_monitor == null || !_monitor.TryGetTarget(out var missionState) || missionState.CurrentState == Mission.State.Over);
            if (!over)
            {
                Require(_endAt == 0 || (now - _endAt) / (double)Stopwatch.Frequency < 30, "Army diagnostic EndMission did not complete within30seconds");
                return;
            }
            if (_observeAt == 0) { _observeAt = now; DemoLog.Diagnostic("MC_ARMY_SMOKE_TEARDOWN_OBSERVE: OnEndMission and State.Over"); return; }
            if ((now - _observeAt) / (double)Stopwatch.Frequency < 10) return;
            DemoLog.Diagnostic("MC_ARMY_SMOKE_FINAL_PASS: actual201vs3 roster; nativePlayer/orderowners; real Fian bow HandleBlow and retry; MC RegisterBlow/casualty; all3 original army deaths and native victory; EndMission plus10seconds safe");
            _monitorActive = false; _monitor = null;
        }
        catch (Exception error)
        {
            _monitorActive = false; _pendingEnd = null; _pendingDeployment = null; _monitor = null;
            DemoLog.Diagnostic("MC_ARMY_SMOKE_FAIL: application tick " + error);
        }
    }
    protected override void OnEndMission()
    {
        if (_monitorActive) _endObserved = true;
        else if (!_finished) Fail(new InvalidOperationException("Native mission ended before army verification"));
        base.OnEndMission();
    }
}
