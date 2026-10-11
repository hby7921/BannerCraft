using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mycraft.Bridge;
using Mycraft.Visuals;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace Mycraft.Demo;

// Explicit isolated launch only. The peer is synthetic MC authority; native AI,
// collision, RegisterBlow and cleanup are real. This is not an MC gameplay test.
public sealed class EntityCombatSmokeBehavior : MissionBehavior
{
    internal const string MobId = "00000000-0000-4000-8000-000000000001";
    private sealed class Army
    {
        public Team Team = null!;
        public BattleSideEnum Side;
        public int Humans;
        public float Power, DeathRatio;
    }
    private static Mission? _pendingEnd;
    private static WeakReference<Mission>? _monitor;
    private static bool _endObserved, _monitorActive;
    private static long _endAt, _observeAt;
    private readonly Stopwatch _clock = new Stopwatch();
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly List<Army> _armies = new List<Army>();
    private Agent? _enemy, _proxy;
    private Team? _proxyTeam;
    private Vec3 _mobPosition, _boatsPosition;
    private BattlePowerCalculationLogic? _power;
    private BattleObserverMissionLogic? _observer;
    private int _phase, _enemyId, _removedCallbacks, _deletedCallbacks;
    private long _phaseAt, _lastAiLog;
    private float _enemyHpBefore, _enemyHpAfter;
    private bool _sawAiTarget, _finished;
    private string ControlPath => Path.Combine(DemoConfig.ModuleFolder, "Logs", "entity-smoke-control.json");
    private string PeerPath => Path.Combine(DemoConfig.ModuleFolder, "Logs", "entity-smoke-peer-result.json");
    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

    public override void AfterStart()
    {
        base.AfterStart();
        _clock.Restart();
        DemoLog.Diagnostic("ENTITY_NATIVE_SMOKE_BEGIN: real native commander_1 vs commander_2; synthetic MC peer; native AI and lifecycle required");
    }

    public override void OnMissionTick(float dt)
    {
        if (_finished || !_clock.IsRunning) return;
        try
        {
            Require(_clock.ElapsedMilliseconds < 120000, "Entity mission smoke exceeded 120 seconds");
            var player = Mission.MainAgent;
            if (player == null || !player.IsActive() || Mission.Mode != MissionMode.Battle) return;
            var demo = Mission.GetMissionBehavior<DemoBridgeMissionBehavior>();
            Require(demo != null, "Production bridge behavior missing");
            if (_enemy != null && !_enemy.IsActive()) throw new InvalidOperationException("Ordinary enemy died or disappeared during nonfatal smoke");
            if (_proxy?.IsActive() == true && _enemy != null)
            {
                _sawAiTarget |= ReferenceEquals(_enemy.GetTargetAgent(), _proxy);
                if (_clock.ElapsedMilliseconds - _lastAiLog > 5000)
                {
                    _lastAiLog = _clock.ElapsedMilliseconds;
                    DemoLog.Diagnostic("ENTITY_NATIVE_AI_OBSERVE: target=" + (_enemy.GetTargetAgent()?.Index ?? -1) +
                        ", proxy=" + _proxy.Index + ", distance=" + (_enemy.Position - _proxy.Position).Length +
                        ", enemyAction=" + _enemy.GetCurrentActionStage(1) + ", controller=" + _enemy.Controller);
                }
            }
            if (_phase == 0)
            {
                if (_clock.ElapsedMilliseconds < 2000) return;
                Require(player.Character.StringId == "commander_1", "Entity diagnostic loader must use ordinary commander_1");
                _enemy = Mission.Agents.FirstOrDefault(a => a.IsActive() && a.IsHuman && !ReferenceEquals(a, player) &&
                    !NativeCreatureScene.OwnsAgent(a) && a.IsEnemyOf(player));
                Require(_enemy != null && _enemy.Character.StringId == "commander_2" && _enemy.Controller == AgentControllerType.AI,
                    "Ordinary native AI commander_2 missing");
                _power = Mission.GetMissionBehavior<BattlePowerCalculationLogic>();
                _observer = Mission.GetMissionBehavior<BattleObserverMissionLogic>();
                foreach (var team in Mission.Teams.Where(t => t.Side == BattleSideEnum.Attacker || t.Side == BattleSideEnum.Defender))
                    _armies.Add(new Army { Team = team, Side = team.Side, Humans = team.ActiveAgents.Count(a => a.IsHuman),
                        Power = _power?.GetTotalTeamPower(team) ?? 0, DeathRatio = _observer?.GetDeathToBuiltAgentRatioForSide(team.Side) ?? 0 });
                Require(_armies.Count == 2 && _armies.All(a => a.Humans > 0), "Expected two original native armies");
                Require(_armies[0].Team.IsEnemyOf(_armies[1].Team) && _armies[1].Team.IsEnemyOf(_armies[0].Team), "Original army enemy relation missing");
                WriteControl("connect", demo!);
                Next(1);
                DemoLog.Diagnostic("ENTITY_NATIVE_PRECONNECT_PASS: ordinary commanders and both original army identities/sides/counts/power/death ratios captured; run=" + _runId);
            }
            else if (_phase == 1)
            {
                Require(PhaseAge < 30000, "Synthetic peer did not activate the production session within 30 seconds");
                if (!demo!.OwnsPlayer(player) || demo.EntitySceneSmoke == null) return;
                var direction = player.Position - _enemy!.Position; direction.z = 0;
                if (direction.Normalize() < .01f) direction = new Vec3(0, 1, 0);
                _mobPosition = _enemy.Position + direction * 2.2f;
                _mobPosition.z = _enemy.Position.z;
                var forward = player.LookDirection; forward.z = 0;
                if (forward.Normalize() < .01f) forward = new Vec3(0, 1, 0);
                _boatsPosition = player.Position + forward * 6;
                _enemyId = demo.EntitySceneSmokeActorId(_enemy);
                WriteControl("spawn", demo);
                Next(2);
            }
            else if (_phase == 2)
            {
                Require(PhaseAge < 15000, "Native proxy/original boat models did not appear within 15 seconds");
                var scene = demo!.EntitySceneSmoke;
                var mob = scene?.Mobs.FirstOrDefault(m => m.Id == MobId);
                if (mob?.Agent == null || scene!.BoatCount != 11) return;
                var cow=scene.Mobs.FirstOrDefault(m=>m.Kind=="cow");
                var boss=scene.Mobs.FirstOrDefault(m=>m.Kind=="wither");
                if(cow?.Agent==null || boss?.Agent==null || cow.GenericVisual?.Ready!=true || boss.GenericVisual?.Ready!=true)return;
                foreach(var army in _armies)Require(!cow.Agent.Team.IsEnemyOf(army.Team) && !army.Team.IsEnemyOf(cow.Agent.Team),"Native passive cow incorrectly made hostile");
                Require(Math.Abs(cow.Agent.Monster.BodyCapsuleRadius-.45f)<.001f && Math.Abs(boss.Agent.Monster.BodyCapsuleRadius-.45f)<.001f
                    && Math.Abs(boss.Agent.Monster.BodyCapsulePoint1.z-3.05f)<.001f,"Actual dynamic collider registration differs from native cow/Wither dimensions");
                ProbeCapsule(cow.Agent,.9f,1.4f);ProbeCapsule(boss.Agent,.9f,3.5f);
                var bossBar=demo.McBosses.SingleOrDefault();
                if(Mission.GetMissionBehavior<Mycraft.UI.MinecraftHotbarMissionBehavior>()?.HasNativeBossBar(273)!=true)return;
                Require(bossBar!=null && bossBar.Hp==150 && bossBar.MaxHp==300 && Math.Abs(bossBar.Fraction-.5)<.001,"Actual native Wither health did not update host bossbar");
                DemoLog.Diagnostic("ENTITY_NATIVE_GENERIC_BOSS_PASS: passive generic cow-first creates safe None team; original native snapshots rendered; real dynamic cow/Wither capsules; Wither bossbar150/300 exactly half");
                _proxy = mob.Agent; _proxyTeam = _proxy.Team;
                Require(_proxy.IsActive() && _proxy.Controller == AgentControllerType.None && _proxy.Team.Side == BattleSideEnum.None,
                    "Proxy is not a passive real native agent on its dedicated None team");
                Require(!_proxy.AgentVisuals.GetVisible() && mob.Visual != null && WitherSkeletonVisual.AssetsAvailable && BoatVisual.AssetsAvailable,
                    "Original MC visual creation failed or native soldier body leaked");
                foreach (var army in _armies)
                    Require(_proxy.Team.IsEnemyOf(army.Team) && army.Team.IsEnemyOf(_proxy.Team), "Proxy is not hostile to both original armies");
                CheckArmies();
                ProbeCapsule(_proxy);
                CaptureOriginalModels();
                // Synthetic authority exercises NPC combat, rather than local
                // player controls. Isolate physical keyboard input after the
                // production native dismount/takeover has been verified.
                player.Controller=AgentControllerType.None;
                DemoLog.Diagnostic("ENTITY_NATIVE_PROXY_AND_BOATS_PASS: passive None-team proxy hostile to both unchanged armies; actual 0.7 x 2.4 capsule rays; original wither and first eleven boats uploaded");
                Next(3);
            }
            else if (_phase == 3)
            {
                Require(PhaseAge < 45000, "Ordinary native AI did not naturally target and damage the MC proxy within 45 seconds; no forced target/ForwardHurt used");
                var peer = ReadPeer();
                var mob = demo!.EntitySceneSmoke?.Mobs.FirstOrDefault(m => m.Id == MobId);
                if (!_sawAiTarget || peer == null || DemoProtocol.L(peer, "nativeHurtCount") < 1 || mob == null || DemoProtocol.D(mob.State, "hp") >= 20) return;
                Require(DemoProtocol.L(peer, "nativeHurtSource") == _enemyId, "Received mob_hurt was not from the ordinary native diagnostic soldier");
                Require(DemoProtocol.D(peer, "mobHp") < 20 && DemoProtocol.D(peer, "nativeHurtDamage") > 0,
                    "Actual native hit did not change synthetic MC health authority");
                CheckArmies();
                _enemyHpBefore = _enemy!.Health;
                Require(_enemyHpBefore > 14, "Native enemy has insufficient life for nonfatal seven-damage smoke");
                DemoLog.Diagnostic("ENTITY_NATIVE_AI_TO_MC_PASS: native GetTargetAgent selected proxy; actual HandleBlow -> reliable mob_hurt; source=" + _enemyId +
                    ", native forwarded MC damage=" + DemoProtocol.D(peer, "nativeHurtDamage") + ", MC hp=" + DemoProtocol.D(mob.State, "hp"));
                WriteControl("return_hit", demo!);
                Next(4);
            }
            else if (_phase == 4)
            {
                Require(PhaseAge < 10000, "Synthetic MC mob_hit did not lower ordinary native soldier HP");
                var peer = ReadPeer();
                if (peer == null || !BridgeJson.B(peer, "returnHitAck") || DemoProtocol.L(peer, "returnHitSends") < 3 || PhaseAge < 2000) return;
                _enemyHpAfter = _enemy!.Health;
                Require(Math.Abs((_enemyHpBefore - _enemyHpAfter) - 7) < .05f,
                    "RegisterBlow damage or duplicate suppression failed: before=" + _enemyHpBefore + ", after=" + _enemyHpAfter);
                CheckArmies();
                DemoLog.Diagnostic("ENTITY_NATIVE_MC_TO_AI_PASS: real RegisterBlow lowered native soldier HP exactly once by seven; duplicate event sent three times; before=" + _enemyHpBefore + ", after=" + _enemyHpAfter);
                CaptureOriginalModels();
                WriteControl("boats_two", demo!);
                Next(5);
            }
            else if (_phase == 5)
            {
                Require(PhaseAge < 15000, "Second original boat batch or first batch removal failed");
                var peer = ReadPeer();
                if (peer == null || DemoProtocol.L(peer, "boatBatch") != 2 || DemoProtocol.L(peer, "boatRemoveAcks") < 11 || demo!.EntitySceneSmoke?.BoatCount != 11) return;
                Require(Math.Abs(_enemy!.Health - _enemyHpAfter) < .05f, "Late duplicate mob_hit changed native soldier HP again");
                DemoLog.Diagnostic("ENTITY_NATIVE_ALL_BOATS_PASS: all twenty-two original variants created in eleven-model batches; first batch reliable removal acknowledged");
                WriteControl("show_boss", demo!);
                Next(8);
            }
            else if(_phase==8)
            {
                Require(PhaseAge<12000,"Original three-head Wither did not present in front of the native camera");
                if(PhaseAge<3000)return;
                var boss=demo!.EntitySceneSmoke?.Mobs.FirstOrDefault(m=>m.Kind=="wither");
                if(boss?.GenericVisual?.Ready!=true || Mission.GetMissionBehavior<Mycraft.UI.MinecraftHotbarMissionBehavior>()?.HasNativeBossBar(273)!=true)return;
                CaptureOriginalModels();
                DemoLog.Diagnostic("ENTITY_NATIVE_VISIBLE_WITHER_PASS: original three-head native renderer in front of camera; actual Gauntlet boss clip273/546 and150/300 text; held for three seconds before capture");
                WriteControl("kill_remove",demo);Next(6);
            }
            else if (_phase == 6)
            {
                Require(PhaseAge < 18000, "Proxy death/removal or native late callbacks failed to complete");
                var peer = ReadPeer();
                var scene = demo!.EntitySceneSmoke;
                if (peer == null || !BridgeJson.B(peer, "mobRemoveAck") || DemoProtocol.L(peer, "boatRemoveAcks") < 22 ||
                    DemoProtocol.L(peer, "retiredReplaySends") < 3 || scene == null || scene.Mobs.Any() || scene.BoatCount != 0 ||
                    (_removedCallbacks + _deletedCallbacks) == 0 || PhaseAge < 4000) return;
                Require(!demo.McBosses.Any(),"Bossbar survived native boss removal");
                Require(_proxyTeam != null && NativeCreatureScene.OwnsTeam(_proxyTeam), "Proxy team identity vanished before late native callbacks");
                CheckArmies();
                DemoLog.Diagnostic("ENTITY_NATIVE_REMOVE_PASS: dead proxy/withers/all twenty-two boats retired; stale alive replays cannot resurrect; late removed=" + _removedCallbacks + ", deleted=" + _deletedCallbacks + "; original army statistics unchanged");
                demo.EndEntitySceneSmokeSession();
                Next(7);
            }
            else if (_phase == 7)
            {
                if (PhaseAge < 500) return;
                Require(!demo!.IsEngaged && demo.EntitySceneSmoke == null, "Entity scene survived normal bridge teardown");
                CheckArmies();
                _finished = true; _pendingEnd = Mission; _monitor = new WeakReference<Mission>(Mission);
                _monitorActive = true; _endObserved = false; _endAt = _observeAt = 0;
                DemoLog.Diagnostic("ENTITY_NATIVE_SESSION_TEARDOWN_PASS: production entity scene cleared; queuing ordinary EndMission");
            }
        }
        catch (Exception e) { Fail(e); }
    }

    private long PhaseAge => _clock.ElapsedMilliseconds - _phaseAt;
    private void Next(int phase) { _phase = phase; _phaseAt = _clock.ElapsedMilliseconds; }

    private void CheckArmies()
    {
        foreach (var army in _armies)
        {
            Require(army.Team.Side == army.Side && !NativeCreatureScene.OwnsTeam(army.Team), "MC proxy changed an original army identity/side");
            Require(army.Team.ActiveAgents.Count(a => a.IsHuman) == army.Humans, "MC proxy changed original army human count");
            if (_power != null) Require(Math.Abs(_power.GetTotalTeamPower(army.Team) - army.Power) < .001f, "MC proxy changed original army battle power");
            if (_observer != null) Require(Math.Abs(_observer.GetDeathToBuiltAgentRatioForSide(army.Side) - army.DeathRatio) < .001f,
                "MC proxy affected ordinary army casualty statistics");
        }
        Require(_armies[0].Team.IsEnemyOf(_armies[1].Team) && _armies[1].Team.IsEnemyOf(_armies[0].Team), "MC proxy altered original two-army relations");
    }

    private void ProbeCapsule(Agent proxy,float width=.7f,float height=2.4f)
    {
        var radial = _enemy!.Position - proxy.Position; radial.z = 0;
        if (radial.Normalize() < .01f) radial = new Vec3(0, 1, 0);
        var transverse = new Vec3(-radial.y, radial.x, 0);
        bool Probe(float offset, float height)
        {
            var center = proxy.Position + radial * offset + new Vec3(0, 0, height);
            return ReferenceEquals(Mission.RayCastForClosestAgent(center - transverse * .8f, center + transverse * .8f,
                _enemy.Index, .001f, out var distance), proxy);
        }
        Require(Probe(width*.5f-.01f,height*.5f) && Probe(-width*.5f+.01f,height*.5f) && Probe(0,.08f) && Probe(0,height-.08f),
            "Real proxy collider does not cover native dimensions "+width+" x "+height);
        Require(!Probe(width*.5f+.05f,height*.5f) && !Probe(0,height+.08f), "Proxy collision exceeds the expected capsule bounds");
    }

    private void WriteControl(string phase, DemoBridgeMissionBehavior demo)
    {
        var message = new Dictionary<string, object> { ["runId"] = _runId, ["phase"] = phase, ["targetEid"] = _enemyId, ["mobId"] = MobId };
        var mob = demo.EntitySceneSmokeMcPoint(_mobPosition); var boats = demo.EntitySceneSmokeMcPoint(_boatsPosition);
        message["mobX"] = (double)mob.x; message["mobY"] = (double)mob.y; message["mobZ"] = (double)mob.z;
        message["boatX"] = (double)boats.x; message["boatY"] = (double)boats.y; message["boatZ"] = (double)boats.z;
        var player=Mission.MainAgent;
        var forward=player.LookDirection;forward.z=0;if(forward.Normalize()<.01f)forward=new Vec3(0,1,0);
        var boss=demo.EntitySceneSmokeMcPoint(player.Position+forward*7+new Vec3(0,0,1));
        message["bossX"]=(double)boss.x;message["bossY"]=(double)boss.y;message["bossZ"]=(double)boss.z;
        File.WriteAllText(ControlPath, BridgeJson.Write(message));
    }

    private Dictionary<string, object>? ReadPeer()
    {
        string json;
        try { if (!File.Exists(PeerPath)) return null; json = File.ReadAllText(PeerPath); }
        catch (IOException) { return null; }
        if (!BridgeJson.TryParse(json, out var result) || BridgeJson.S(result, "runId") != _runId) return null;
        Require(!BridgeJson.B(result, "failed"), "Synthetic peer failed: " + BridgeJson.S(result, "error"));
        return result;
    }

    private static void CaptureOriginalModels()
    {
        try
        {
            var method = typeof(TaleWorlds.Engine.Utilities).GetMethod("TakeScreenshot", new[] { typeof(string) });
            if (method == null) { DemoLog.Diagnostic("ENTITY_NATIVE_CAPTURE_UNAVAILABLE: installed engine lacks Utilities.TakeScreenshot(string)"); return; }
            var path = Path.Combine(DemoConfig.ModuleFolder, "Logs", "entity-native-original-models.bmp");
            method.Invoke(null, new object[] { path });
            DemoLog.Diagnostic("ENTITY_NATIVE_CAPTURE_REQUEST: " + path);
        }
        catch (Exception e) { DemoLog.Diagnostic("ENTITY_NATIVE_CAPTURE_UNAVAILABLE: " + e.GetBaseException().Message); }
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
    {
        if (ReferenceEquals(affectedAgent, _proxy))
        {
            _removedCallbacks++;
            if (!NativeCreatureScene.OwnsTeam(affectedAgent.Team)) Fail(new InvalidOperationException("Proxy None-team scope lost in late OnAgentRemoved"));
            DemoLog.Diagnostic("ENTITY_NATIVE_PROXY_REMOVED_CALLBACK: state=" + agentState + "; dedicated team scope retained");
        }
        base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
    }

    public override void OnAgentDeleted(Agent affectedAgent)
    {
        if (ReferenceEquals(affectedAgent, _proxy))
        {
            _deletedCallbacks++;
            if (!NativeCreatureScene.OwnsTeam(affectedAgent.Team)) Fail(new InvalidOperationException("Proxy None-team scope lost in late OnAgentDeleted"));
            DemoLog.Diagnostic("ENTITY_NATIVE_PROXY_DELETED_CALLBACK: dedicated team scope retained");
        }
        base.OnAgentDeleted(affectedAgent);
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private void Fail(Exception error)
    {
        _finished = true; _monitorActive = false; _pendingEnd = null; _monitor = null;
        DemoLog.Diagnostic("ENTITY_NATIVE_SMOKE_FAIL: " + error);
    }

    public static void ApplicationTick(float dt)
    {
        if (!_monitorActive) return;
        try
        {
            if (_pendingEnd != null)
            {
                var mission = _pendingEnd; _pendingEnd = null; _endAt = Stopwatch.GetTimestamp();
                DemoLog.Diagnostic("ENTITY_NATIVE_TEARDOWN_REQUEST: ordinary EndMission from application tick");
                mission.EndMission(); return;
            }
            var now = Stopwatch.GetTimestamp();
            bool over = _endObserved && (_monitor == null || !_monitor.TryGetTarget(out var observedMission) || observedMission.CurrentState == Mission.State.Over);
            if (!over)
            {
                Require(_endAt == 0 || (now - _endAt) / (double)Stopwatch.Frequency < 30, "Ordinary entity smoke EndMission did not complete within 30 seconds");
                return;
            }
            if (_observeAt == 0) { _observeAt = now; DemoLog.Diagnostic("ENTITY_NATIVE_TEARDOWN_OBSERVE: OnEndMission and State.Over"); return; }
            if ((now - _observeAt) / (double)Stopwatch.Frequency < 10) return;
            DemoLog.Diagnostic("ENTITY_NATIVE_FINAL_PASS: actual natural native AI -> MC hurt state; synthetic MC hit -> real native RegisterBlow once; original visuals/collision/armies/removal; ordinary EndMission and ten seconds survived");
            _monitorActive = false; _monitor = null;
        }
        catch (Exception e) { _monitorActive = false; _pendingEnd = null; _monitor = null; DemoLog.Diagnostic("ENTITY_NATIVE_SMOKE_FAIL: application teardown " + e); }
    }

    protected override void OnEndMission()
    {
        if (_monitorActive) _endObserved = true;
        else if (!_finished) Fail(new InvalidOperationException("Native mission ended before entity combat verification"));
        base.OnEndMission();
    }
}
