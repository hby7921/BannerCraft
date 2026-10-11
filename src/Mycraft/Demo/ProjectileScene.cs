using System;
using System.Collections.Generic;
using System.Linq;
using Mycraft.Building;
using Mycraft.Visuals;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Display only: no native collider, damage, gravity, ammunition or flight
// simulation. Every transform and removal comes from Minecraft's projectile.
public sealed class ProjectileScene : IDisposable
{
    private const int Capacity=256;
    private sealed class Visual
    {
        public GameEntity Entity=null!;
        public string Kind="";
        public int Color=-1;
        public Mesh Mesh=null!;
        public readonly MinecraftProjectileMotion Motion=new MinecraftProjectileMotion();
        public bool InUse;
    }
    private sealed class Assets
    {
        public readonly Dictionary<string,Mesh> Meshes=new Dictionary<string,Mesh>(StringComparer.Ordinal);
        public readonly List<Texture> Textures=new List<Texture>();
        public readonly List<Material> Materials=new List<Material>();
    }
    private readonly Mission _mission;
    private readonly NativeSceneResources _resources;
    private readonly Dictionary<string,Visual> _active=new Dictionary<string,Visual>(StringComparer.Ordinal);
    private sealed class GenericProjectile
    {
        public GenericMobVisual Visual=null!;
        public string Kind="",Key="";
        public readonly MinecraftProjectileMotion Motion=new MinecraftProjectileMotion();
    }
    private readonly Dictionary<string,GenericProjectile> _generic=new Dictionary<string,GenericProjectile>(StringComparer.Ordinal);
    private readonly List<Visual> _pool=new List<Visual>();
    private Assets? _assets;
    private float _groundY,_anchorZ;
    private int _offsetX,_offsetZ;
    private bool _originSet,_disposed;
    public int ActiveCount=>_active.Count+_generic.Count;
    public string Status {get;private set;}="";

    public ProjectileScene(Mission mission){_mission=mission;_resources=NativeSceneResources.For(mission);}

    public void SetOrigin(float groundY,float anchorZ,int offsetX=0,int offsetZ=0)
    {
        if(!BlockGeometry.Finite(groundY)||!BlockGeometry.Finite(anchorZ))throw new ArgumentException("Invalid projectile origin");
        if(_originSet&&(Math.Abs(_groundY-groundY)>.001f||Math.Abs(_anchorZ-anchorZ)>.001f||_offsetX!=offsetX||_offsetZ!=offsetZ))
            throw new InvalidOperationException("Projectile origin changed during session");
        _groundY=groundY;_anchorZ=anchorZ;_offsetX=offsetX;_offsetZ=offsetZ;_originSet=true;
    }

    public void ApplyState(string entityId,string kind,float x,float y,float z,float yaw,float pitch,bool alive,int color=-1,string sid="",string visualKey="")
    {
        if(_disposed||!_originSet)return;
        try
        {
            if(!alive){Retire(entityId);return;}
            if(visualKey.Length>0)
            {
                if(!Mycraft.Bridge.NativeEntityProtocol.VisualKey(visualKey) || !Mycraft.Bridge.NativeEntityProtocol.VisualKey(sid))throw new ArgumentException("Invalid projectile model key");
                if(!alive){Retire(entityId);return;}
                if(!_generic.TryGetValue(entityId,out var projectile))
                {
                    if(ActiveCount>=Capacity){Status="Projectile visual capacity reached";return;}
                    projectile=new GenericProjectile{Kind=kind,Key=visualKey,Visual=new GenericMobVisual(_mission,sid,visualKey)};
                    _generic.Add(entityId,projectile);
                }
                if(projectile.Kind!=kind || projectile.Key!=visualKey)throw new ArgumentException("Native projectile changed model identity");
                projectile.Motion.Apply(x,y,z,yaw,pitch);Present(projectile);return;
            }
            if(!MinecraftWeaponPresentation.ValidProjectile(entityId,kind,x,y,z,yaw,pitch))throw new ArgumentException("Invalid authoritative projectile state");
            if(!alive){Retire(entityId);return;}
            if(kind=="firework_rocket")kind="firework";
            if(!_active.TryGetValue(entityId,out var visual))
            {
                if(_active.Count>=Capacity){Status="Projectile visual capacity reached";return;}
                visual=_pool.FirstOrDefault(v=>!v.InUse&&v.Kind==kind&&v.Color==color);
                if(visual==null)
                {
                    var mesh=Mesh(kind,color);
                    visual=_pool.FirstOrDefault(v=>!v.InUse);
                    if(visual!=null)
                    {
                        if(!visual.Entity.RemoveComponentWithMesh(visual.Mesh))throw new InvalidOperationException("Previous projectile mesh could not be detached");
                        visual.Entity.AddMesh(mesh);visual.Mesh=mesh;visual.Kind=kind;visual.Color=color;
                    }
                    else
                    {
                        _resources.Check(1024);
                        var entity=GameEntity.CreateEmpty(_mission.Scene,false,false,false);_resources.Hold(entity,1024);
                        entity.SetVisibilityExcludeParents(false);entity.AddMesh(mesh);
                        visual=new Visual {Entity=entity,Kind=kind,Color=color,Mesh=mesh};_pool.Add(visual);
                    }
                }
                visual.Motion.Reset();
                visual.InUse=true;_active.Add(entityId,visual);
            }
            if(visual.Kind!=kind)throw new ArgumentException("Projectile kind changed during flight");
            visual.Motion.Apply(x,y,z,yaw,pitch);Present(visual);
        }
        catch(Exception e){Status="Projectile state: "+e.Message;DemoLog.Diagnostic(Status);}
    }

    public void Tick(float dt)
    {
        if(_disposed||!_originSet||!BlockGeometry.Finite(dt)||dt<=0)return;
        try
        {
            foreach(var pair in _active.ToArray())
            {
                pair.Value.Motion.Advance(dt);
                if(pair.Value.Motion.Age>MinecraftProjectileMotion.StaleSeconds){Retire(pair.Key);continue;}
                Present(pair.Value);
            }
            foreach(var pair in _generic.ToArray())
            {
                pair.Value.Motion.Advance(dt);
                if(pair.Value.Motion.Age>MinecraftProjectileMotion.StaleSeconds){Retire(pair.Key);continue;}
                Present(pair.Value);
            }
        }
        catch(Exception e){Status="Projectile visual tick: "+e.Message;DemoLog.Diagnostic(Status);}
    }

    private void Present(Visual visual)
    {
        visual.Motion.Sample(out var x,out var y,out var z,out var yaw,out var pitch);
        var native=BlockGeometry.ToNative(x,y,z,_groundY,_anchorZ,_offsetX,_offsetZ);
        var aim=MinecraftWeaponPresentation.ProjectileDirection(yaw,pitch);var forward=new Vec3(aim.X,aim.Y,aim.Z);
        var right=Vec3.CrossProduct(forward,new Vec3(0,0,1));
        if(right.Normalize()<.001f)right=new Vec3(1,0,0);
        var depth=Vec3.CrossProduct(forward,right);depth.Normalize();
        var rotation=Mat3.Identity;rotation.s=right;rotation.f=depth;rotation.u=forward;
        visual.Entity.SetGlobalFrame(new MatrixFrame(rotation,new Vec3(native.X,native.Y,native.Z)));
        visual.Entity.SetVisibilityExcludeParents(true);
    }
    private void Present(GenericProjectile projectile)
    {
        projectile.Motion.Sample(out var x,out var y,out var z,out _,out _);
        var point=BlockGeometry.ToNative(x,y,z,_groundY,_anchorZ,_offsetX,_offsetZ);
        projectile.Visual.Update(new Vec3(point.X,point.Y,point.Z),1);
    }

    private Mesh Mesh(string kind,int color)
    {
        if(_assets==null){_assets=new Assets();_resources.Hold(_assets,8*1024*1024);}
        var key=kind+":"+color;
        if(_assets.Meshes.TryGetValue(key,out var existing))return existing;
        var pixels=kind=="firework"?MinecraftModelAssets.GetItem("firework_rocket"):MinecraftModelAssets.GetWeapon(kind);
        if(pixels==null)throw new InvalidOperationException("Original projectile texture missing: "+kind);
        if(kind=="tipped_arrow"&&color>=0)
        {
            pixels=(byte[])pixels.Clone();int red=(color>>16)&255,green=(color>>8)&255,blue=color&255;
            for(int i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(pixels[i]*red/255);pixels[i+1]=(byte)(pixels[i+1]*green/255);pixels[i+2]=(byte)(pixels[i+2]*blue/255);}
        }
        int size=kind=="firework"?16:32;
        var geometry=kind=="trident"?VoxelGeometry.Trident(false):kind=="firework"?VoxelGeometry.Item(pixels,.5f,16,7.5f,7.5f):VoxelGeometry.Arrow(pixels);
        var material=MinecraftModelAssets.Material(pixels,size,out var texture);
        _assets.Textures.Add(texture);_assets.Materials.Add(material);
        var mesh=MinecraftModelAssets.Mesh(geometry,material);_assets.Meshes.Add(key,mesh);return mesh;
    }

    private void Retire(string id)
    {
        if(_generic.TryGetValue(id,out var generic)){_generic.Remove(id);generic.Visual.Dispose();}
        if(!_active.TryGetValue(id,out var visual))return;
        _active.Remove(id);visual.InUse=false;visual.Entity.SetVisibilityExcludeParents(false);
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var visual in _pool)visual.Entity.Remove(0);
        foreach(var visual in _generic.Values)visual.Visual.Dispose();
        _generic.Clear();
        _active.Clear();_pool.Clear();
        // Mission pool retains mesh/material/texture wrappers until native
        // scene jobs and the Mission have both finished. Never clear them here.
        _assets=null;
    }
}
