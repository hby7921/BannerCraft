using System;
using System.Collections.Generic;
using System.Linq;
using Mycraft.Bridge;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

public sealed partial class DemoBridgeMissionBehavior
{
    private NativeCreatureScene? _entities;
    private readonly WeaponHitLedger _mobHits=new WeaponHitLedger();
    private readonly WeaponHitLedger _entityRemovals=new WeaponHitLedger();
    private readonly EventRetryTracker _mobHurts=new EventRetryTracker(4096);
    private readonly Dictionary<int,long> _mobEffectActors=new Dictionary<int,long>();
    private long _nextMobGround;
    private int _groundCursor;
    internal NativeCreatureScene? EntitySceneSmoke=>_entities;
    internal string SessionIdForVisuals=>_sessionId;
    private int ActorLimit=>McArmyMissionBehavior.Current?.OwnsMission(Mission)==true && McArmyMissionBehavior.Current.ExpectedCount>0 ? McArmyMissionBehavior.Current.ActorCapacity:64;
    private long _nextArmySpawnRequest;
    public string CreatureVisualStatus=>_entities!=null && _entities.Mobs.Any(m=>m.Alive && m.Kind=="wither" &&
        (m.GenericVisual?.Ready!=true || m.GenericVisual.VisiblePartCount==0 || m.GenericVisual.DiagnosticStatus=="zero_alpha_tint"))
        ? "正在同步凋零模型" : "";
    public IEnumerable<McBossState> McBosses=>_entities==null ? Enumerable.Empty<McBossState>() :
        _entities.Mobs.Where(m=>m.Alive && m.State!=null && DemoProtocol.S(m.State,"kind")=="wither")
            .Select(m=>new McBossState(m.State));
    public string RequestMobSummon(string kind)
    {
        if(!IsEngaged || !IsConnected)return "请先进入自定义野战并在 MC 执行 /mycraft connect。";
        if(!NativeEntityProtocol.MobKind(kind))return "用法：mycraft.summon wither";
        var request=DemoProtocol.Msg("mob_summon",_sessionId,0);
        request["requestId"]=Guid.NewGuid().ToString();request["kind"]=kind;
        return Send(request)?"已请求生成 "+kind+"；MC 将检查原版条件并返回结果。":"召唤请求未发送，连接已中断。";
    }
    internal int EntitySceneSmokeActorId(Agent agent)=>ActorId(agent);
    internal Vec3 EntitySceneSmokeMcPoint(Vec3 host)=>new Vec3(host.x+(float)_config.OffsetX,(float)_config.GroundY+host.z-_anchorBLz,host.y+(float)_config.OffsetZ);
    private void ReceiveEntityState(Dictionary<string,object> message)
    {
        if(!IsEngaged || _entities==null)return;
        var position=ToHostPoint((float)DemoProtocol.D(message,"x"),(float)DemoProtocol.D(message,"y"),(float)DemoProtocol.D(message,"z"));
        if(DemoProtocol.S(message,"type")=="mob_state")
        {
            // Queue genuine ground before first model construction can stall a frame.
            if(BridgeJson.B(message,"alive") && !_entities.Mobs.Any(m=>m.Id==DemoProtocol.S(message,"entityId")))
                SendEntityGround(DemoProtocol.S(message,"entityId"),position,true);
            _entities.MobState(message,position,1/(float)_config.Scale,_config.PlayerHurtFactor);
        }
        else _entities.BoatState(message,position,1/(float)_config.Scale);
    }
    private void ReceiveEntityRemoval(Dictionary<string,object> message)
    {
        string eventId=DemoProtocol.S(message,"eventId");
        var result=_entityRemovals.Register(eventId,1,_clock.ElapsedMilliseconds);
        if(result==WeaponHitLedger.Result.Duplicate){Send(DemoProtocol.Ack(_sessionId,0,eventId));return;}
        if(!IsEngaged || result!=WeaponHitLedger.Result.Accepted){Abort("invalid entity removal");return;}
        _entities?.Remove(DemoProtocol.S(message,"entityId"),DemoProtocol.S(message,"type")=="mob_remove");
        Send(DemoProtocol.Ack(_sessionId,0,eventId));
    }
    private void ReceiveMobHit(Dictionary<string,object> message)
    {
        string eventId=DemoProtocol.S(message,"eventId");
        float damage=(float)DemoProtocol.D(message,"damage");
        var result=_mobHits.Register(eventId,damage,_clock.ElapsedMilliseconds);
        if(result==WeaponHitLedger.Result.Duplicate){Send(DemoProtocol.Ack(_sessionId,0,eventId));return;}
        if(!IsEngaged || result!=WeaponHitLedger.Result.Accepted){Abort("invalid creature hit");return;}
        int id=(int)DemoProtocol.L(message,"eid");
        if(_activeActors.Contains(id) && _knownActors.TryGetValue(id,out var target) && target.IsActive() && target.Health>0 && !NativeCreatureScene.OwnsAgent(target))
        {
            _mobEffectActors[id]=_clock.ElapsedMilliseconds+12000;
            var source=_entities?.Source(DemoProtocol.S(message,"sourceId"));
            bool missing=source?.IsActive()!=true;
            var direction=missing?Vec3.Up:target.Position-source!.Position;
            if(direction.Normalize()<.001f)direction=Vec3.Up;
            float scaledDamage=(float)(damage*_config.McCreatureDamageFactor);
            ApplyMcDamage(target,scaledDamage,DemoProtocol.S(message,"kind")=="wither"?DamageTypes.Blunt:DamageTypes.Cut,direction,missing?null:source,missing);
            DemoLog.Info("MC creature damage applied: source="+DemoProtocol.S(message,"sourceId")+", target="+id+", kind="+DemoProtocol.S(message,"kind")+", mcDamage="+damage+", creatureFactor="+_config.McCreatureDamageFactor+", damage="+scaledDamage);
        }
        Send(DemoProtocol.Ack(_sessionId,0,eventId));
    }
    public bool ForwardCreatureHurt(Agent victim,int attackerIndex,int nativeDamage,string sourceKind="melee")
    {
        if(!IsEngaged || _entities==null || !_entities.TryFind(victim,out var id))return false;
        if(nativeDamage<=0 || _player==null || attackerIndex==_player.Index)return true;
        var source=Mission.FindAgentWithIndex(attackerIndex);
        if(source==null || !source.IsActive() || !source.IsHuman || NativeCreatureScene.OwnsAgent(source))return true;
        int sourceId=ActorId(source);
        // The actual attacker may be beyond the player's near-interest set.
        // Establish its real proxy before damage while retaining the64actor cap.
        if(!_activeActors.Contains(sourceId))
        {
            if(_activeActors.Count>=ActorLimit)
            {
                int evict=_activeActors.OrderBy(actorId=>_mobEffectActors.ContainsKey(actorId)?1:0)
                    .ThenByDescending(actorId=>_knownActors.TryGetValue(actorId,out var candidate) && candidate.IsActive() ? (candidate.Position-source.Position).LengthSquared : float.MaxValue).First();
                _activeActors.Remove(evict);
                Send(DemoProtocol.ActorRemove(_sessionId,0,evict));
            }
            _activeActors.Add(sourceId);
        }
        SendActorUpsert(sourceId,source);
        string eventId=Guid.NewGuid().ToString();
        var message=DemoProtocol.Msg("mob_hurt",_sessionId,0);
        message["eventId"]=eventId;message["entityId"]=id;message["sourceEid"]=sourceId;
        message["sourceKind"]=sourceKind;
        message["damage"]=nativeDamage*_config.PlayerHurtFactor;
        if(Send(message) && !_mobHurts.Track(eventId,message,_clock.ElapsedMilliseconds))Abort("creature damage outbox full");
        DemoLog.Info("Native soldier hit MC creature: source="+sourceId+", creature="+id+", nativeDamage="+nativeDamage+", mcDamage="+message["damage"]);
        return true;
    }
    private double CreatureInterest(Vec3 position)
    {
        double nearest=double.MaxValue;
        if(_entities!=null)foreach(var mob in _entities.Mobs)
            if(mob.Alive)nearest=Math.Min(nearest,(position-mob.Position).LengthSquared);
        return nearest;
    }
    private void TickEntities()
    {
        if(IsEngaged && IsConnected && _clock.ElapsedMilliseconds>=_nextArmySpawnRequest)
        {
            _nextArmySpawnRequest=_clock.ElapsedMilliseconds+1000;
            var request=McArmyMissionBehavior.Current?.TakeSpawnRequest(_sessionId,EntitySceneSmokeMcPoint);
            if(request!=null)Send(request);
        }
        var retries=_mobHurts.TakeDue(_clock.ElapsedMilliseconds,out var exhausted);
        foreach(var retry in retries)Send(retry);
        if(exhausted){Abort("creature damage acknowledgement timed out");return;}
        foreach(var expired in _mobEffectActors.Where(p=>p.Value<_clock.ElapsedMilliseconds).Select(p=>p.Key).ToArray())_mobEffectActors.Remove(expired);
        if(_entities==null || _blocks==null || _clock.ElapsedMilliseconds<_nextMobGround)return;
        var mobs=_entities.Mobs.Where(m=>m.Alive).ToArray();
        if(mobs.Length==0)return;
        _nextMobGround=_clock.ElapsedMilliseconds+100;
        var mob=mobs[_groundCursor++%mobs.Length];
        // One sampled patch per100ms, alternating current floor and target
        // corridor. Existing terrain sampler keeps real host slopes/heights.
        var center=mob.Position;
        if((_groundCursor/mobs.Length)%2!=0 && mob.TargetId>0 && _knownActors.TryGetValue(mob.TargetId,out var target) && target.IsActive())
        {
            var delta=target.Position-center;float distance=delta.Normalize();
            center+=delta*Math.Min(8,distance);
        }
        SendEntityGround(mob.Id,center);
    }
    private void SendEntityGround(string entityId,Vec3 center,bool initial=false)
    {
        if(_blocks==null)return;
        int x=BlockGeometryCell(center.x)+(int)_config.OffsetX,z=BlockGeometryCell(center.y)+(int)_config.OffsetZ;
        float y=(float)_config.GroundY+center.z-_anchorBLz;
        var ground=_blocks.CaptureEntityGroundPatch(x,z,y);
        if(ground.Count==0)return;
        var message=DemoProtocol.Msg("mob_ground",_sessionId,0);
        message["entityId"]=entityId;message["x"]=(double)x+.5;message["y"]=(double)y;message["z"]=(double)z+.5;
        message["groundCount"]=ground.Count;
        for(int i=0;i<ground.Count;i++)
        {message["ground"+i+"X"]=ground[i].X;message["ground"+i+"Y"]=ground[i].Y;message["ground"+i+"Z"]=ground[i].Z;message["ground"+i+"H"]=(double)ground[i].H;}
        Send(message);
        if(initial)DemoLog.Info("MC initial creature ground queued before model: uuid="+entityId+", samples="+ground.Count+", anchorY="+y);
    }
    private static int BlockGeometryCell(float value)=>(int)Math.Floor(value);
    private void ClearEntities()
    {
        _entities?.Dispose();_entities=null;
        _mobHits.Clear();_entityRemovals.Clear();_mobHurts.Clear();_mobEffectActors.Clear();_nextMobGround=0;_groundCursor=0;
    }
    internal void EndEntitySceneSmokeSession()
    {
        if(!Array.Exists(Environment.GetCommandLineArgs(),arg=>arg=="--mycraft-entity-scene-smoke"))
            throw new InvalidOperationException("Entity scene diagnostics are disabled");
        Teardown("entity_scene_smoke");
    }
}
