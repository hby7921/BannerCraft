using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mycraft.Bridge;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// The roster is supplied by native CustomBattleTroopSupplier. These are the
// original army agents, not a second army of manually spawned proxy humans.
public sealed class McArmyMissionBehavior : MissionBehavior
{
    public const string WitherTroopId = "bannercraft_mc_wither";
    internal sealed class ArmySlot
    {
        internal int Id;
        internal Agent Agent = null!;
        internal string Kind = "wither", EntityId = "";
        internal bool PlayerSide, Alive = true;
        internal int LastAttacker = -1;
        internal Vec3 SpawnPosition;
    }

    private readonly Dictionary<int, ArmySlot> _slots = new Dictionary<int, ArmySlot>();
    private static readonly FieldInfo CombatantsField = AccessTools.Field(typeof(MissionCombatantsLogic), "BattleCombatants")
        ?? throw new InvalidOperationException("Native custom battle combatant roster is unavailable");
    private readonly Stopwatch _clock = new Stopwatch();
    private Dictionary<string, object>? _request;
    private string _sessionId = "";
    private long _sentAt, _retryAt;
    private bool _spawnAccepted, _failed, _endQueued, _ending;
    private int _expectedCount, _expectedEnemies;
    public static McArmyMissionBehavior? Current { get; private set; }
    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    public bool PendingPlan => !_failed && (_slots.Count < _expectedCount || _slots.Values.Any(s => s.Alive && s.EntityId.Length == 0));
    public int ExpectedCount => _expectedCount;
    public int ActorCapacity {get;private set;}=256;
    internal IEnumerable<ArmySlot> Slots => _slots.Values;
    internal bool OwnsMission(Mission mission) => !_ending && ReferenceEquals(Mission, mission);
    // Keep the objective until the queued setup abort actually ends the mission;
    // releasing it one tick earlier would turn transport failure into victory.
    internal bool HasEnemyObjective => !_ending && _expectedEnemies > _slots.Values.Count(s => !s.PlayerSide && !s.Alive);

    public override void OnBehaviorInitialize()
    {
        base.OnBehaviorInitialize();
        if (!GameNetwork.IsMultiplayer && Game.Current?.GameType is TaleWorlds.MountAndBlade.CustomBattle.CustomGame && Mission.IsFieldBattle)
            Current = this;
    }

    public override void AfterStart()
    {
        base.AfterStart();
        if (!GameNetwork.IsMultiplayer && Game.Current?.GameType is TaleWorlds.MountAndBlade.CustomBattle.CustomGame && Mission.IsFieldBattle)
            Current = this;
        var combatants = Mission.GetMissionBehavior<MissionCombatantsLogic>();
        if (combatants != null && CombatantsField.GetValue(combatants) is IEnumerable<IBattleCombatant> roster)
            foreach (var party in roster.OfType<CustomBattleCombatant>())
            {
                int count = party.Characters.Count(c => c.StringId == WitherTroopId);
                _expectedCount += count;
                if (party.Side != Mission.PlayerTeam.Side) _expectedEnemies += count;
            }
        if(combatants!=null && CombatantsField.GetValue(combatants) is IEnumerable<IBattleCombatant> actorRoster)
        {
            int humans=actorRoster.OfType<CustomBattleCombatant>().Sum(p=>p.Characters.Count(c=>c.StringId!=WitherTroopId));
            ActorCapacity=humans>256?640:256;
            if(_expectedCount>0 && humans-1>640)Fail("本场 MC 军队最多同步 640 名骑砍士兵。");
        }
        if (_expectedCount > 16) Fail("MC 军队一次最多支持 16 只生物。");
        _clock.Restart();
    }

    public override void OnAgentBuild(Agent agent, Banner banner)
    {
        base.OnAgentBuild(agent, banner);
        if (_failed || !OwnsMission(Mission) || agent.Character?.StringId != WitherTroopId) return;
        if (agent.Origin is not CustomBattleAgentOrigin || agent.Team == null || agent.Team.Side == BattleSideEnum.None)
            throw new InvalidOperationException("MC army slot requires its original custom-battle origin and army team");
        if (_slots.Count >= 16) throw new InvalidOperationException("MC custom army supports at most 16 native creatures");
        int id = checked(agent.Index + 1);
        var slot = new ArmySlot { Id = id, Agent = agent, PlayerSide = agent.Origin.IsUnderPlayersCommand };
        _slots.Add(id, slot);
        NativeCreatureScene.MarkArmyAgent(agent);
        agent.Controller = AgentControllerType.None;
        agent.SetMortalityState(Agent.MortalityState.Invulnerable);
        agent.BaseHealthLimit = agent.HealthLimit = 300;
        agent.Health = 300;
        if (agent.AgentVisuals?.IsValid() == true) agent.AgentVisuals.SetVisible(false);
        DemoLog.Diagnostic("MC_ARMY_NATIVE_SLOT: slot=" + id + ", agent=" + agent.Index + ", side=" + agent.Team.Side +
            ", origin=" + agent.Origin.GetType().Name + ", formation=" + (agent.Formation?.FormationIndex.ToString() ?? "none") + ", playerSide=" + slot.PlayerSide);
    }

    public override void OnMissionTick(float dt)
    {
        base.OnMissionTick(dt);
        if (_ending) return;
        if (_endQueued)
        {
            _endQueued = false;
            // An unsuccessful transport is an aborted setup, not a victory.
            Mission.EndMission();
            return;
        }
        foreach (var slot in _slots.Values)
            if (slot.Alive && slot.Agent.IsActive())
            {
                slot.Agent.Controller = AgentControllerType.None;
                if (slot.Agent.AgentVisuals?.IsValid() == true) slot.Agent.AgentVisuals.SetVisible(false);
                if (slot.EntityId.Length == 0 && slot.Agent.Health < 1) slot.Agent.Health = 300;
            }
        if (_request != null && PendingPlan && _clock.ElapsedMilliseconds - _sentAt > 45000)
            Fail("MC 军队在 45 秒内未完成生成，请重新进入战局并连接 MC。");
    }

    // Called after the normal player session is active. Repeated requests retain
    // their exact requestId, coordinates and slot identity until all are bound.
    internal Dictionary<string, object>? TakeSpawnRequest(string sid, Func<Vec3, Vec3> hostToMc)
    {
        if (_failed || _ending || !PendingPlan || _slots.Count != _expectedCount || Mission.Mode != MissionMode.Battle || Mission.MainAgent?.IsActive() != true) return null;
        if (_sessionId.Length > 0 && _sessionId != sid) { Fail("MC 军队会话已更换，请重新进入战局。"); return null; }
        long now = _clock.ElapsedMilliseconds;
        if (_request == null)
        {
            _sessionId = sid;
            var player = Mission.MainAgent;
            var forward = player.LookDirection; forward.z = 0;
            if (forward.Normalize() < .01f) forward = new Vec3(0, 1, 0);
            var right = new Vec3(forward.y, -forward.x, 0);
            var ordered = _slots.Values.OrderBy(s => s.Id).ToArray();
            if (ordered.Any(s => s.PlayerSide != ordered[0].PlayerSide))
                throw new InvalidOperationException("A native MC army spawn batch requires one allegiance");
            _request = DemoProtocol.Msg("mc_army_spawn", sid, 0);
            _request["requestId"] = Guid.NewGuid().ToString();
            _request["count"] = ordered.Length;
            _request["playerSide"] = ordered[0].PlayerSide;
            for (int i = 0; i < ordered.Length; i++)
            {
                var slot = ordered[i];
                var point = player.Position + forward * 100 + right * ((i - (ordered.Length - 1) * .5f) * 6);
                if (!Mission.IsPositionInsideBoundaries(point.AsVec2))
                {Fail("前方 100 米的凋零生成位置超出战场边界，请调整朝向后重新连接战局。");return null;}
                float terrain = Mission.Scene.GetTerrainHeight(point.AsVec2);
                if (!CombatEventLedger.IsFinite(terrain))
                {Fail("无法读取凋零出生点的地形高度，请调整朝向后重新连接战局。");return null;}
                // Sample the target's terrain directly; its elevation may differ
                // from the player's elevation a hundred metres behind it.
                float sampledGround = Mission.Scene.GetGroundHeightAtPosition(
                    new Vec3(point.x, point.y, Math.Max(terrain, player.Position.z) + 200),
                    BodyFlags.CommonCollisionExcludeFlags | BodyFlags.BodyOwnerEntity | BodyFlags.BodyOwnerFlora);
                float ground = CombatEventLedger.IsFinite(sampledGround) ? Math.Max(terrain, sampledGround) : terrain;
                point.z = ground + 10;
                var mc = hostToMc(point);
                if (!CombatEventLedger.IsFinite(mc.x) || !CombatEventLedger.IsFinite(mc.y) ||
                    !CombatEventLedger.IsFinite(mc.z) || Math.Abs(mc.y) > 2048)
                {Fail("凋零出生高度超出 MC 同步范围，请调整朝向后重新连接战局。");return null;}
                DemoLog.Diagnostic("MC_ARMY_SPAWN_HEIGHT: slot="+slot.Id+", terrain="+terrain+
                    ", sampledGround="+sampledGround+", groundUsed="+ground+", spawnZ="+point.z+", mcY="+mc.y+", heightAboveGround=10");
                slot.SpawnPosition = point;
                slot.Agent.TeleportToPosition(point);
                string prefix = "mob" + i;
                _request[prefix + "Kind"] = slot.Kind;
                _request[prefix + "Slot"] = slot.Id;
                _request[prefix + "X"] = (double)mc.x;
                _request[prefix + "Y"] = (double)mc.y;
                _request[prefix + "Z"] = (double)mc.z;
            }
            _sentAt = now;
            DemoLog.Diagnostic("MC_ARMY_SPAWN_REQUEST: count=" + ordered.Length + ", forwardDistance=100, heightAboveGround=10, request=" + _request["requestId"] + ", native player controller=" + player.Controller);
        }
        if (now < _retryAt) return null;
        _retryAt = now + 1000;
        return _request;
    }

    internal void HandleSpawnResult(Dictionary<string, object> message)
    {
        if (_request == null || DemoProtocol.S(message, "requestId") != (string)_request["requestId"]) return;
        if (!BridgeJson.B(message, "ok")) { Fail(DemoProtocol.S(message, "message")); return; }
        if (!_spawnAccepted) DemoLog.Diagnostic("MC_ARMY_NATIVE_SPAWN_ACCEPTED: " + DemoProtocol.S(message, "message"));
        _spawnAccepted = true;
    }

    internal Agent? AssignedNativeSlot(int armySlot, Dictionary<string, object> state)
    {
        if (_failed || _ending || !_slots.TryGetValue(armySlot, out var slot))
            throw new InvalidOperationException("MC creature refers to an absent native army slot");
        string id = DemoProtocol.S(state, "entityId");
        if (slot.Kind != DemoProtocol.S(state, "kind") || slot.PlayerSide != BridgeJson.B(state, "armyPlayerSide"))
            throw new InvalidOperationException("MC army slot kind or allegiance changed");
        if (slot.EntityId.Length > 0 && slot.EntityId != id) throw new InvalidOperationException("MC army slot changed UUID");
        if (slot.EntityId.Length == 0)
        {
            slot.EntityId = id;
            DemoLog.Diagnostic("MC_ARMY_NATIVE_BOUND: slot=" + armySlot + ", uuid=" + id + ", agent=" + slot.Agent.Index + ", originalTeam=" + slot.Agent.Team.Side);
        }
        if (!slot.Alive) return null;
        if (!slot.Agent.IsActive()) throw new InvalidOperationException("MC army slot disappeared before its native creature died");
        slot.Agent.SetMortalityState(Agent.MortalityState.Mortal);
        return slot.Agent;
    }

    internal bool CompleteDeath(Agent agent)
    {
        var slot = _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Agent, agent));
        if (slot == null) return false;
        if (!slot.Alive) return true;
        slot.Alive = false;
        if (agent.IsActive())
        {
            var source = slot.LastAttacker >= 0 ? Mission.FindAgentWithIndex(slot.LastAttacker) : null;
            bool validSource = source?.IsActive() == true && ReferenceEquals(source.Mission, Mission);
            var blow = new Blow(validSource ? source!.Index : -1)
            {
                DamageType = DamageTypes.Blunt, GlobalPosition = agent.Position,
                InflictedDamage = (int)Math.Max(1, agent.Health), DamageCalculated = true
            };
            blow.WeaponRecord.FillAsMeleeBlow(null, null, -1, -1);
            if (!validSource) NativeCreatureScene.MarkMissingSourceVictim(agent);
            agent.SetMortalityState(Agent.MortalityState.Mortal);
            // Agent.Die -> original Mission removal -> original supplier kill
            // count and formation/order/score callbacks, exactly once.
            agent.Die(blow);
        }
        DemoLog.Diagnostic("MC_ARMY_NATIVE_DEATH: slot=" + slot.Id + ", remainingEnemies=" + _slots.Values.Count(s => !s.PlayerSide && s.Alive));
        return true;
    }

    internal void RecordAttacker(Agent victim, int attackerIndex)
    {
        var slot = _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Agent, victim));
        var source = Mission.FindAgentWithIndex(attackerIndex);
        if (slot?.Alive == true && source?.IsActive() == true && source.IsEnemyOf(victim)) slot.LastAttacker = attackerIndex;
    }

    internal bool IsPending(Agent agent) => _slots.Values.Any(s => ReferenceEquals(s.Agent, agent) && s.Alive && s.EntityId.Length == 0);

    internal void FailSetup(string reason)
    {
        if (OwnsMission(Mission) && _expectedCount > 0) Fail(reason);
    }

    internal void NotifySessionEnded(string reason)
    {
        if (_ending || _failed || Mission.IsMissionEnding || Mission.MissionEnded || _expectedCount == 0 || !HasEnemyObjective) return;
        Fail("MC 军队连接中断：" + reason + "。请重新进入战局连接。");
    }

    private void Fail(string reason)
    {
        if (_failed || _ending) return;
        _failed = true;
        _endQueued = true;
        DemoLog.Diagnostic("MC_ARMY_ABORT: " + reason);
        InformationManager.DisplayMessage(new InformationMessage("BannerCraft: " + reason));
    }

    public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState state, KillingBlow blow)
    {
        base.OnAgentRemoved(affectedAgent, affectorAgent, state, blow);
        var slot = _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Agent, affectedAgent));
        if (slot != null && slot.Alive && !_ending && !_failed) Fail("MC 军队的原生槽在 MC 确认死亡前被移除了。");
    }

    protected override void OnEndMission()
    {
        _ending = true;
        _request = null;
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnEndMission();
    }
}

[HarmonyPatch(typeof(Mission), nameof(Mission.SpawnAgent))]
internal static class McArmyNativeBuildPatch
{
    static void Prefix(Mission __instance, AgentBuildData agentBuildData)
    {
        if (McArmyMissionBehavior.Current?.OwnsMission(__instance) != true || agentBuildData.AgentCharacter?.StringId != McArmyMissionBehavior.WitherTroopId) return;
        // Monster is immutable after Agent creation. Give the actual original
        // troop its actual Wither capsule at the native build point.
        agentBuildData.Monster(NativeCreatureCollider.Get(.9f, 3.5f)).Controller(AgentControllerType.None)
            .Equipment(new Equipment()).NoWeapons(true).NoArmor(true).NoHorses(true);
    }
}
