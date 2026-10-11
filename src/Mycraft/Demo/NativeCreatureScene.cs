using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Xml;
using Mycraft.Bridge;
using Mycraft.Visuals;
using TaleWorlds.Engine;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace Mycraft.Demo;

internal sealed class NativeCreatureScene : IDisposable
{
    internal sealed class Mob
    {
        public string Id="";
        public Agent? Agent;
        public WitherSkeletonVisual? Visual;
        public GenericMobVisual? GenericVisual;
        public string Kind="",VisualKey="",Collider="";
        public Dictionary<string,object> State=null!;
        public Vec3 Position;
        public long Tick=-1;
        public bool Alive;
        public int TargetId;
        public int ArmySlot;
        public string VisualDiagnosticStatus="";
        public int VisualDiagnosticCount;
    }
    private sealed class Boat
    {
        public BoatVisual Visual=null!;
        public string Kind="";
        public long Tick=-1;
    }
    // Late native removal callbacks still require the team marker after Dispose.
    private static readonly ConditionalWeakTable<Team,object> Teams=new ConditionalWeakTable<Team,object>();
    private static readonly ConditionalWeakTable<Agent,object> ArmyAgents=new ConditionalWeakTable<Agent,object>();
    private static readonly ConditionalWeakTable<Agent,object> MissingSourceVictims=new ConditionalWeakTable<Agent,object>();
    private readonly Mission _mission;
    private readonly Dictionary<string,Mob> _mobs=new Dictionary<string,Mob>(StringComparer.Ordinal);
    private readonly Dictionary<Agent,string> _agents=new Dictionary<Agent,string>();
    private readonly Dictionary<string,Boat> _boats=new Dictionary<string,Boat>(StringComparer.Ordinal);
    private readonly HashSet<string> _retired=new HashSet<string>(StringComparer.Ordinal);
    private Team? _team;
    private Team? _neutralTeam;
    private bool _disposed;
    private int _visualDiagnosticCount;
    public IEnumerable<Mob> Mobs=>_mobs.Values;
    internal int BoatCount=>_boats.Count;
    public NativeCreatureScene(Mission mission){_mission=mission;}
    internal static bool OwnsTeam(Team? team)=>team!=null && Teams.TryGetValue(team,out _);
    internal static bool OwnsAmbientAgent(Agent? agent)=>agent!=null && OwnsTeam(agent.Team);
    internal static bool OwnsArmyAgent(Agent? agent)=>agent!=null && ArmyAgents.TryGetValue(agent,out _);
    internal static bool OwnsAgent(Agent? agent)=>OwnsAmbientAgent(agent) || OwnsArmyAgent(agent);
    internal static void MarkArmyAgent(Agent agent){ArmyAgents.Remove(agent);ArmyAgents.Add(agent,new object());}
    internal static void MarkMissingSourceVictim(Agent agent){MissingSourceVictims.Remove(agent);MissingSourceVictims.Add(agent,new object());}
    internal static bool IsMissingSourceVictim(Agent agent)=>MissingSourceVictims.TryGetValue(agent,out _);
    internal static void ClearMissingSourceVictim(Agent agent)=>MissingSourceVictims.Remove(agent);
    public bool TryFind(Agent agent,out string id)=>_agents.TryGetValue(agent,out id!);
    public Agent? Source(string id)=>_mobs.TryGetValue(id,out var mob) ? mob.Agent : null;
    private Team EnsureTeam(bool hostile=true)
    {
        // Native 1.4.8 indexes only the two army sides while first calculating
        // power. Warm that cache before adding either kind of None-side team.
        var existingPower=_mission.GetMissionBehavior<BattlePowerCalculationLogic>();
        var originalArmy=_mission.Teams.FirstOrDefault(t=>t.Side==BattleSideEnum.Attacker || t.Side==BattleSideEnum.Defender);
        if(existingPower!=null && !existingPower.IsTeamPowersCalculated && originalArmy!=null)existingPower.GetTotalTeamPower(originalArmy);
        if(!hostile && _neutralTeam!=null)return _neutralTeam;
        if(!hostile)
        {
            _neutralTeam=_mission.Teams.Add(BattleSideEnum.None,isPlayerGeneral:false,isPlayerSergeant:false,isSettingRelations:false);
            Teams.Add(_neutralTeam,new object());
            foreach(var other in _mission.Teams.Where(t=>!OwnsTeam(t)))
            {_neutralTeam.SetIsEnemyOf(other,false);other.SetIsEnemyOf(_neutralTeam,false);}
            return _neutralTeam;
        }
        if(_team!=null)return _team;
        var power=_mission.GetMissionBehavior<BattlePowerCalculationLogic>();
        var original=_mission.Teams.Where(t=>t.Side==BattleSideEnum.Attacker || t.Side==BattleSideEnum.Defender).ToArray();
        if(power!=null && !power.IsTeamPowersCalculated && original.Length>0)power.GetTotalTeamPower(original[0]);
        _team=_mission.Teams.Add(BattleSideEnum.None,isPlayerGeneral:false,isPlayerSergeant:false,isSettingRelations:false);
        Teams.Add(_team,new object());
        foreach(var other in original){_team.SetIsEnemyOf(other,true);other.SetIsEnemyOf(_team,true);}
        DemoLog.Info("MC creature team created; native army teams and counters preserved");
        return _team;
    }
    public void MobState(Dictionary<string,object> state,Vec3 position,float scale,double hostToMcDamage)
    {
        if(_disposed)return;
        string id=DemoProtocol.S(state,"entityId");
        if(_retired.Contains(id))return;
        bool alive=BridgeJson.B(state,"alive");
        int armySlot=(int)DemoProtocol.L(state,"armySlot");
        if(!_mobs.TryGetValue(id,out var mob))
        {
            if(!alive)
            {
                if(armySlot>0)
                {
                    var army=McArmyMissionBehavior.Current;
                    if(army?.OwnsMission(_mission)!=true)throw new InvalidOperationException("MC army mission is missing");
                    var existing=army.AssignedNativeSlot(armySlot,state);
                    if(existing!=null)army.CompleteDeath(existing);
                    _retired.Add(id);
                }
                return;
            }
            if(_mobs.Count>=16)throw new InvalidOperationException("MC creature presentation limit reached");
            string key=DemoProtocol.S(state,"visualKey"),kind=DemoProtocol.S(state,"kind");
            mob=new Mob{Id=id,Kind=kind,VisualKey=key,ArmySlot=armySlot};
            if(key.Length>0)mob.GenericVisual=new GenericMobVisual(_mission,DemoBridgeMissionBehavior.Current!.SessionIdForVisuals,key);
            else
            {
                if(kind!="wither_skeleton" || !WitherSkeletonVisual.AssetsAvailable)throw new InvalidOperationException("Original native creature model unavailable");
                mob.Visual=new WitherSkeletonVisual(_mission);
            }
            _mobs.Add(id,mob);
        }
        long tick=DemoProtocol.L(state,"nativeTick");
        if(tick<mob.Tick)return;
        if(mob.Kind!=DemoProtocol.S(state,"kind") || mob.VisualKey!=DemoProtocol.S(state,"visualKey"))throw new InvalidOperationException("Creature identity changed model");
        if(mob.ArmySlot!=armySlot)throw new InvalidOperationException("Creature changed native army slot");
        mob.Tick=tick;mob.State=state;mob.Position=position;mob.Alive=alive;
        mob.TargetId=(int)DemoProtocol.L(state,"targetEid");
        bool ready=mob.GenericVisual?.Update(position,scale,DemoProtocol.L(state,"hurtTime")>0)==true || mob.Visual!=null;
        if(mob.GenericVisual!=null && mob.VisualDiagnosticStatus!=mob.GenericVisual.DiagnosticStatus &&
            mob.VisualDiagnosticCount<4 && _visualDiagnosticCount<128)
        {
            mob.VisualDiagnosticCount++;_visualDiagnosticCount++;
            mob.VisualDiagnosticStatus=mob.GenericVisual.DiagnosticStatus;
            var diagnostic=mob.GenericVisual.DiagnosticDetails();
            diagnostic["entityId"]=id;diagnostic["kind"]=mob.Kind;diagnostic["armySlot"]=armySlot;
            diagnostic["mcX"]=DemoProtocol.D(state,"x");diagnostic["mcY"]=DemoProtocol.D(state,"y");diagnostic["mcZ"]=DemoProtocol.D(state,"z");
            try
            {
                float terrain=_mission.Scene.GetGroundHeightAtPosition(position+Vec3.Up*32,
                    BodyFlags.CommonCollisionExcludeFlags|BodyFlags.BodyOwnerEntity|BodyFlags.BodyOwnerFlora);
                if(CombatEventLedger.IsFinite(terrain)){diagnostic["hostTerrainZ"]=(double)terrain;diagnostic["heightAboveTerrain"]=(double)(position.z-terrain);}
            }
            catch(Exception error){diagnostic["terrainDiagnosticError"]=error.Message;}
            DemoLog.Info("MC creature visual context: "+BridgeJson.Write(diagnostic));
        }
        float width=(float)DemoProtocol.D(state,"width",.7)*scale,height=(float)DemoProtocol.D(state,"height",2.4)*scale;
        string collider=NativeCreatureCollider.Key(width,height);
        if(mob.ArmySlot>0 && mob.Agent==null && alive)
        {
            var army=McArmyMissionBehavior.Current;
            if(army?.OwnsMission(_mission)!=true)throw new InvalidOperationException("MC army mission is missing");
            var original=army.AssignedNativeSlot(mob.ArmySlot,state);
            if(original==null)throw new InvalidOperationException("Retired native army slot cannot become alive again");
            mob.Agent=original;mob.Collider=collider;_agents.Add(original,id);
        }
        if(mob.Agent!=null && mob.Collider!=collider)
        {
            if(mob.ArmySlot>0)throw new InvalidOperationException("MC army creature changed collision dimensions");
            _agents.Remove(mob.Agent);if(mob.Agent.IsActive())mob.Agent.FadeOut(true,false);mob.Agent=null;
        }
        bool hostile=!state.ContainsKey("hostile") || BridgeJson.B(state,"hostile");
        if(alive && ready && mob.Agent==null && mob.ArmySlot==0)
        {
            var troop=MBObjectManager.Instance.GetObject<BasicCharacterObject>("imperial_recruit")
                ??throw new InvalidOperationException("Native passive-agent troop unavailable");
            var monster=state.ContainsKey("width")?NativeCreatureCollider.Get(width,height):
                MBObjectManager.Instance.GetObject<Monster>("bannercraft_wither_skeleton") ?? throw new InvalidOperationException("Creature collider unavailable");
            var body=_mission.SpawnAgent(new AgentBuildData(new BasicBattleAgentOrigin(troop))
                .Team(EnsureTeam(hostile)).Controller(AgentControllerType.None).Formation(null)
                .InitialPosition(position).InitialDirection(new Vec2(0,1)).Monster(monster)
                .Equipment(new Equipment()).NoWeapons(true).NoArmor(true).NoHorses(true));
            mob.Agent=body;mob.Collider=collider;_agents.Add(body,id);
            DemoLog.Info("MC "+mob.Kind+" native proxy spawned: uuid="+id+", agent="+body.Index+", width="+width+", height="+height);
        }
        var agent=mob.Agent;
        if(agent!=null && agent.IsActive())
        {
            agent.TeleportToPosition(position);
            if(alive && mob.ArmySlot==0 && agent.Team!=EnsureTeam(hostile))agent.SetTeam(EnsureTeam(hostile),false);
            if(agent.AgentVisuals?.IsValid()==true)agent.AgentVisuals.SetVisible(false);
            if(alive)
            {
                agent.HealthLimit=(float)Math.Max(1,Math.Min(10000,DemoProtocol.D(state,"maxHp")/hostToMcDamage));
                agent.Health=(float)Math.Max(1,Math.Min(agent.HealthLimit,DemoProtocol.D(state,"hp")/hostToMcDamage));
            }
            else
            {
                _agents.Remove(agent);mob.Agent=null;
                if(mob.ArmySlot>0)McArmyMissionBehavior.Current?.CompleteDeath(agent);
                else agent.FadeOut(true,false);
            }
        }
        mob.Visual?.Update(position,scale,F(state,"bodyYaw"),F(state,"headYaw"),F(state,"pitch"),F(state,"walkPosition"),F(state,"walkSpeed"),
            F(state,"attackProgress"),BridgeJson.B(state,"aggressive"),(int)DemoProtocol.L(state,"hurtTime"),(int)DemoProtocol.L(state,"deathTime"),
            (int)Math.Min(int.MaxValue,tick),DemoProtocol.S(state,"mainItem"));
    }
    public void BoatState(Dictionary<string,object> state,Vec3 position,float scale)
    {
        if(_disposed)return;
        string id=DemoProtocol.S(state,"entityId"),kind=DemoProtocol.S(state,"kind");
        if(_retired.Contains(id))return;
        if(!BridgeJson.B(state,"alive")){Remove(id,false);return;}
        if(!_boats.TryGetValue(id,out var boat))
        {
            if(_boats.Count>=16)throw new InvalidOperationException("MC boat presentation limit reached");
            if(!BoatVisual.SupportsKind(kind))throw new InvalidOperationException("Original boat model unavailable: "+kind);
            boat=new Boat{Kind=kind,Visual=new BoatVisual(_mission,kind)};_boats.Add(id,boat);
            DemoLog.Info("MC native boat displayed: uuid="+id+", kind="+kind);
        }
        if(boat.Kind!=kind)throw new InvalidOperationException("Boat identity changed type");
        long tick=DemoProtocol.L(state,"nativeTick");if(tick<boat.Tick)return;boat.Tick=tick;
        boat.Visual.Update(position,scale,F(state,"yaw"),F(state,"pitch"),F(state,"leftPaddle"),F(state,"rightPaddle"),
            F(state,"hurtTime"),F(state,"damage"),F(state,"bubbleAngle"),(int)Math.Min(int.MaxValue,tick),
            (int)DemoProtocol.L(state,"hurtDir",1),BridgeJson.B(state,"underWater"));
    }
    public void Remove(string id,bool mobKind)
    {
        if(_retired.Count>=4096)throw new InvalidOperationException("Entity retirement budget reached");
        _retired.Add(id);
        if(mobKind && _mobs.TryGetValue(id,out var mob))
        {
            if(mob.Agent!=null)
            {
                var agent=mob.Agent;_agents.Remove(agent);mob.Agent=null;
                if(mob.ArmySlot>0)McArmyMissionBehavior.Current?.CompleteDeath(agent);
                else if(agent.IsActive())agent.FadeOut(true,false);
            }
            mob.Visual?.Dispose();mob.GenericVisual?.Dispose();_mobs.Remove(id);
        }
        if(!mobKind && _boats.TryGetValue(id,out var boat)){boat.Visual.Dispose();_boats.Remove(id);}
    }
    public void AgentDeleted(Agent agent)
    {
        if(_agents.TryGetValue(agent,out var id)){_agents.Remove(agent);if(_mobs.TryGetValue(id,out var mob))mob.Agent=null;}
    }
    private static float F(Dictionary<string,object> state,string key)=>(float)DemoProtocol.D(state,key);
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var mob in _mobs.Values){if(mob.ArmySlot==0 && mob.Agent?.IsActive()==true)mob.Agent.FadeOut(true,false);mob.Agent=null;mob.Visual?.Dispose();mob.GenericVisual?.Dispose();}
        foreach(var boat in _boats.Values)boat.Visual.Dispose();
        _agents.Clear();_mobs.Clear();_boats.Clear();_retired.Clear();
    }
}

internal static class NativeCreatureCollider
{
    private static readonly ConditionalWeakTable<Game,Dictionary<string,Monster>> Cache=new ConditionalWeakTable<Game,Dictionary<string,Monster>>();
    internal static string Key(float width,float height)=>BitConverter.ToInt32(BitConverter.GetBytes(width),0).ToString("x8")+BitConverter.ToInt32(BitConverter.GetBytes(height),0).ToString("x8");
    internal static Monster Get(float width,float height)
    {
        var cache=Cache.GetValue(Game.Current,_=>new Dictionary<string,Monster>());
        string key=Key(width,height);if(cache.TryGetValue(key,out var result))return result;
        if(cache.Count>=256)throw new InvalidOperationException("Native creature collision shape budget reached");
        string id="bannercraft_body_"+key;
        var manager=MBObjectManager.Instance;result=manager.GetObject<Monster>(id);
        if(result==null)
        {
            float radius=Math.Min(width,height)*.5f;
            string N(float n)=>n.ToString("R",CultureInfo.InvariantCulture);
            string p1=width<=height?"0,0,"+N(height-radius):N((width-height)*.5f)+",0,"+N(radius);
            string p2=width<=height?"0,0,"+N(radius):N(-(width-height)*.5f)+",0,"+N(radius);
            var xml=new XmlDocument();xml.LoadXml("<Monster id='"+id+"' base_monster='human' standing_eye_height='"+N(height*.9f)+"' crouch_eye_height='"+N(height*.9f)+"'><Capsules><body_capsule radius='"+N(radius)+"' pos1='"+p1+"' pos2='"+p2+"'/><crouched_body_capsule radius='"+N(radius)+"' pos1='"+p1+"' pos2='"+p2+"'/></Capsules></Monster>");
            result=manager.CreateObjectFromXmlNode(xml.DocumentElement!) as Monster ?? throw new InvalidOperationException("Native creature collider could not be registered");
        }
        cache.Add(key,result);return result;
    }
}
