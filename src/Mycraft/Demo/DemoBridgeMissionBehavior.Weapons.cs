using System;
using System.Collections.Generic;
using Mycraft.Bridge;
using Mycraft.Patches;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace Mycraft.Demo;

public sealed partial class DemoBridgeMissionBehavior
{
    public string McWeaponKind {get;private set;}="none";
    public string McOffhandWeaponKind {get;private set;}="none";
    public string McUsedHand {get;private set;}="none";
    public int McUseTicks {get;private set;}
    public float McCrossbowCharge {get;private set;}
    public bool McCrossbowLoaded {get;private set;}
    public bool McCrossbowFirework {get;private set;}
    public bool McOffhandCrossbowLoaded {get;private set;}
    public bool McOffhandCrossbowFirework {get;private set;}
    private ProjectileScene? _projectileScene;
    private LightningScene? _lightningScene;
    private readonly WeaponHitLedger _weaponHits=new WeaponHitLedger();
    private readonly Dictionary<string,TaleWorlds.Library.Vec3> _projectilePositions=new Dictionary<string,TaleWorlds.Library.Vec3>();
    private readonly Dictionary<string,TaleWorlds.Library.Vec3> _projectileVelocities=new Dictionary<string,TaleWorlds.Library.Vec3>();
    public float McEyeHeight {get;private set;}=1.62f;
    private int _aimedActorIndex=-1;
    private readonly Dictionary<string,MissionWeapon> _carriers=new Dictionary<string,MissionWeapon>();
    private MissionWeapon _savedCarrierMain;
    private string _carrierKind="";
    private bool _weaponLeftDown;
    private long _weaponAttackAt=-1000;
    public float McWeaponAttackPulse=>Math.Max(0,1-(_clock.ElapsedMilliseconds-_weaponAttackAt)/250f);
    private long _spinStateAt;
    private TaleWorlds.Library.Vec3 _spinVelocity;
    public bool McSpinAttack {get;private set;}
    public float McSpinAngle {get;private set;}
    private bool UsesNativeWeaponInput=>McWeaponKind=="bow"||McWeaponKind=="crossbow"||McWeaponKind=="trident"||McWeaponKind=="spear";

    private void ReadWeaponState(Dictionary<string,object> message)
    {
        McWeaponKind=DemoProtocol.S(message,"weaponKind");
        McOffhandWeaponKind=DemoProtocol.S(message,"offhandWeaponKind");
        McUsedHand=DemoProtocol.S(message,"usedHand");
        McUseTicks=(int)DemoProtocol.L(message,"useTicks");
        McCrossbowCharge=(float)DemoProtocol.D(message,"crossbowCharge");
        McCrossbowLoaded=BridgeJson.B(message,"crossbowLoaded");
        McCrossbowFirework=BridgeJson.B(message,"crossbowFirework");
        McOffhandCrossbowLoaded=BridgeJson.B(message,"offhandCrossbowLoaded");
        McOffhandCrossbowFirework=BridgeJson.B(message,"offhandCrossbowFirework");
        McEyeHeight=(float)DemoProtocol.D(message,"eyeHeight",1.62);
        if(IsEngaged)SyncWeaponCarrier();
    }

    private void SyncWeaponCarrier()
    {
        if(_player?.IsActive()!=true || _carrierKind==McWeaponKind)return;
        var kind=McWeaponKind;
        MissionWeapon carrier;
        if(!_carriers.TryGetValue(kind,out carrier))
        {
            var weaponClass=kind=="bow"?WeaponClass.Bow:kind=="crossbow"?WeaponClass.Crossbow:
                kind=="trident"?WeaponClass.Javelin:kind=="spear"?WeaponClass.OneHandedPolearm:WeaponClass.OneHandedSword;
            bool found=false;
            foreach(var item in MBObjectManager.Instance.GetObjectTypeList<ItemObject>())
            {
                if(item.Weapons==null)continue;
                for(var usage=0;usage<item.Weapons.Count;usage++)
                {
                    if(item.Weapons[usage].WeaponClass!=weaponClass)continue;
                    carrier=new MissionWeapon(item,null,null);carrier.CurrentUsageIndex=usage;found=true;break;
                }
                if(found)break;
            }
            if(!found){Abort("native animation carrier missing: "+kind);return;}
            _carriers[kind]=carrier;
        }
        if(!_player.Equipment[EquipmentIndex.Weapon0].IsEmpty)_player.RemoveEquippedWeapon(EquipmentIndex.Weapon0);
        if(!carrier.IsEmpty)
        {
            _player.EquipWeaponWithNewEntity(EquipmentIndex.Weapon0,ref carrier);
            _player.TryToWieldWeaponInSlot(EquipmentIndex.Weapon0,Agent.WeaponWieldActionType.Instant,false);
        }
        _carrierKind=kind;
        MinecraftHeroState.Log("MC weapon carrier: "+McItem+" -> "+kind);
    }

    private void SendWeaponAttack()
    {
        if(_player?.IsActive()!=true || McUsingItem || _useInputDown || McSpinAttack)return;
        var id=Guid.NewGuid().ToString("N");
        if(McWeaponKind!="spear" && !_attacks.TryIssue(id,_clock.ElapsedMilliseconds)){Abort("attack ledger full");return;}
        _weaponAttackAt=_clock.ElapsedMilliseconds;
        SyncActors();
        var pos=_player.Position;var dir=ResolveRangedAim();
        var mc=_transform!.BlToMc(pos.x,pos.y,pos.z,Math.Atan2(dir.x,dir.y)*180/Math.PI);
        var message=DemoProtocol.AttackInput(_sessionId,0,id,mc.X,mc.Y+(pos.z-_anchorBLz)*_config.Scale,mc.Z,mc.Yaw);
        message["pitch"]=-Math.Asin(Math.Max(-1,Math.Min(1,dir.z)))*180/Math.PI;
        AddMotion(message,_player);Send(message);
    }

    private void ReceiveWeaponHit(Dictionary<string,object> message)
    {
        var id=DemoProtocol.S(message,"eventId");
        var result=_weaponHits.Register(id,DemoProtocol.D(message,"damage"),_clock.ElapsedMilliseconds);
        if(result==WeaponHitLedger.Result.Duplicate){Send(DemoProtocol.Ack(_sessionId,0,id));return;}
        if(!IsEngaged||result!=WeaponHitLedger.Result.Accepted){Abort("invalid native weapon hit: "+result);return;}
        int eid=(int)DemoProtocol.L(message,"eid");
        if(_knownActors.TryGetValue(eid,out var actor)&&actor.IsActive()&&actor.Health>0&&!ReferenceEquals(actor,_player))
        {
            ApplyMcDamage(actor,(float)DemoProtocol.D(message,"damage"),DamageTypes.Pierce,_player!.LookDirection);
            MinecraftHeroState.Log("MC ranged damage: kind="+DemoProtocol.S(message,"kind")+", eid="+eid+
                ", source="+DemoProtocol.S(message,"sourceId")+", damage="+DemoProtocol.D(message,"damage"));
        }
        else MinecraftHeroState.Log("MC ranged target retired before receipt: eid="+eid+", source="+DemoProtocol.S(message,"sourceId"));
        Send(DemoProtocol.Ack(_sessionId,0,id));
    }

    private void AddMotion(Dictionary<string,object> message,Agent actor)
    {
        // Host metres/second -> MC displacement per20Hz tick, with hostY becoming MCz.
        var speed=actor.Velocity;
        message["vx"]=(double)speed.x*_config.Scale/20;message["vy"]=(double)speed.z*_config.Scale/20;message["vz"]=(double)speed.y*_config.Scale/20;
    }

    private void ClearWeapons()
    {
        try{_projectileScene?.Dispose();}catch(Exception e){MinecraftHeroState.LogFailure("projectile teardown",e);}
        _projectileScene=null;_projectilePositions.Clear();_projectileVelocities.Clear();_aimedActorIndex=-1;
        _weaponHits.Clear();_carriers.Clear();_carrierKind="";_weaponLeftDown=false;
        try{_lightningScene?.Dispose();}catch(Exception e){MinecraftHeroState.LogFailure("lightning teardown",e);}_lightningScene=null;
        McWeaponKind="none";McOffhandWeaponKind="none";McUsedHand="none";McUseTicks=0;McCrossbowCharge=0;
        McCrossbowLoaded=false;McCrossbowFirework=false;McOffhandCrossbowLoaded=false;McOffhandCrossbowFirework=false;
        McSpinAttack=false;_spinVelocity=TaleWorlds.Library.Vec3.Zero;_spinStateAt=0;
    }

    private void ReceiveNativeMotion(Dictionary<string,object> message)
    {
        McSpinAttack=BridgeJson.B(message,"spinning");_spinStateAt=_clock.ElapsedMilliseconds;
        _spinVelocity=new TaleWorlds.Library.Vec3((float)DemoProtocol.D(message,"vx")*20,
            (float)DemoProtocol.D(message,"vz")*20,(float)DemoProtocol.D(message,"vy")*20);
        McSpinAngle=(DemoProtocol.L(message,"nativeTick")%360)*-75*(float)Math.PI/180;
    }

    private void TickNativeMotion(float dt)
    {
        if(!McSpinAttack || _player?.IsActive()!=true)return;
        if(_clock.ElapsedMilliseconds-_spinStateAt>200 || !OwnsHotbarWheel){StopNativeSpin();return;}
        var displacement=_spinVelocity*Math.Max(0,Math.Min(dt,.05f));
        if(displacement.LengthSquared<.000001f)return;
        var position=_player.Position;
        var ignored=_player.AgentVisuals.GetEntity().WeakEntity;
        // The native launch supplies velocity; real host terrain/scenery still stops the body.
        foreach(float height in new[]{.35f,.95f,1.5f})
        {
            var start=position+new TaleWorlds.Library.Vec3(0,0,height);var end=start+displacement;
            if(Mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in start,in end,ignored,out var distance,out var entity,
                .2f,TaleWorlds.Engine.BodyFlags.CommonFocusRayCastExcludeFlags) && distance<displacement.Length+.2f)
            {StopNativeSpin();return;}
        }
        var destination=position+displacement;
        if(!Mission.IsPositionInsideBoundaries(destination.AsVec2)){StopNativeSpin();return;}
        _player.TeleportToPosition(destination);
    }

    private void StopNativeSpin()
    {
        McSpinAttack=false;_spinVelocity=TaleWorlds.Library.Vec3.Zero;
        var message=DemoProtocol.Msg("stop_spin",_sessionId,0);message["requestId"]=Guid.NewGuid().ToString("N");Send(message);
    }

    private void RemoveActorProxy(Agent actor)
    {
        if(!_actorIds.TryGetValue(actor,out var id))return;
        _actorIds.Remove(actor);_knownActors.Remove(id);_activeActors.Remove(id);
        if(IsConnected)Send(DemoProtocol.ActorRemove(_sessionId,0,id));
        if(ReferenceEquals(actor,_enemy))_enemy=null;
    }
    public override void OnAgentRemoved(Agent affectedAgent,Agent affectorAgent,AgentState agentState,KillingBlow blow)
    {
        _entities?.AgentDeleted(affectedAgent);
        RemoveActorProxy(affectedAgent);base.OnAgentRemoved(affectedAgent,affectorAgent,agentState,blow);
    }
    public override void OnAgentDeleted(Agent affectedAgent)
    {
        _entities?.AgentDeleted(affectedAgent);
        RemoveActorProxy(affectedAgent);
        if(ReferenceEquals(affectedAgent,_player))_player=null;
        base.OnAgentDeleted(affectedAgent);
    }

    private TaleWorlds.Library.Vec3 ResolveRangedAim()
    {
        var fallback=_player!.LookDirection;_aimedActorIndex=-1;
        if(!(TaleWorlds.ScreenSystem.ScreenManager.TopScreen is TaleWorlds.MountAndBlade.View.Screens.MissionScreen screen))return fallback;
        screen.ScreenPointToWorldRay(new TaleWorlds.Library.Vec2(.5f,.5f),out var origin,out var end);
        var direction=end-origin;
        if(!RangedTargeting.Finite(direction.x)||!RangedTargeting.Finite(direction.y)||!RangedTargeting.Finite(direction.z)||direction.Normalize()<.001f)return fallback;
        var eye=_player.Position+new TaleWorlds.Library.Vec3(0,0,McEyeHeight/(float)_config.Scale);
        if(!Mycraft.Building.BlockGeometry.CameraReach(origin.x,origin.y,origin.z,direction.x,direction.y,direction.z,
            eye.x,eye.y,eye.z,(float)RangedTargeting.MaximumDistance,out var distance))return direction;
        end=origin+direction*distance;float nearest=distance;
        var ignored=_player.AgentVisuals.GetEntity().WeakEntity;
        if(Mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in origin,in end,ignored,out var sceneDistance,out var entity,
            .001f,TaleWorlds.Engine.BodyFlags.CommonFocusRayCastExcludeFlags)&&RangedTargeting.Finite(sceneDistance)&&sceneDistance>=0)
            nearest=Math.Min(nearest,sceneDistance);
        var actor=Mission.RayCastForClosestAgent(origin,end,_player.Index,.001f,out var actorDistance);
        if(actor!=null && RangedTargeting.Finite(actorDistance)&&actorDistance>=0 && actorDistance<=nearest+.05f)
        {nearest=Math.Min(nearest,actorDistance);_aimedActorIndex=actor.Index;}
        var shot=origin+direction*nearest-eye;
        if(shot.Normalize()<.1f||TaleWorlds.Library.Vec3.DotProduct(shot,direction)<=0)return direction;
        return shot;
    }
}
