using System;
using System.Collections.Generic;
using System.Linq;
using Mycraft.Building;
using Mycraft.Visuals;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Channeling lightning is a real owned Minecraft entity. This scene mirrors
// its native seed and visible life phase without damage, physics, or timers.
public sealed class LightningScene : IDisposable
{
    private const int Capacity=24,MeshCapacity=256;
    private sealed class Bolt
    {
        public GameEntity Entity=null!;
        public Mesh? Mesh;
        public ulong Seed;
        public bool InUse;
        public float Age;
    }
    private sealed class Assets
    {
        public Material Material=null!;
        public Texture Texture=null!;
        public readonly Dictionary<ulong,Mesh> Meshes=new Dictionary<ulong,Mesh>();
    }
    private readonly Mission _mission;
    private readonly NativeSceneResources _resources;
    private readonly Dictionary<string,Bolt> _active=new Dictionary<string,Bolt>(StringComparer.Ordinal);
    private readonly List<Bolt> _pool=new List<Bolt>();
    private Assets? _assets;
    private float _groundY,_anchorZ;
    private int _offsetX,_offsetZ;
    private bool _originSet,_disposed,_failed;
    public int ActiveCount=>_active.Count;
    public string Status {get;private set;}="";

    public LightningScene(Mission mission){_mission=mission;_resources=NativeSceneResources.For(mission);}

    public void SetOrigin(float groundY,float anchorZ,int offsetX=0,int offsetZ=0)
    {
        if(!BlockGeometry.Finite(groundY)||!BlockGeometry.Finite(anchorZ))throw new ArgumentException("Invalid lightning origin");
        if(_originSet&&(Math.Abs(_groundY-groundY)>.001f||Math.Abs(_anchorZ-anchorZ)>.001f||_offsetX!=offsetX||_offsetZ!=offsetZ))
            throw new InvalidOperationException("Lightning origin changed during session");
        _groundY=groundY;_anchorZ=anchorZ;_offsetX=offsetX;_offsetZ=offsetZ;_originSet=true;
    }

    public void ApplyState(string id,float x,float y,float z,string seedHex,bool visible,bool alive)
    {
        if(_disposed||_failed||!_originSet)return;
        try
        {
            if(string.IsNullOrEmpty(id)||id.Length>128||!LightningGeometry.TrySeed(seedHex,out var seed)||
                !BlockGeometry.Finite(x)||!BlockGeometry.Finite(y)||!BlockGeometry.Finite(z)||
                Math.Abs(x)>30000000||Math.Abs(y)>30000000||Math.Abs(z)>30000000)throw new ArgumentException("Invalid native lightning state");
            if(!alive){Retire(id);return;}
            if(!_active.TryGetValue(id,out var bolt))
            {
                if(_active.Count>=Capacity){Status="Lightning visual capacity reached";return;}
                bolt=_pool.FirstOrDefault(b=>!b.InUse);
                if(bolt==null)
                {
                    _resources.Check(1024);var entity=GameEntity.CreateEmpty(_mission.Scene,false,false,false);_resources.Hold(entity,1024);
                    entity.SetVisibilityExcludeParents(false);bolt=new Bolt {Entity=entity};_pool.Add(bolt);
                }
                bolt.InUse=true;_active.Add(id,bolt);
            }
            bolt.Age=0;bolt.Entity.SetVisibilityExcludeParents(false);
            var native=BlockGeometry.ToNative(x,y,z,_groundY,_anchorZ,_offsetX,_offsetZ);
            var frame=MatrixFrame.Identity;frame.origin=new Vec3(native.X,native.Y,native.Z);bolt.Entity.SetGlobalFrame(frame);
            if(!visible)return;
            if(bolt.Mesh==null||bolt.Seed!=seed)
            {
                var mesh=GetMesh(seed);
                if(bolt.Mesh!=null&&!bolt.Entity.RemoveComponentWithMesh(bolt.Mesh))throw new InvalidOperationException("Previous lightning mesh could not be detached");
                bolt.Entity.AddMesh(mesh);bolt.Mesh=mesh;bolt.Seed=seed;
            }
            bolt.Entity.SetVisibilityExcludeParents(true);
        }
        catch(Exception e){Status="Lightning state: "+e.Message;DemoLog.Diagnostic(Status);}
    }

    public void Tick(float dt)
    {
        if(_disposed||_failed||!_originSet||!BlockGeometry.Finite(dt)||dt<=0)return;
        try
        {
            foreach(var pair in _active.ToArray())
            {
                pair.Value.Age+=Math.Min(dt,.25f);
                // Disconnected snapshots may not leave a stale bolt lit forever.
                // No host age, flash timer or random value alters a live MC phase.
                if(pair.Value.Age>2)Retire(pair.Key);
            }
        }
        catch(Exception e){Status="Lightning visual tick: "+e.Message;DemoLog.Diagnostic(Status);}
    }

    private Mesh GetMesh(ulong seed)
    {
        if(_assets==null)
        {
            var assets=new Assets();_resources.Hold(assets,16*1024*1024);
            try
            {
            assets.Material=MinecraftModelAssets.Material(new byte[]{255,255,255,255},1,out var texture);assets.Texture=texture;
            var material=assets.Material;
            material.UsingDynamicLight=false;material.UsingSunLight=false;material.UsingSpecular=false;
            material.IsSunShadowReceiver=false;material.IsDynamicShadowReceiver=false;
            // Local1.5.4 Material shader flags: unlit/self illumination0x80000,
            // diffuse alpha0x800; disable hemispherical/cubic ambient lighting.
            material.SetShaderFlags((material.GetShaderFlags()&~(0x4UL|0x200000UL))|0x80000UL|0x800UL);
            // MC BlendFunction.LIGHTNING is SRC_ALPHA,ONE (additive alpha).
            material.SetAlphaBlendMode(Material.MBAlphaBlendMode.AddAlpha);
            material.Flags|=MaterialFlags.DontCastShadow|MaterialFlags.NoModifyDepthBuffer|MaterialFlags.RequiresForwardRendering;
            _assets=assets;
            }
            catch {_failed=true;throw;}
        }
        if(_assets.Meshes.TryGetValue(seed,out var existing))return existing;
        // Never clear an in-flight native mesh. Retain each seed geometry until
        // its Mission is collectible, with an explicit bounded lifetime budget.
        if(_assets.Meshes.Count>=MeshCapacity)throw new InvalidOperationException("Lightning retained mesh capacity reached");
        var geometry=LightningGeometry.Build(seed);var builder=new MeshBuilder();
        foreach(var vertex in geometry.Vertices)builder.AddFaceCorner(new Vec3(vertex.X,vertex.Y,vertex.Z),
            new Vec3(vertex.Nx,vertex.Ny,vertex.Nz),new Vec2(.5f,.5f),uint.MaxValue);
        for(int i=0;i<geometry.Indices.Count;i+=3)builder.AddFace(geometry.Indices[i],geometry.Indices[i+1],geometry.Indices[i+2]);
        var mesh=builder.Finalize();mesh.SetMaterial(_assets.Material);mesh.Color=LightningGeometry.NativeColor;
        mesh.VisibilityMask=VisibilityMaskFlags.DefaultDynamic;mesh.UpdateBoundingBox();_assets.Meshes.Add(seed,mesh);return mesh;
    }

    private void Retire(string id)
    {
        if(!_active.TryGetValue(id,out var bolt))return;
        _active.Remove(id);bolt.InUse=false;bolt.Entity.SetVisibilityExcludeParents(false);
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var bolt in _pool)bolt.Entity.Remove(0);
        _active.Clear();_pool.Clear();_assets=null;
    }
}
