using System;
using System.Collections.Generic;
using Mycraft.Building;
using Mycraft.Demo;
using Mycraft.Patches;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Visuals;

public sealed class MinecraftPlayerModelBehavior : MissionBehavior
{
    private sealed class Part
    {
        public sbyte Bone;
        public GameEntity Entity = null!;
        public MatrixFrame Offset;
        public bool Head;
        public float Length;
    }
    public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    private Agent? _agent;
    private Skeleton? _skeleton;
    private readonly List<Part> _parts = new List<Part>();
    private readonly Dictionary<string,Part> _items = new Dictionary<string,Part>();
    private readonly List<Texture> _textures = new List<Texture>();
    private readonly List<Material> _materials = new List<Material>();
    private bool _originalVisible;
    private bool _hidden;
    private readonly bool[] _weaponWasVisible = new bool[5];
    private bool _failed;
    private bool _inactive;
    internal bool IsModelInstalled => _agent!=null && _hidden && !_inactive && !_failed;
    private float _readyTime;
    private float _itemTime;
    private Part? _held;
    private string _heldId = "";
    private string _heldAsset="";
    private Part? _offhand;
    private string _offhandId="";
    private string _offhandAsset="";
    private sbyte _leftUpper,_leftLower,_rightUpper,_rightLower;
    private readonly List<(GameEntity Entity,Light Light,Vec3 Offset)> _testLights = new List<(GameEntity,Light,Vec3)>();

    public override void OnMissionTick(float dt)
    {
        if (_failed || _inactive) return;
        var player=Mission.MainAgent;
        try
        {
            if(!ModelSmokeTest.RenderPlayerModel && DemoBridgeMissionBehavior.Current?.OwnsPlayer(player)!=true)
            {
                if(_agent!=null)Restore();
                _readyTime=0;
                return;
            }
            if(_agent!=null && !ReferenceEquals(_agent,player))Restore();
            if(_agent!=null&&!_agent.IsActive()){RetireInactiveModel(false);return;}
            if(player==null || !player.IsActive())return;
            if (_agent == null)
            {
                if (Mission.Mode != MissionMode.Battle || !player!.AgentVisuals.IsValid() || !player.AgentVisuals.CheckResources(true)) return;
                _readyTime += dt;
                if (_readyTime < 1f) return;
                Install(player);
            }
            _itemTime += dt;
            if (!_inactive && _agent!=null && _itemTime >= .1f && _agent.IsActive()) { _itemTime=0; UpdateItem(); }
        }
        catch(Exception e) { _failed=true; MinecraftHeroState.LogFailure("block model",e); Restore(); }
    }

    private void Install(Agent player)
    {
        DemoLog.Diagnostic("MODEL2 begin independent scene visuals");
        if (!MinecraftModelAssets.Available) throw new InvalidOperationException("Skin cache missing");
        _agent=player;
        _skeleton=player.AgentVisuals.GetSkeleton();
        if (_skeleton == null) throw new InvalidOperationException("Skeleton not ready");
        _leftUpper=Bone(HumanBone.UpperarmL);_leftLower=Bone(HumanBone.ForearmL);
        _rightUpper=Bone(HumanBone.UpperarmR);_rightLower=Bone(HumanBone.ForearmR);
        var skin=MinecraftModelAssets.Material(MinecraftModelAssets.Skin,64,out var texture);
        _textures.Add(texture); _materials.Add(skin);
        RetainMaterial(skin,texture,64);
        var hip=Rest(Bone(HumanBone.Abdomen));
        var neck=Rest(Bone(HumanBone.Neck));
        var torso=MatrixFrame.Identity;
        torso.origin=(hip+neck)*.5f;
        AddPart(Bone(HumanBone.Thorax),VoxelGeometry.SkinBox(.48f,.26f,Math.Max(.5f,Math.Min(.8f,(neck-hip).Length)),16,16,8,4,12),torso,skin);
        var head=MatrixFrame.Identity;
        head.origin=Rest(Bone(HumanBone.Head))+new Vec3(0,0,.12f);
        AddPart(Bone(HumanBone.Head),VoxelGeometry.SkinBox(.48f,.48f,.48f,0,0,8,8,8),head,skin,true);
        Limb(HumanBone.UpperarmL,HumanBone.ForearmL,HumanBone.HandL,32,48,skin);
        Limb(HumanBone.UpperarmR,HumanBone.ForearmR,HumanBone.HandR,40,16,skin);
        Limb(HumanBone.ThighL,HumanBone.CalfL,HumanBone.FootL,16,48,skin);
        Limb(HumanBone.ThighR,HumanBone.CalfR,HumanBone.FootR,0,16,skin);
        UpdateFrames();
        DemoLog.Diagnostic("MODEL2 scene parts ready; hiding original visual entity");
        _originalVisible=player.AgentVisuals.GetVisible();
        for (var i=0;i<5;i++)
        {
            if(player.Equipment[(EquipmentIndex)i].IsEmpty)continue;
            var entity=player.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)i);
            _weaponWasVisible[i]=entity.IsValid && entity.GetVisibilityExcludeParents();
        }
        player.AgentVisuals.SetVisible(false);
        _hidden=true;
        DemoLog.Diagnostic("MODEL2 original visual hidden; building held item");
        UpdateItem();
        if(DemoConfig.Load(DemoConfig.ModuleFolder).ModelTestLights) AddTestLights();
        DemoLog.Diagnostic("MODEL2 installed selected skin body: " + _parts.Count + " independent parts");
    }

    private sbyte Bone(HumanBone name)
    {
        var index=_agent!.AgentVisuals.GetRealBoneIndex(name);
        if(index<0 || index>=_skeleton!.GetBoneCount())throw new InvalidOperationException("Missing bone " + name);
        return index;
    }
    private Vec3 Rest(sbyte bone)=>_skeleton!.GetBoneEntitialRestFrame(bone,false).origin;

    private void Limb(HumanBone upper,HumanBone lower,HumanBone tip,int ux,int uy,Material material)
    {
        Segment(Bone(upper),Bone(lower),ux,uy,0,material);
        Segment(Bone(lower),Bone(tip),ux,uy,6,material);
    }
    private void Segment(sbyte from,sbyte to,int ux,int uy,int row,Material material)
    {
        var a=Rest(from); var b=Rest(to); var up=a-b; var length=up.Normalize();
        if(length<.05f || length>1f)throw new InvalidOperationException("Invalid limb rest length");
        var right=Vec3.CrossProduct(new Vec3(0,1,0),up);
        if(right.Normalize()<.01f)right=new Vec3(1,0,0);
        var forward=Vec3.CrossProduct(up,right); forward.Normalize();
        var rotation=Mat3.Identity; rotation.s=right; rotation.f=forward; rotation.u=up;
        AddPart(from,VoxelGeometry.SkinBox(.24f,.24f,length+.015f,ux,uy,4,4,12,row,6),new MatrixFrame(rotation,(a+b)*.5f),material,length:length);
    }
    private void AddPart(sbyte bone,VoxelGeometry geometry,MatrixFrame rest,Material material,bool head=false,float length=0)
    {
        var offset=_skeleton!.GetBoneEntitialRestFrame(bone,false).TransformToLocal(rest);
        var entity=CreateEntity(geometry,material);
        _parts.Add(new Part {Bone=bone,Entity=entity,Offset=offset,Head=head,Length=length});
    }
    private GameEntity CreateEntity(VoxelGeometry geometry,Material material)
    {
        var mesh=MinecraftModelAssets.Mesh(geometry,material);
        var resources=NativeSceneResources.For(Mission);resources.Hold(mesh,geometry.Vertices.Count*48L);
        var entity=GameEntity.CreateEmpty(Mission.Scene,false,false,false);
        resources.Hold(entity,1024);
        entity.SetVisibilityExcludeParents(false);
        entity.AddMesh(mesh);
        return entity;
    }
    private void RetainMaterial(Material material,Texture texture,int size)
    {
        var resources=NativeSceneResources.For(Mission);
        resources.Hold(texture,size*size*4L*64);resources.Hold(material,1024);
    }

    public override void OnPreDisplayMissionTick(float dt)
    {
        if(_agent==null || _failed || _inactive)return;
        if(!ModelSmokeTest.RenderPlayerModel && DemoBridgeMissionBehavior.Current?.OwnsPlayer(_agent)!=true)
        {Restore();return;}
        try { UpdateFrames(); }
        catch(Exception e) { _failed=true; MinecraftHeroState.LogFailure("block pose update",e); Restore(); }
    }
    private void UpdateFrames()
    {
        if(_agent==null||_inactive)return;
        if(!_agent.IsActive()){RetireInactiveModel(false);return;}
        if(_agent.AgentVisuals?.IsValid()!=true)return;
        if(_hidden)
        {
            // Native wielding can recreate or reveal equipment independently of the body.
            _agent.AgentVisuals.SetVisible(false);
            for(var i=0;i<5;i++)
            {
                if(_agent.Equipment[(EquipmentIndex)i].IsEmpty)continue;
                var entity=_agent.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)i);
                if(entity.IsValid)entity.SetVisibilityExcludeParents(false);
            }
        }
        var world=_agent.AgentVisuals.GetGlobalFrame();
        var demo=DemoBridgeMissionBehavior.Current;
        if(demo?.McSpinAttack==true)
        {
            var axis=_agent.LookDirection;
            if(axis.Normalize()>.001f)world.rotation.RotateAboutAnArbitraryVector(in axis,demo.McSpinAngle);
        }
        var shieldRaised=demo?.McUsingShield==true && _offhandId=="minecraft:shield";
        var shoulder=_agent.AgentVisuals.GetBoneEntitialFrame(_leftUpper,false).origin;
        var elbow=shoulder+new Vec3(-.08f,.20f,-.28f);
        var shieldHand=shoulder+new Vec3(.15f,.48f,-.02f);
        var pose=MinecraftWeaponPresentation.UsePose.None;bool useOffhand=false;
        if(demo?.IsEngaged==true)
        {
            useOffhand=demo.McUsingItem&&demo.McUsedHand=="offhand";
            bool loaded=useOffhand?demo.McOffhandCrossbowLoaded:demo.McCrossbowLoaded;
            pose=MinecraftWeaponPresentation.Pose(useOffhand?demo.McOffhandWeaponKind:demo.McWeaponKind,demo.McUsingItem,loaded);
            if(pose==MinecraftWeaponPresentation.UsePose.None&&demo.McOffhandCrossbowLoaded)
            {useOffhand=true;pose=MinecraftWeaponPresentation.Pose(demo.McOffhandWeaponKind,false,true);}
            if(!demo.McUsingItem&&demo.McWeaponKind=="spear"&&demo.McWeaponAttackPulse>0)
            {useOffhand=false;pose=MinecraftWeaponPresentation.UsePose.Spear;}
        }
        var rightShoulder=_agent.AgentVisuals.GetBoneEntitialFrame(_rightUpper,false).origin;
        var center=(shoulder+rightShoulder)*.5f;
        var look=_agent.LookDirection;
        var aim=new Vec3(Dot(look,world.rotation.s),Dot(look,world.rotation.f),Dot(look,world.rotation.u));
        if(aim.Normalize()<.001f)aim=new Vec3(0,1,0);
        var aimRight=Vec3.CrossProduct(aim,new Vec3(0,0,1));if(aimRight.Normalize()<.001f)aimRight=new Vec3(1,0,0);
        var aimUp=Vec3.CrossProduct(aimRight,aim);aimUp.Normalize();
        float side=useOffhand?-1:1;
        var weaponHand=center+aimRight*(side*.14f)+aim*.5f+aimUp*.04f;
        var supportHand=center-aimRight*(side*.10f)+aim*.42f+aimUp*.04f;
        if(pose==MinecraftWeaponPresentation.UsePose.Bow)
            supportHand=center-aimRight*(side*.04f)+aim*(.4f-.23f*MinecraftWeaponPresentation.BowDraw(demo?.McUseTicks??0))+aimUp*.08f;
        else if(pose==MinecraftWeaponPresentation.UsePose.CrossbowCharge)
            supportHand=center-aimRight*(side*(.23f-.13f*(demo?.McCrossbowCharge??0)))+aim*.39f;
        else if(pose==MinecraftWeaponPresentation.UsePose.Trident)
        {weaponHand=center+aimRight*(side*.28f)-aim*.06f+aimUp*.28f;supportHand=center-aimRight*(side*.16f)+aim*.34f-aimUp*.1f;}
        else if(pose==MinecraftWeaponPresentation.UsePose.Spear)
        {
            // The native PiercingWeapon attack owns the hit. This bounded pulse
            // displays only the thrust/retraction on its original long shaft.
            float jab=useOffhand?0:Math.Max(0,Math.Min(1,demo?.McWeaponAttackPulse??0));
            weaponHand=center+aimRight*(side*.2f)+aim*(.2f+.35f*jab)-aimUp*.12f;
            supportHand=center-aimRight*(side*.08f)+aim*(.54f+.18f*jab)-aimUp*.12f;
        }
        var leftHand=useOffhand?weaponHand:supportHand;var rightHand=useOffhand?supportHand:weaponHand;
        var leftElbow=(shoulder+leftHand)*.5f-aimUp*.12f-aimRight*.1f;
        var rightElbow=(rightShoulder+rightHand)*.5f-aimUp*.12f+aimRight*.1f;
        foreach(var lamp in _testLights)
        {
            var frame=MatrixFrame.Identity;
            frame.origin=world.origin+lamp.Offset;
            lamp.Entity.SetGlobalFrame(frame);
        }
        foreach(var part in _parts)
        {
            var local=_agent.AgentVisuals.GetBoneEntitialFrame(part.Bone,false).TransformToParent(part.Offset);
            if(pose!=MinecraftWeaponPresentation.UsePose.None)
            {
                if(part.Bone==_leftUpper)local=UseArmFrame(shoulder,leftElbow,part.Length);
                else if(part.Bone==_leftLower)local=UseArmFrame(leftElbow,leftHand,part.Length);
                else if(part.Bone==_rightUpper)local=UseArmFrame(rightShoulder,rightElbow,part.Length);
                else if(part.Bone==_rightLower)local=UseArmFrame(rightElbow,rightHand,part.Length);
            }
            else if(shieldRaised && part.Bone==_leftUpper)local=UseArmFrame(shoulder,elbow,part.Length);
            else if(shieldRaised && part.Bone==_leftLower)local=UseArmFrame(elbow,shieldHand,part.Length);
            var frame=world.TransformToParent(local);
            part.Entity.SetGlobalFrame(frame);
            part.Entity.SetVisibilityExcludeParents(!part.Head || !Mission.CameraIsFirstPerson || !_agent.IsActive());
        }
        if(_held!=null)
        {
            var frame=world.TransformToParent(_agent.AgentVisuals.GetBoneEntitialFrame(_held.Bone,false).TransformToParent(_held.Offset));
            if(pose!=MinecraftWeaponPresentation.UsePose.None&&!useOffhand)
                frame=world.TransformToParent(UseItemFrame(pose,weaponHand,aim,aimRight,aimUp));
            else if(pose!=MinecraftWeaponPresentation.UsePose.None)
                frame.origin=world.TransformToParent(new MatrixFrame(Mat3.Identity,rightHand)).origin;
            _held.Entity.SetGlobalFrame(frame);
            _held.Entity.SetVisibilityExcludeParents(_agent.IsActive());
        }
        if(_offhand!=null)
        {
            var frame=world.TransformToParent(_agent.AgentVisuals.GetBoneEntitialFrame(_offhand.Bone,false).TransformToParent(_offhand.Offset));
            if(pose!=MinecraftWeaponPresentation.UsePose.None&&useOffhand)
                frame=world.TransformToParent(UseItemFrame(pose,weaponHand,aim,aimRight,aimUp));
            else if(pose!=MinecraftWeaponPresentation.UsePose.None)
            {
                var support=MatrixFrame.Identity;support.origin=leftHand;
                if(_offhandId=="minecraft:shield")support.origin+=new Vec3(0,.1f,0);
                frame=world.TransformToParent(support);
            }
            else if(shieldRaised&&pose==MinecraftWeaponPresentation.UsePose.None)
            {
                var shieldFrame=MatrixFrame.Identity;
                // The grip center is at local Y=-.10. Align it to the hand;
                // the old left/bottom-origin pixel item required a wrong half-plate offset.
                shieldFrame.origin=shieldHand+new Vec3(0,.10f,0);
                frame=world.TransformToParent(shieldFrame);
            }
            if(shieldRaised&&pose==MinecraftWeaponPresentation.UsePose.None)frame.origin-=frame.rotation.f*(demo?.ShieldImpactOffset??0);
            _offhand.Entity.SetGlobalFrame(frame);
            _offhand.Entity.SetVisibilityExcludeParents(_agent.IsActive());
        }
    }

    private static float Dot(Vec3 a,Vec3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
    private static MatrixFrame UseItemFrame(MinecraftWeaponPresentation.UsePose pose,Vec3 hand,Vec3 aim,Vec3 right,Vec3 up)
    {
        var rotation=Mat3.Identity;
        if(pose==MinecraftWeaponPresentation.UsePose.Bow)
        {rotation.s=aim;rotation.u=up;rotation.f=Vec3.CrossProduct(up,aim);}
        else {rotation.s=right;rotation.u=aim;rotation.f=Vec3.CrossProduct(aim,right);}
        return new MatrixFrame(rotation,hand);
    }
    private static MatrixFrame UseArmFrame(Vec3 from,Vec3 to,float originalLength)
    {
        var up=from-to;float length=up.Normalize();
        if(length<.001f)return new MatrixFrame(Mat3.Identity,from);
        var right=Vec3.CrossProduct(new Vec3(0,1,0),up);
        if(right.Normalize()<.01f)right=new Vec3(1,0,0);
        var forward=Vec3.CrossProduct(up,right);forward.Normalize();
        var rotation=Mat3.Identity;rotation.s=right;rotation.f=forward;rotation.u=up;
        if(originalLength>.001f)rotation.u*=length/originalLength;
        return new MatrixFrame(rotation,(from+to)*.5f);
    }

    private void AddTestLights()
    {
        var offsets=new[]{new Vec3(1.5f,0,1.6f),new Vec3(-1.5f,0,1.6f),
            new Vec3(0,1.5f,1.6f),new Vec3(0,-1.5f,1.6f),new Vec3(0,0,2.8f)};
        foreach(var offset in offsets)
        {
            var entity=GameEntity.CreateEmpty(Mission.Scene,false,false,false);
            var light=Light.CreatePointLight(5f);
            _testLights.Add((entity,light,offset));
            light.LightColor=new Vec3(1f,1f,1f);
            light.Intensity=4f;
            light.ShadowEnabled=false;
            light.SetShadowType(Light.ShadowType.NoShadow);
            if(!entity.AddLight(light))throw new InvalidOperationException("Unable to attach model test light");
            entity.SetVisibilityExcludeParents(true);
        }
        MinecraftHeroState.Log("model test lights enabled: front/back/left/right/top, radius=5, intensity=4");
    }

    private void UpdateItem()
    {
        var demo=DemoBridgeMissionBehavior.Current;
        var id=demo?.IsEngaged==true ? demo.McItem : NativeItem();
        var offhandId=demo?.IsEngaged==true ? demo.McHotbar?.OffhandItem ?? "minecraft:air" : NativeOffhand();
        UpdateHand(id,false,demo?.IsEngaged==true?demo:null);
        UpdateHand(offhandId,true,demo?.IsEngaged==true?demo:null);
    }

    private void UpdateHand(string id,bool offhand,DemoBridgeMissionBehavior? demo)
    {
        string kind=demo==null?MinecraftWeaponPresentation.Kind(id):offhand?demo.McOffhandWeaponKind:demo.McWeaponKind;
        bool usingHand=demo?.McUsingItem==true&&demo.McUsedHand==(offhand?"offhand":"mainhand");
        bool loaded=demo!=null&&(offhand?demo.McOffhandCrossbowLoaded:demo.McCrossbowLoaded);
        bool firework=demo!=null&&(offhand?demo.McOffhandCrossbowFirework:demo.McCrossbowFirework);
        string name=MinecraftWeaponPresentation.Asset(id,kind,usingHand,demo?.McUseTicks??0,demo?.McCrossbowCharge??0,loaded,firework);
        if(id==(offhand?_offhandId:_heldId)&&name==(offhand?_offhandAsset:_heldAsset))return;
        var old=offhand?_offhand:_held;
        if(old!=null)old.Entity.SetVisibilityExcludeParents(false);
        if(offhand){_offhand=null;_offhandId=id;_offhandAsset=name;}else{_held=null;_heldId=id;_heldAsset=name;}
        if(id=="minecraft:air")return;
        var cacheKey=(offhand?"left:":"right:")+name;
        if(!_items.TryGetValue(cacheKey,out var item))
        {
            var block=MinecraftModelAssets.GetBlock(name);
            var shield=name=="shield";
            bool trident=kind=="trident",spear=kind=="spear";
            var pixels=shield ? MinecraftModelAssets.Shield : trident||spear?MinecraftModelAssets.GetWeapon(name):block?.Pixels ?? MinecraftModelAssets.GetItem(name);
            if(pixels==null || _items.Count>=128)return;
            var geometry=shield ? VoxelGeometry.Shield() : trident?VoxelGeometry.Trident():spear?VoxelGeometry.Item(pixels,1.7f,32,15.5f,15.5f,(float)Math.PI/4):
                block!=null ? block.Geometry() : kind=="bow"?VoxelGeometry.Item(pixels,.9f,16,7.5f,7.5f,-(float)Math.PI/4):
                kind=="crossbow"?VoxelGeometry.Item(pixels,.9f,16,10.5f,10.5f,(float)Math.PI/4):VoxelGeometry.Item(pixels);
            if(geometry.Vertices.Count==0)return;
            int textureSize=shield?64:trident||spear?32:block?.TextureSize??16;
            var material=MinecraftModelAssets.Material(pixels,textureSize,out var texture);
            _textures.Add(texture); _materials.Add(material);
            RetainMaterial(material,texture,textureSize);
            var offset=MatrixFrame.Identity;
            if(shield)
            {
                // Center the grip at the native item bone in either hand.
                if(offhand)
                {
                    offset.rotation.s=new Vec3(0,1,0); offset.rotation.f=new Vec3(-1,0,0);
                    offset.origin=new Vec3(-.10f,0,0);
                }
                else offset.origin=new Vec3(0,.10f,0);
            }
            item=new Part {Bone=Bone(offhand?HumanBone.ItemL:HumanBone.ItemR),Entity=CreateEntity(geometry,material),Offset=offset};
            _items.Add(cacheKey,item);
        }
        if(offhand)_offhand=item;else _held=item;
        MinecraftHeroState.Log("block " + (offhand?"offhand":"mainhand") + " appearance: " + id);
    }

    private string NativeOffhand()
    {
        var slot=_agent!.GetOffhandWieldedItemIndex();
        if(slot<EquipmentIndex.Weapon0 || slot>EquipmentIndex.ExtraWeaponSlot)return "minecraft:air";
        var weapon=_agent.Equipment[slot];
        if(weapon.IsEmpty)return "minecraft:air";
        var kind=weapon.CurrentUsageItem?.WeaponClass??WeaponClass.Undefined;
        return kind==WeaponClass.SmallShield || kind==WeaponClass.LargeShield ? "minecraft:shield" : "minecraft:iron_sword";
    }

    private string NativeItem()
    {
        var weapon=_agent!.WieldedWeapon;
        if(weapon.IsEmpty)return "minecraft:air";
        switch(weapon.CurrentUsageItem?.WeaponClass??WeaponClass.Undefined)
        {
            case WeaponClass.Bow:return "minecraft:bow";
            case WeaponClass.Crossbow:return "minecraft:crossbow";
            case WeaponClass.OneHandedAxe:
            case WeaponClass.TwoHandedAxe:return "minecraft:iron_axe";
            default:return "minecraft:iron_sword";
        }
    }

    public override void OnAgentRemoved(Agent affectedAgent,Agent affectorAgent,AgentState agentState,KillingBlow blow)
    {
        if(ReferenceEquals(affectedAgent,_agent))RetireInactiveModel(true);
        base.OnAgentRemoved(affectedAgent,affectorAgent,agentState,blow);
    }
    public override void OnAgentDeleted(Agent affectedAgent)
    {
        if(ReferenceEquals(affectedAgent,_agent))RetireInactiveModel(false);
        base.OnAgentDeleted(affectedAgent);
    }
    private void RetireInactiveModel(bool restoreNativeBody)
    {
        if(_inactive)return;
        var previous=_agent;bool wasHidden=_hidden;
        // Latch before any callback work. No subsequent tick may query this
        // agent's state pointer, weapon entities or native skeleton after deletion.
        _inactive=true;_agent=null;_skeleton=null;_hidden=false;_held=null;_offhand=null;
        bool bodyRestored=false;
        if(restoreNativeBody&&wasHidden&&previous!=null)
        {
            try
            {
                // OnAgentRemoved is the one-time valid removal window. Restore
                // just the native corpse body; never enumerate inactive equipment.
                var visuals=previous.AgentVisuals;
                if(visuals?.IsValid()==true){visuals.SetVisible(_originalVisible);bodyRestored=true;}
            }
            catch(Exception e){MinecraftHeroState.LogFailure("inactive body visibility",e);}
        }
        foreach(var part in _parts)part.Entity.SetVisibilityExcludeParents(false);
        foreach(var item in _items.Values)item.Entity.SetVisibilityExcludeParents(false);
        foreach(var lamp in _testLights)lamp.Entity.SetVisibilityExcludeParents(false);
        // Keep owned meshes/materials alive until ordinary mission cleanup.
        DemoLog.Diagnostic("MODEL inactive detached: nativeCorpseRestored="+bodyRestored+", bone/equipment synchronization stopped");
    }

    protected override void OnEndMission() { Restore(); base.OnEndMission(); }
    public override void OnRemoveBehavior() { Restore(); base.OnRemoveBehavior(); }
    internal void RestoreNativePlayer() => Restore();
    private void Restore()
    {
        foreach(var lamp in _testLights)
        {
            lamp.Entity.RemoveComponent(lamp.Light);
            lamp.Light.Dispose();
            lamp.Entity.Remove(0);
        }
        _testLights.Clear();
        if(_hidden && !_inactive && _agent?.IsActive()==true && _agent.AgentVisuals?.IsValid()==true)
        {
            _agent.AgentVisuals.SetVisible(_originalVisible);
            for(var i=0;i<5;i++)
            {
                if(_agent.Equipment[(EquipmentIndex)i].IsEmpty)continue;
                var entity=_agent.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)i);
                if(entity.IsValid)entity.SetVisibilityExcludeParents(_weaponWasVisible[i]);
            }
        }
        _hidden=false;
        foreach(var part in _parts)part.Entity.Remove(0);
        foreach(var item in _items.Values)item.Entity.Remove(0);
        _parts.Clear(); _items.Clear(); _held=null; _offhand=null; _agent=null; _skeleton=null;
        // Texture wrappers remain referenced until all scene meshes have been removed.
        _textures.Clear(); _materials.Clear();
        _heldId="";_heldAsset="";_offhandId="";_offhandAsset="";
        _readyTime=0;_itemTime=0;
        Array.Clear(_weaponWasVisible,0,_weaponWasVisible.Length);
    }
}
