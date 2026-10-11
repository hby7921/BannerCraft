using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mycraft.Building;
using Mycraft.Visuals;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;
using Path=System.IO.Path;

namespace Mycraft.Demo;

public sealed partial class TntScene : IDisposable
{
    private sealed class Primed
    {
        public GameEntity Normal=null!,White=null!;
        public TntSceneMotion Motion=new TntSceneMotion();
        public bool InUse;
    }
    private sealed class Particle
    {
        public GameEntity Entity=null!;public Mesh Mesh=null!;
        public Vec3 Position,Previous,Velocity;public float Size;public int Age,Lifetime,Frame=-1,Shade;public bool Alive,Smoke;
    }
    private sealed class Emitter
    {
        public Vec3 Position;public float Radius;public int Tick;public Random Random=null!;
    }
    private sealed class Assets
    {
        public readonly List<Texture> Textures=new List<Texture>();
        public readonly List<Material> Materials=new List<Material>();
        public Mesh Tnt=null!,White=null!;
        public Mesh?[,] ExplosionMeshes=new Mesh?[16,16],SmokeMeshes=new Mesh?[8,8];
        public Material ParticleMaterial=null!;
    }
    private static byte[]? _tntPixels;
    private static byte[][]? _explosionPixels;
    private static byte[][]? _smokePixels;
    private readonly Mission _mission;
    private readonly NativeSceneResources _resources;
    private readonly Dictionary<string,Primed> _primed=new Dictionary<string,Primed>();
    private readonly List<Primed> _primedPool=new List<Primed>();
    private readonly List<Particle> _particles=new List<Particle>();
    private readonly List<Emitter> _emitters=new List<Emitter>();
    private readonly HashSet<string> _ended=new HashSet<string>(),_exploded=new HashSet<string>();
    private readonly bool[] _presentedFrames=new bool[16];
    private readonly bool[] _presentedSmokeFrames=new bool[8];
    private readonly TntParticleClock _particleClock=new TntParticleClock();
    private readonly Random _smokeRandom=new Random();
    private Assets? _assets;
    private float _groundY,_anchorZ;
    private int _offsetX,_offsetZ;
    private bool _originSet,_disposed,_failed;
    public string Status {get;private set;}="";
    public int ActiveCount=>_primed.Count;
    public int ActiveParticleCount=>_particles.Count(x=>x.Alive);
    public int ActiveExplosionParticleCount=>_particles.Count(x=>x.Alive&&!x.Smoke);
    public int ActiveSmokeParticleCount=>_particles.Count(x=>x.Alive&&x.Smoke);
    public int SmokeParticlesEmitted {get;private set;}
    public int AnimationFramesVisited=>_presentedFrames.Count(x=>x);
    public int SmokeFramesVisited=>_presentedSmokeFrames.Count(x=>x);
    public TntScene(Mission mission){_mission=mission;_resources=NativeSceneResources.For(mission);}

    // Module-load only; no file access in TNT state or render ticks.
    public static void Cache(string moduleFolder)
    {
        try
        {
            string visuals=Path.Combine(moduleFolder,"Visuals");
            var tnt=File.ReadAllBytes(Path.Combine(visuals,"blocks","tnt.atlas.rgba"));
            if(tnt.Length!=16384)throw new InvalidDataException("TNT atlas must be 64x64 RGBA");
            var frames=new byte[16][];
            for(int i=0;i<frames.Length;i++)
            {
                frames[i]=File.ReadAllBytes(Path.Combine(visuals,"particles","explosion_"+i+".rgba"));
                if(frames[i].Length!=4096)throw new InvalidDataException("Explosion frame must be 32x32 RGBA");
            }
            var smoke=new byte[8][];
            for(int i=0;i<smoke.Length;i++)
            {
                smoke[i]=File.ReadAllBytes(Path.Combine(visuals,"particles","smoke_"+i+".rgba"));
                if(smoke[i].Length!=256)throw new InvalidDataException("Smoke frame must be 8x8 RGBA");
            }
            _tntPixels=tnt;_explosionPixels=frames;_smokePixels=smoke;
            DemoLog.Diagnostic("TNT assets cached: native TNT overlay, 16 explosion + 8 smoke frames; 20Hz host reproduction; audio owner=Minecraft");
        }
        catch(Exception e){_tntPixels=null;_explosionPixels=null;_smokePixels=null;DemoLog.Diagnostic("TNT asset cache failed: "+e);}
    }
    public void SetOrigin(float groundY,float anchorZ,int offsetX=0,int offsetZ=0)
    {
        if(!BlockGeometry.Finite(groundY)||!BlockGeometry.Finite(anchorZ))throw new ArgumentException("Invalid TNT origin");
        if(_originSet&&(Math.Abs(_groundY-groundY)>.001f||Math.Abs(_anchorZ-anchorZ)>.001f||_offsetX!=offsetX||_offsetZ!=offsetZ))throw new InvalidOperationException("TNT origin changed during session");
        _groundY=groundY;_anchorZ=anchorZ;_offsetX=offsetX;_offsetZ=offsetZ;_originSet=true;
    }
    public void ApplyState(string id,float x,float y,float z,int fuse,bool alive)
    {
        if(_disposed||_failed||!_originSet)return;
        try
        {
            if(!TntSceneTiming.ValidId(id)||!TntSceneTiming.ValidPosition(x,y,z)||fuse<0||fuse>32767)throw new ArgumentException("Invalid primed TNT state");
            if(!alive){Retire(id);return;}
            if(_ended.Contains(id))return;
            EnsureAssets();
            if(!_primed.TryGetValue(id,out var value))
            {
                if(_primed.Count>=64)throw new InvalidOperationException("Primed TNT visual capacity reached");
                value=_primedPool.FirstOrDefault(p=>!p.InUse);
                if(value==null)
                {
                    value=new Primed {Normal=Entity(_assets!.Tnt),White=Entity(_assets.White)};
                    _primedPool.Add(value);
                }
                value.InUse=true;value.Motion=new TntSceneMotion();_primed.Add(id,value);
                DemoLog.Diagnostic("TNT primed visual created: "+id+", fuse="+fuse);
            }
            value.Motion.Apply(x,y,z,fuse,true);
        }
        catch(Exception e){Fail("TNT state",e);}
    }
    public void Explode(string id,float x,float y,float z,float radius)
    {
        if(_disposed||_failed||!_originSet)return;
        try
        {
            if(!TntSceneTiming.ValidId(id)||!TntSceneTiming.ValidPosition(x,y,z)||!BlockGeometry.Finite(radius)||radius<=0||radius>32)
                throw new ArgumentException("Invalid explosion state");
            if(_exploded.Contains(id))return;
            if(_exploded.Count>=2048)throw new InvalidOperationException("Explosion event capacity reached");
            EnsureAssets();_exploded.Add(id);if(_primed.ContainsKey(id))Retire(id);
            // Visual saturation must not affect reliable explosion damage or flood the log.
            if(_emitters.Count>=32)_emitters.RemoveAt(0);
            _emitters.Add(new Emitter {Position=World(x,y,z),Radius=radius,Random=new Random(id.GetHashCode())});
            DemoLog.Diagnostic("TNT explosion visual begun: "+id+", blastRadius="+radius+", visualSpread="+TntSceneTiming.ExplosionVisualSpread(radius)+
                ", visualQuadScale="+TntSceneTiming.VisualQuadScale+", damageRadiusUnchanged=true, audio=Minecraft");
        }
        catch(Exception e){Fail("TNT explosion",e);}
    }
    public void Tick(float dt)
    {
        if(_disposed||_failed||!_originSet||!BlockGeometry.Finite(dt)||dt<=0)return;
        dt=Math.Min(dt,.25f);
        try
        {
            foreach(var pair in _primed.ToArray())
            {
                var p=pair.Value;p.Motion.Advance(dt);
                if(p.Motion.Age>5){Retire(pair.Key);Status="TNT visual removed after missing authoritative state";continue;}
                p.Motion.Sample(out var x,out var y,out var z);
                float remaining=TntSceneTiming.Remaining(p.Motion.Fuse,p.Motion.Age),scale=TntSceneTiming.Scale(remaining);
                var frame=MatrixFrame.Identity;frame.rotation.s*=scale;frame.rotation.f*=scale;frame.rotation.u*=scale;
                frame.origin=World(x,y,z)+new Vec3(0,0,.5f-.5f*scale);
                p.Normal.SetGlobalFrame(frame);p.White.SetGlobalFrame(frame);
                bool white=TntSceneTiming.White(remaining);p.Normal.SetVisibilityExcludeParents(!white);p.White.SetVisibilityExcludeParents(white);
            }
            int simulationSteps=_particleClock.Advance(dt);
            for(int step=0;step<simulationSteps;step++)SimulateParticles();
            MatrixFrame camera=ScreenManager.TopScreen is MissionScreen screen?screen.CombatCamera.Frame:MatrixFrame.Identity;
            foreach(var particle in _particles)
            {
                if(!particle.Alive)continue;
                int frameIndex=TntSceneTiming.ParticleFrame(particle.Age,particle.Lifetime,particle.Smoke?8:16);
                if(particle.Frame!=frameIndex)
                {
                    var mesh=ParticleMesh(particle.Smoke,frameIndex,particle.Shade);
                    if(particle.Mesh!=null&&!particle.Entity.RemoveComponentWithMesh(particle.Mesh))
                        throw new InvalidOperationException("Previous particle frame mesh could not be detached");
                    particle.Entity.AddMesh(mesh);particle.Mesh=mesh;particle.Frame=frameIndex;
                    if(particle.Smoke)_presentedSmokeFrames[frameIndex]=true;else _presentedFrames[frameIndex]=true;
                }
                float size=particle.Size*(particle.Smoke?TntSceneTiming.SmokeGrowth(particle.Age,_particleClock.PartialTick,particle.Lifetime):1);
                // Vanilla age-zero smoke can have zero area. Never give native
                // entity transforms a singular scale; hide that subpixel frame.
                if(size<=.0001f){particle.Entity.SetVisibilityExcludeParents(false);continue;}
                var rotation=Mat3.Identity;rotation.s=camera.rotation.s*size;rotation.f=-camera.rotation.u*size;rotation.u=camera.rotation.f*size;
                var position=particle.Previous+(particle.Position-particle.Previous)*_particleClock.PartialTick;
                particle.Entity.SetGlobalFrame(new MatrixFrame(rotation,position));particle.Entity.SetVisibilityExcludeParents(true);
            }
        }
        catch(Exception e){Fail("TNT visual tick",e);}
    }
    private void SimulateParticles()
    {
        // Match vanilla age++ >= lifetime, not continuous render-frame aging.
        // Existing particles advance before births: catch-up batches retain
        // different ages and one 50ms update cannot emit a double first batch.
        foreach(var p in _particles)
        {
            if(!p.Alive)continue;
            if(p.Age++>=p.Lifetime){p.Alive=false;p.Entity.SetVisibilityExcludeParents(false);continue;}
            p.Previous=p.Position;
            if(p.Smoke)
            {
                // Unobstructed Particle.tick motion. Exact MC voxel collisions
                // are not transported by this bounded visual reproduction.
                p.Velocity.z+=TntSceneTiming.SmokeRisePerTick;p.Position+=p.Velocity;p.Velocity*=TntSceneTiming.SmokeFriction;
            }
        }
        foreach(var primed in _primed.Values)
        {
            if(primed.Motion.Age>.25f||TntSceneTiming.Remaining(primed.Motion.Fuse,primed.Motion.Age)<=0)continue;
            primed.Motion.Sample(out var x,out var y,out var z);
            SpawnSmoke(World(x,y+.5f,z));
        }
        foreach(var emitter in _emitters.ToArray())
        {
            for(int i=0;i<6;i++)SpawnParticle(emitter);
            if(++emitter.Tick==8)_emitters.Remove(emitter);
        }
    }
    private Particle? AcquireParticle(bool smoke)
    {
        if(!TntSceneTiming.CanSpawnParticle(smoke,ActiveSmokeParticleCount,ActiveExplosionParticleCount))return null;
        var p=_particles.FirstOrDefault(x=>!x.Alive);
        if(p==null)
        {
            if(_particles.Count>=384)return null;
            _resources.Check(1024);var entity=GameEntity.CreateEmpty(_mission.Scene,false,false,false);_resources.Hold(entity,1024);
            entity.SetVisibilityExcludeParents(false);p=new Particle {Entity=entity};_particles.Add(p);
        }
        p.Age=0;p.Frame=-1;p.Alive=true;p.Smoke=smoke;p.Velocity=Vec3.Zero;
        return p;
    }
    private void SpawnParticle(Emitter emitter)
    {
        var p=AcquireParticle(false);if(p==null)return;
        float Offset()=>(float)(emitter.Random.NextDouble()-emitter.Random.NextDouble())*TntSceneTiming.ExplosionVisualSpread(emitter.Radius);
        p.Position=emitter.Position+new Vec3(Offset(),Offset(),Offset());p.Previous=p.Position;
        p.Lifetime=6+emitter.Random.Next(4);p.Size=TntSceneTiming.ExplosionVisualQuadSize(emitter.Tick);
        p.Shade=emitter.Random.Next(16);
    }
    private void SpawnSmoke(Vec3 position)
    {
        var p=AcquireParticle(true);if(p==null)return;
        var sample=TntSceneTiming.SmokeSample(_smokeRandom);
        p.Position=p.Previous=position;p.Velocity=new Vec3(sample.X,sample.Z,sample.Y);
        p.Lifetime=sample.Lifetime;p.Size=sample.Width;
        p.Shade=Math.Min(7,(int)Math.Round(sample.Shade/.3f*7));
        SmokeParticlesEmitted++;
    }
    private void EnsureAssets()
    {
        if(_assets!=null)return;
        if(_tntPixels==null||_explosionPixels==null||_smokePixels==null)throw new InvalidOperationException("TNT visual assets were not generated or cached");
        var assets=new Assets();_resources.Hold(assets,8*1024*1024);_assets=assets;
        try
        {
        var normal=MinecraftModelAssets.Material(_tntPixels,64,out var normalTexture);assets.Materials.Add(normal);assets.Textures.Add(normalTexture);
        var white=MinecraftModelAssets.Material(TntSceneTiming.WhiteOverlay(_tntPixels),64,out var whiteTexture);assets.Materials.Add(white);assets.Textures.Add(whiteTexture);
        assets.Tnt=MinecraftModelAssets.Mesh(VoxelGeometry.HeldBlockBottomTop(1),normal);
        assets.White=MinecraftModelAssets.Mesh(VoxelGeometry.HeldBlockBottomTop(1),white);
        var particleMaterial=MinecraftModelAssets.Material(new byte[]{255,255,255,255},1,out var particleTexture);
        particleMaterial.SetAlphaBlendMode(Material.MBAlphaBlendMode.NoAlphaBlend);
        assets.ParticleMaterial=particleMaterial;assets.Materials.Add(particleMaterial);assets.Textures.Add(particleTexture);
        DemoLog.Diagnostic("TNT native assets ready; particle transparency uses original pixel silhouette geometry");
        }
        catch {_failed=true;throw;}
    }
    private Mesh ParticleMesh(bool smoke,int frame,int shade)
    {
        var cache=smoke?_assets!.SmokeMeshes:_assets!.ExplosionMeshes;
        var existing=cache[frame,shade];if(existing!=null)return existing;
        int size=smoke?8:32;
        var spans=ParticleSilhouette.Read(smoke?_smokePixels![frame]:_explosionPixels![frame],size);
        var builder=new MeshBuilder();var normal=new Vec3(0,-1,0);var uv=new Vec2(.5f,.5f);int vertex=0;
        foreach(var span in spans)
        {
            float left=(float)span.Start/size-.5f,right=(float)span.End/size-.5f;
            float bottom=.5f-(float)(span.Row+1)/size,top=.5f-(float)span.Row/size;
            builder.AddFaceCorner(new Vec3(left,0,bottom),normal,uv,uint.MaxValue);
            builder.AddFaceCorner(new Vec3(right,0,bottom),normal,uv,uint.MaxValue);
            builder.AddFaceCorner(new Vec3(right,0,top),normal,uv,uint.MaxValue);
            builder.AddFaceCorner(new Vec3(left,0,top),normal,uv,uint.MaxValue);
            builder.AddFace(vertex,vertex+1,vertex+2);builder.AddFace(vertex,vertex+2,vertex+3);vertex+=4;
        }
        var mesh=builder.Finalize();mesh.SetMaterial(_assets.ParticleMaterial);
        uint channel=(uint)Math.Round(255*(smoke?shade/7.0*.3:.4+shade/15.0*.6));
        mesh.Color=0xff000000u|(channel<<16)|(channel<<8)|channel;
        mesh.VisibilityMask=VisibilityMaskFlags.DefaultDynamic;mesh.UpdateBoundingBox();
        cache[frame,shade]=mesh;return mesh;
    }
    private GameEntity Entity(Mesh mesh)
    {
        _resources.Check(1024);var entity=GameEntity.CreateEmpty(_mission.Scene,false,false,false);_resources.Hold(entity,1024);
        entity.SetVisibilityExcludeParents(false);entity.AddMesh(mesh);return entity;
    }
    private static Mesh Billboard(Material material)
    {
        var builder=new MeshBuilder();var normal=new Vec3(0,-1,0);
        builder.AddFaceCorner(new Vec3(-.5f,0,-.5f),normal,new Vec2(0,0),uint.MaxValue);
        builder.AddFaceCorner(new Vec3(.5f,0,-.5f),normal,new Vec2(1,0),uint.MaxValue);
        builder.AddFaceCorner(new Vec3(.5f,0,.5f),normal,new Vec2(1,1),uint.MaxValue);
        builder.AddFaceCorner(new Vec3(-.5f,0,.5f),normal,new Vec2(0,1),uint.MaxValue);
        builder.AddFace(0,1,2);builder.AddFace(0,2,3);var mesh=builder.Finalize();mesh.SetMaterial(material);mesh.VisibilityMask=VisibilityMaskFlags.DefaultDynamic;mesh.UpdateBoundingBox();return mesh;
    }
    private Vec3 World(float x,float y,float z)
    {
        var native=BlockGeometry.ToNative(x,y,z,_groundY,_anchorZ,_offsetX,_offsetZ);
        return new Vec3(native.X,native.Y,native.Z);
    }
    internal static BlockModelSnapshot SmokeSnapshot(bool compound)
    {
        if(_tntPixels==null)throw new InvalidOperationException("TNT smoke asset missing");
        var geometry=VoxelGeometry.HeldBlockBottomTop(1);
        var quads=new BlockModelSnapshot.Quad[6];int[] order={0,3,2,1};
        for(int f=0;f<6;f++)
        {
            var normal=geometry.Vertices[f*4];
            var quad=new BlockModelSnapshot.Quad {Material=0,Tint=uint.MaxValue,Vertices=new float[20],NormalX=normal.Nx,NormalY=normal.Nz,NormalZ=normal.Ny};
            for(int i=0;i<4;i++)
            {
                var v=geometry.Vertices[f*4+order[i]];int p=i*5;
                quad.Vertices[p]=v.X+.5f;quad.Vertices[p+1]=v.Z;quad.Vertices[p+2]=v.Y+.5f;quad.Vertices[p+3]=v.U;quad.Vertices[p+4]=v.V;
            }
            quads[f]=quad;
        }
        var boxes=compound?new[]{new BlockBox(0,0,0,.4f,.5f,1),new BlockBox(.6f,0,0,1,1,1)}:new[]{new BlockBox(0,0,0,1,1,1)};
        return new BlockModelSnapshot {Textures=new[]{new BlockModelSnapshot.Image {Width=64,Height=64,Pixels=_tntPixels}},Quads=quads,
            Collision=boxes,Outline=boxes,ByteLength=_tntPixels.Length+6*100+64};
    }
    private void Retire(string id)
    {
        if(_ended.Count<2048)_ended.Add(id);
        if(!_primed.TryGetValue(id,out var p))return;
        _primed.Remove(id);p.InUse=false;p.Normal.SetVisibilityExcludeParents(false);p.White.SetVisibilityExcludeParents(false);
        DemoLog.Diagnostic("TNT primed visual retired: "+id);
    }
    private void Fail(string stage,Exception error){Status=stage+": "+error.Message;DemoLog.Diagnostic(Status+Environment.NewLine+error);}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        DisposeReferenceSprites();
        foreach(var p in _primedPool){p.Normal.Remove(0);p.White.Remove(0);}
        foreach(var p in _particles)p.Entity.Remove(0);
        _primed.Clear();_primedPool.Clear();_particles.Clear();_emitters.Clear();_ended.Clear();_exploded.Clear();
        // Materials/meshes remain retained by the Mission pool until native jobs
        // and the Mission have finished. No PhysicsShape.Clear/manual invalidation.
        _assets=null;
    }
}
