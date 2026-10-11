using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mycraft.Patches;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace Mycraft.Building;

// Native scene work is main-thread only. File polling/decoding is confined to
// two background readers; MC block_state/model snapshots remain authoritative.
public sealed class BattleBlockScene : IDisposable
{
    private const int ModelLimit=64, MaterialLimit=256, ShapeLimit=1024;
    private const long DataBudget=32L*1024*1024, TextureBudget=64L*1024*1024;
    private sealed class Desired { public string Item="",Model=""; }
    private sealed class PreparedModel
    {
        public BlockModelSnapshot Data=null!;
        public BlockSurfaceGeometry.Surface[] Surfaces=Array.Empty<BlockSurfaceGeometry.Surface>();
    }
    private sealed class NativeModel
    {
        public BlockModelSnapshot Data=null!;
        public readonly List<Mesh> Meshes=new List<Mesh>();
        public readonly List<Texture> Textures=new List<Texture>();
        public readonly List<Material> Materials=new List<Material>();
        public long TextureBytes;
    }
    private sealed class Placed
    {
        public string Model="";
        public GameEntity Visual=null!;
        public readonly List<GameEntity> Colliders=new List<GameEntity>();
    }
    private sealed class SmokeRay
    {
        public Vec3 Start,End;
        public UIntPtr Expected;
        public float Entry,Exit;
    }
    private sealed class SmokeControl
    {
        public GameEntity Entity=null!;
        public BodyFlags Flags;
        public bool Visible;
    }
    private readonly Mission _mission;
    private readonly Dictionary<(int x,int y,int z),Desired> _desired=new Dictionary<(int,int,int),Desired>();
    private readonly Dictionary<(int x,int y,int z),Placed> _blocks=new Dictionary<(int,int,int),Placed>();
    private readonly Dictionary<string,NativeModel> _models=new Dictionary<string,NativeModel>();
    private readonly Dictionary<string,Task<PreparedModel>> _pending=new Dictionary<string,Task<PreparedModel>>();
    private readonly Dictionary<string,string> _failures=new Dictionary<string,string>();
    private readonly Dictionary<(int x,int y,int z),List<SmokeRay>> _smokeRays=new Dictionary<(int,int,int),List<SmokeRay>>();
    private readonly List<SmokeControl> _smokeControls=new List<SmokeControl>();
    private CancellationTokenSource _cancel=new CancellationTokenSource();
    private readonly NativeSceneResources _resources;
    private float _groundY,_anchorZ;
    private int _offsetX,_offsetZ;
    private bool _originSet,_disposed;
    private string _session="";
    private DateTime _sessionStart;
    private long _dataBytes,_textureBytes;
    private int _materialCount,_liveShapes;
    private readonly string _snapshotRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Mycraft","block-models");
    public int Count=>_desired.Count;
    public string ItemAt(int x,int y,int z)=>_desired.TryGetValue((x,y,z),out var desired)?desired.Item:"minecraft:air";
    public string Status {get;private set;}="";
    public BattleBlockScene(Mission mission) { _mission=mission;_resources=NativeSceneResources.For(mission); }
    public static void Cache(string moduleFolder)=>MinecraftHeroState.Log("placed blocks use native Minecraft model snapshots");

    public void SetSession(string sid)
    {
        if(_disposed || sid==_session)return;
        if(!BlockModelSnapshot.ValidKey(sid)){Status="Invalid block model session";return;}
        Reset();_session=sid;_sessionStart=DateTime.UtcNow;Status="";
    }
    public void SetOrigin(float groundY,float anchorBLz,int offsetX=0,int offsetZ=0)
    {
        if(!BlockGeometry.Finite(groundY)||!BlockGeometry.Finite(anchorBLz))throw new ArgumentException("Invalid block origin");
        if(_originSet&&(Math.Abs(_groundY-groundY)>.001f||Math.Abs(_anchorZ-anchorBLz)>.001f||_offsetX!=offsetX||_offsetZ!=offsetZ))
            throw new InvalidOperationException("Block origin cannot change during a session");
        _groundY=groundY;_anchorZ=anchorBLz;_offsetX=offsetX;_offsetZ=offsetZ;_originSet=true;
    }
    public void Apply(int x,int y,int z,string item,float groundY,float anchorBLz)=>Apply(x,y,z,item,groundY,anchorBLz,"");
    public void Apply(int x,int y,int z,string item,float groundY,float anchorBLz,string modelKey)
    {
        if(_disposed)return;
        try
        {
            SetOrigin(groundY,anchorBLz,_offsetX,_offsetZ);
            var cell=(x,y,z);
            if(item=="minecraft:air"||item=="minecraft:cave_air"||item=="minecraft:void_air")
            { RemoveCell(cell);_desired.Remove(cell);EvictUnused();RetryBudgets();return; }
            if(_session.Length==0)throw new InvalidOperationException("Block model session is not connected");
            if(!BlockGeometry.Allowed(item)||!BlockModelSnapshot.ValidKey(modelKey))throw new InvalidOperationException("Block state lacks a valid Minecraft model snapshot");
            if(!BlockGeometry.CanAdd(Count,_desired.ContainsKey(cell)))throw new InvalidOperationException("Placed block limit reached");
            _desired[cell]=new Desired {Item=item,Model=modelKey};
            if(_blocks.TryGetValue(cell,out var existing)&&existing.Model==modelKey){Status="Minecraft block ready";return;}
            // Keep the committed native instance until its replacement is fully
            // uploaded and has working collision, including lit-furnace updates.
            Status=_failures.TryGetValue(modelKey,out var error)?error:"Waiting for Minecraft model: "+item;
        }
        catch(Exception e){Report("Block state: "+e.Message,e);}
    }

    public void Tick()
    {
        if(_disposed||_session.Length==0)return;
        // Complete at most one asset upload and two scene instances per frame.
        foreach(var entry in _pending.ToArray())
        {
            if(!entry.Value.IsCompleted)continue;
            _pending.Remove(entry.Key);
            try
            {
                var prepared=entry.Value.GetAwaiter().GetResult();var data=prepared.Data;
                if(!_desired.Values.Any(x=>x.Model==entry.Key))continue;
                EvictUnused();
                var model=CreateModel(data,prepared.Surfaces);
                _models.Add(entry.Key,model);_dataBytes+=data.ByteLength;_textureBytes+=model.TextureBytes;_materialCount+=model.Materials.Count;
            }
            catch(Exception e){_failures[entry.Key]="Minecraft block model unavailable: "+e.Message;Report(_failures[entry.Key],e);}
            break;
        }
        int installed=0;
        foreach(var entry in _desired)
        {
            if((_blocks.TryGetValue(entry.Key,out var current)&&current.Model==entry.Value.Model)||_failures.ContainsKey(entry.Value.Model))continue;
            if(_models.TryGetValue(entry.Value.Model,out var model))
            {
                if(installed>=2)continue;
                try { Install(entry.Key,entry.Value.Model,model);installed++; }
                catch(Exception e){_failures[entry.Value.Model]="Block scene model failed: "+e.Message;Report(_failures[entry.Value.Model],e);}
            }
            else if(_pending.Count<2&&!_pending.ContainsKey(entry.Value.Model))
            {
                string sid=_session,key=entry.Value.Model;var started=_sessionStart;var token=_cancel.Token;
                _pending.Add(key,Task.Run(async()=>
                {
                    var loaded=await BlockModelSnapshot.LoadAsync(_snapshotRoot,sid,key,started,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new PreparedModel {Data=loaded,Surfaces=BlockSurfaceGeometry.Build(loaded)};
                }));
            }
        }
        var failure=_desired.Values.FirstOrDefault(x=>_failures.ContainsKey(x.Model));
        if(failure!=null)Status=_failures[failure.Model];
        else
        {
            int waiting=_desired.Count(x=>!_blocks.TryGetValue(x.Key,out var current)||current.Model!=x.Value.Model);
            if(waiting>0)Status="Waiting for Minecraft block models: "+waiting;
        }
    }

    private NativeModel CreateModel(BlockModelSnapshot data,BlockSurfaceGeometry.Surface[]? prepared=null)
    {
        var surfaces=prepared??BlockSurfaceGeometry.Build(data);
        long bytes=0;
        foreach(var surface in surfaces){var image=surface.Image;int scale=PixelScale(image);bytes+=(long)image.Width*image.Height*4*scale*scale;}
        if(_models.Count>=ModelLimit||_dataBytes+data.ByteLength>DataBudget||_textureBytes+bytes>TextureBudget||_materialCount+surfaces.Length>MaterialLimit)
            throw new InvalidOperationException("Native block model cache budget reached");
        var model=new NativeModel {Data=data,TextureBytes=bytes};
        _resources.Hold(model,data.ByteLength+bytes+surfaces.Sum(s=>(long)s.Vertices.Count*48+s.Indices.Count*4L));
        Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: build model textures="+data.Textures.Length+", quads="+data.Quads.Length+", visible tint groups="+surfaces.Length);
        try
        {
            foreach(var surface in surfaces)
            {
                var image=surface.Image;int scale=PixelScale(image),width=image.Width*scale,height=image.Height*scale;
                Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: texture "+surface.Material+" "+width+"x"+height+", tint="+surface.Tint.ToString("x8"));
                // Bake the actual Minecraft world/power tint into this texture,
                // avoiding the native default shader's ambiguous corner tint.
                var tinted=BlockSurfaceGeometry.TintPixels(image,surface.Tint);
                var pixels=new byte[width*height*4];bool blend=false;
                for(int p=3;p<image.Pixels.Length;p+=4)blend|=image.Pixels[p]>0&&image.Pixels[p]<255;
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    Buffer.BlockCopy(tinted,((y/scale)*image.Width+x/scale)*4,pixels,(y*width+x)*4,4);
                var texture=Texture.CreateFromByteArray(pixels,width,height);texture.SetTextureAsAlwaysValid();model.Textures.Add(texture);
                var material=Material.GetDefaultMaterial().CreateCopy();model.Materials.Add(material);
                material.SetEnableSkinning(false);material.UsingDynamicLight=true;
                material.SetTexture(Material.MBTextureType.DiffuseMap,texture);
                // Fully transparent texels have no triangles. Preserve partial
                // alpha for genuinely translucent textures without depending
                // on alpha-testing for wire/torch/leaf silhouettes.
                material.UsingDiffuseAlphaMap=blend;material.SetAlphaTestValue(.01f);
                if(blend)material.SetShaderFlags(material.GetShaderFlags()|0x800UL); // Local Material.UseDiffuseAlphaMap.
                material.SetAlphaBlendMode(blend?Material.MBAlphaBlendMode.Modulate:Material.MBAlphaBlendMode.NoAlphaBlend);
                var builder=new MeshBuilder();
                foreach(var vertex in surface.Vertices)
                {
                    builder.AddFaceCorner(new Vec3(vertex.X,vertex.Y,vertex.Z),
                        new Vec3(vertex.Nx,vertex.Ny,vertex.Nz),new Vec2(vertex.U,vertex.V),uint.MaxValue);
                }
                for(int i=0;i<surface.Indices.Count;i+=3)builder.AddFace(surface.Indices[i],surface.Indices[i+1],surface.Indices[i+2]);
                Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: finalize surface "+surface.Material+", vertices="+surface.Vertices.Count);
                var mesh=builder.Finalize();mesh.SetMaterial(material);mesh.VisibilityMask=VisibilityMaskFlags.DefaultDynamic;mesh.UpdateBoundingBox();model.Meshes.Add(mesh);
            }
            return model;
        }
        catch {throw;}
    }
    private static int PixelScale(BlockModelSnapshot.Image image)=>Math.Max(image.Width,image.Height)<=16?4:Math.Max(image.Width,image.Height)<=32?2:1;

    private void Install((int x,int y,int z) cell,string modelKey,NativeModel model)
    {
        int replacedShapes=_blocks.TryGetValue(cell,out var previous)?previous.Colliders.Count:0;
        if(_liveShapes-replacedShapes+model.Data.Collision.Length>ShapeLimit)throw new InvalidOperationException("Live block collision budget reached");
        bool tnt=_desired.TryGetValue(cell,out var desired)&&desired.Item=="minecraft:tnt";
        bool overlaps=!CanPlaceBoxes(cell.x,cell.y,cell.z,model.Data.Collision,false,tnt);
        var placed=new Placed {Model=modelKey};
        try
        {
            var frame=MatrixFrame.Identity;frame.origin=World(cell.x,cell.y,cell.z);
            _resources.Check(1024);
            Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: create visual at "+frame.origin);
            placed.Visual=GameEntity.CreateEmpty(_mission.Scene,false,false,false);placed.Visual.SetGlobalFrame(frame);
            _resources.Hold(placed.Visual,1024);
            foreach(var mesh in model.Meshes)placed.Visual.AddMesh(mesh);
            placed.Visual.SetVisibilityExcludeParents(true);
            foreach(var box in model.Data.Collision)
            {
                var entity=NativeBlockCollider.Create(_mission,box,frame.origin);placed.Colliders.Add(entity);
            }
            // Commit only after every new mesh and collider is ready. Failure
            // above leaves the old model and collision alive and reports status.
            RemoveCell(cell);
            _blocks[cell]=placed;_liveShapes+=placed.Colliders.Count;
            Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: committed "+cell+", colliders="+placed.Colliders.Count);
            Status=overlaps?"Minecraft block rendered; its collision overlaps native scenery or an agent":"Minecraft block ready";
        }
        catch {RemovePlaced(placed);throw;}
    }

    public bool TryInteractionTarget(Agent player,out BlockInteractionTarget target)
    {
        try{return TryInteractionTargetCore(player,out target);}
        catch(Exception e){target=null!;Report("Block targeting failed: "+e.Message,e);return false;}
    }
    private bool TryInteractionTargetCore(Agent player,out BlockInteractionTarget target)
    {
        target=null!;
        if(_disposed||!_originSet||player==null||!player.IsActive()||!player.AgentVisuals.IsValid()||!(ScreenManager.TopScreen is MissionScreen screen))return false;
        screen.ScreenPointToWorldRay(new Vec2(.5f,.5f),out var origin,out var end);
        var dir=end-origin;if(!Finite(origin)||!Finite(dir)||dir.Normalize()<.001f)return false;
        var eye=player.GetEyeGlobalPosition();
        if(!BlockGeometry.CameraReach(origin.x,origin.y,origin.z,dir.x,dir.y,dir.z,eye.x,eye.y,eye.z,8,out var reach))return false;
        end=origin+dir*reach;
        // Ignore only the player's own body; rocks, buildings and other agents
        // still occlude the exact center-screen ray in third-person view.
        var ignored=player.AgentVisuals.GetEntity().WeakEntity;
        bool hit=_mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in origin,in end,ignored,out var sceneDistance,out var collided,.001f,BodyFlags.CommonFocusRayCastExcludeFlags);
        if(hit&&(!BlockGeometry.Finite(sceneDistance)||sceneDistance<0))return false;
        bool owned=hit&&collided!=null&&OwnsCollider(collided);
        bool nativeObject=hit&&collided!=null&&!owned&&(collided.BodyFlag&BodyFlags.BodyOwnerTerrain)==0;
        float nearest=hit?Math.Min(reach,sceneDistance+(owned?.02f:0)):reach;
        var mcOrigin=BlockGeometry.ToMinecraft(origin.x,origin.y,origin.z,_groundY,_anchorZ,_offsetX,_offsetZ);
        foreach(var entry in _blocks)
        {
            var cell=entry.Key;var data=_models[entry.Value.Model].Data;
            foreach(var box in data.Outline)
            {
                if(!BlockGeometry.RayBox(mcOrigin.X,mcOrigin.Y,mcOrigin.Z,dir.x,dir.z,dir.y,BlockGeometry.Translate(box,cell.x,cell.y,cell.z),nearest,
                    out var distance,out var nx,out var ny,out var nz))continue;
                var point=origin+dir*distance;if((point-eye).LengthSquared>64.0001f)continue;
                var mcPoint=BlockGeometry.ToMinecraft(point.x,point.y,point.z,_groundY,_anchorZ,_offsetX,_offsetZ);
                nearest=distance;target=new BlockInteractionTarget {X=cell.x,Y=cell.y,Z=cell.z,Face=BlockGeometry.Face(nx,ny,nz),
                    HitX=mcPoint.X,HitY=mcPoint.Y,HitZ=mcPoint.Z,PlaceX=cell.x+nx,PlaceY=cell.y+ny,PlaceZ=cell.z+nz,TntPlacementY=cell.y+ny};
            }
        }
        if(target!=null)return true;
        if(!hit||nativeObject||owned)return false;
        var terrainPoint=origin+dir*sceneDistance;if((terrainPoint-eye).LengthSquared>64.0001f)return false;
        int x=BlockGeometry.Cell(terrainPoint.x)+_offsetX,z=BlockGeometry.Cell(terrainPoint.y)+_offsetZ;
        if(!TryTerrainHeight(x,z,x+1,z+1,terrainPoint.z,out var highest))return false;
        var support=BlockGeometry.TntGroundSupport(x,z,highest,_groundY,_anchorZ);
        target=new BlockInteractionTarget {X=x,Y=support.Y,Z=z,Face=1,Terrain=true,HitX=terrainPoint.x+_offsetX,HitY=support.Top,HitZ=terrainPoint.y+_offsetZ,
            PlaceX=x,PlaceY=BlockGeometry.GroundCell(highest,_groundY,_anchorZ),PlaceZ=z,TntPlacementY=support.TntPlacementY,SurfaceHeight=support.H};
        return true;
    }
    public bool TryTarget(Agent player,out int x,out int y,out int z,out bool bridgeBlock,out int placeX,out int placeY,out int placeZ)
    {
        x=y=z=placeX=placeY=placeZ=0;bridgeBlock=false;
        if(!TryInteractionTarget(player,out var target))return false;
        x=target.X;y=target.Y;z=target.Z;bridgeBlock=!target.Terrain;placeX=target.PlaceX;placeY=target.PlaceY;placeZ=target.PlaceZ;return true;
    }

    public bool CanPlace(int x,int y,int z)=>CanPlace(x,y,z,"");
    public List<(int X,int Y,int Z)> CaptureGroundPatch(int centerX,int centerZ,float nearMcY)
    {
        var result=new List<(int X,int Y,int Z)>();
        if(_disposed||!_originSet||!BlockGeometry.Finite(nearMcY))return result;
        float nearHeight=BlockGeometry.Height(nearMcY,_groundY,_anchorZ);
        for(int dx=-4;dx<=4;dx++)for(int dz=-4;dz<=4;dz++)
        {
            int x=centerX+dx,z=centerZ+dz;
            if(!CellInsideBoundaries(x,z))continue;
            if(TryTerrainHeight(x,z,x+1,z+1,nearHeight,out var terrain))
                result.Add((x,BlockGeometry.GroundCell(terrain,_groundY,_anchorZ)-1,z));
        }
        return result;
    }
    public List<GroundSupport> CaptureTntGroundPatch(int centerX,int centerZ,float nearMcY)
    {
        var result=new List<GroundSupport>();
        if(_disposed||!_originSet||!BlockGeometry.Finite(nearMcY))return result;
        float nearHeight=BlockGeometry.Height(nearMcY,_groundY,_anchorZ);
        for(int dx=-4;dx<=4;dx++)for(int dz=-4;dz<=4;dz++)
        {
            int x=centerX+dx,z=centerZ+dz;
            if(!CellInsideBoundaries(x,z))continue;
            if(TryTerrainHeight(x,z,x+1,z+1,nearHeight,out var terrain))
                result.Add(BlockGeometry.TntGroundSupport(x,z,terrain,_groundY,_anchorZ));
        }
        return result;
    }

    public List<GroundSupport> CaptureEntityGroundPatch(int centerX,int centerZ,float nearMcY)
        =>BlockGeometry.EntityGroundPatch(CaptureTntGroundPatch(centerX,centerZ,nearMcY),nearMcY);

    // Explicit native smoke only: exercises the production upload/prefab path
    // without requiring a live Minecraft client or touching its world.
    internal void ApplySmokeSnapshot(int x,int y,int z,string modelKey,BlockModelSnapshot snapshot)
    {
        if(!Environment.GetCommandLineArgs().Contains("--mycraft-tnt-scene-smoke"))throw new InvalidOperationException("Smoke flag missing");
        if(!_models.ContainsKey(modelKey))
        {
            var model=CreateModel(snapshot);_models.Add(modelKey,model);_dataBytes+=snapshot.ByteLength;_textureBytes+=model.TextureBytes;_materialCount+=model.Materials.Count;
        }
        Apply(x,y,z,"minecraft:tnt",_groundY,_anchorZ,modelKey);Tick();
    }
    internal bool ProbeSmokeCollision(int x,int y,int z)
    {
        if(!_blocks.TryGetValue((x,y,z),out var placed))return false;
        var boxes=_models[placed.Model].Data.Collision;
        if(boxes.Length!=placed.Colliders.Count||boxes.Length==0)return false;
        var rays=new List<SmokeRay>();_smokeRays[(x,y,z)]=rays;bool success=true;
        for(int i=0;i<boxes.Length;i++)
        {
            var b=boxes[i];var center=World(x+(b.MinX+b.MaxX)*.5f,y+(b.MinY+b.MaxY)*.5f,z+(b.MinZ+b.MaxZ)*.5f);
            var collider=placed.Colliders[i];
            var ray=new SmokeRay {Start=center-new Vec3(0,2,0),End=center+new Vec3(0,2,0),Expected=collider.Pointer,
                Entry=2-(b.MaxZ-b.MinZ)*.5f,Exit=2+(b.MaxZ-b.MinZ)*.5f};rays.Add(ray);
            bool hit=_mission.Scene.RayCastForClosestEntityOrTerrain(ray.Start,ray.End,out var distance,out Vec3 point,out WeakGameEntity entity,.001f,BodyFlags.CommonFocusRayCastExcludeFlags);
            bool passed=hit&&collider==entity&&Math.Abs(distance-ray.Entry)<.03f;success&=passed;
            var bounds=collider.ComputeGlobalPhysicsBoundingBoxMinMax();
            Mycraft.Demo.DemoLog.Diagnostic("TNT_SMOKE_RAY_CREATE cell="+(x,y,z)+", box="+i+", start="+ray.Start+", end="+ray.End+
                ", expected="+ray.Expected+", hit="+hit+", actual="+(entity.IsValid?entity.Pointer:UIntPtr.Zero)+", distance="+distance+
                ", expectedEntry="+ray.Entry+", point="+point+", physics="+collider.GetPhysicsState()+", flags="+collider.BodyFlag+
                ", visible="+collider.GetVisibilityExcludeParents()+", bounds="+bounds.Item1+".."+bounds.Item2+", passed="+passed);
            if(!passed)
            {
                // Diagnostic comparison only: an unfiltered hit never makes
                // the production-mask acceptance check pass.
                bool rawHit=_mission.Scene.RayCastForClosestEntityOrTerrain(ray.Start,ray.End,out var rawDistance,out Vec3 rawPoint,out WeakGameEntity rawEntity,.001f,BodyFlags.None);
                Mycraft.Demo.DemoLog.Diagnostic("TNT_SMOKE_RAY_UNFILTERED cell="+(x,y,z)+", box="+i+", hit="+rawHit+
                    ", actual="+(rawEntity.IsValid?rawEntity.Pointer:UIntPtr.Zero)+", distance="+rawDistance+", point="+rawPoint);
            }
        }
        return success;
    }
    internal void SetSmokeControls(int mode)
    {
        if(_smokeControls.Count==0)
        {
            foreach(var placed in _blocks.Values)foreach(var entity in placed.Colliders)
                _smokeControls.Add(new SmokeControl {Entity=entity,Flags=entity.BodyFlag,Visible=entity.GetVisibilityExcludeParents()});
        }
        foreach(var control in _smokeControls)
        {
            control.Entity.SetBodyFlags(mode==1||mode==2?control.Flags|BodyFlags.BodyOwnerEntity:control.Flags);
            control.Entity.SetVisibilityExcludeParents(mode==2||mode==3?true:control.Visible);
            Mycraft.Demo.DemoLog.Diagnostic("TNT_SMOKE_AB_SET mode="+mode+", pointer="+control.Entity.Pointer+
                ", flags=0x"+((uint)control.Entity.BodyFlag).ToString("X")+", visible="+control.Entity.GetVisibilityExcludeParents()+
                ", physics="+control.Entity.GetPhysicsState());
        }
    }
    internal bool ProbeSmokeControls(string scenario)
    {
        // Diagnostic results never alter the original production-mask verdict.
        // The caller leaves each control active across multiple mission ticks.
        bool success=_smokeRays.Count>0;
        foreach(var entry in _smokeRays)
        {
            for(int i=0;i<entry.Value.Count;i++)
            {
                var ray=entry.Value[i];var collider=_blocks[entry.Key].Colliders[i];
                bool hit=_mission.Scene.RayCastForClosestEntityOrTerrain(ray.Start,ray.End,out var distance,out Vec3 point,out WeakGameEntity actual,.001f,BodyFlags.CommonFocusRayCastExcludeFlags);
                bool matched=hit&&collider==actual&&Math.Abs(distance-ray.Entry)<.03f;success&=matched;
                Mycraft.Demo.DemoLog.Diagnostic("TNT_SMOKE_AB_"+scenario+" cell="+entry.Key+", box="+i+", expected="+ray.Expected+
                    ", start="+ray.Start+", end="+ray.End+", hit="+hit+", actual="+(actual.IsValid?actual.Pointer:UIntPtr.Zero)+", distance="+distance+", point="+point+
                    ", flags=0x"+((uint)collider.BodyFlag).ToString("X")+", visible="+collider.GetVisibilityExcludeParents()+
                    ", physics="+collider.GetPhysicsState()+", matched="+matched);
            }
        }
        return success;
    }
    internal bool ProbeSmokeEmpty(int x,int y,int z)
    {
        if(_blocks.ContainsKey((x,y,z))||!_smokeRays.TryGetValue((x,y,z),out var rays)||rays.Count==0)return false;
        bool success=true;
        for(int i=0;i<rays.Count;i++)
        {
            var ray=rays[i];
            bool hit=_mission.Scene.RayCastForClosestEntityOrTerrain(ray.Start,ray.End,out var distance,out Vec3 point,out WeakGameEntity entity,.001f,BodyFlags.CommonFocusRayCastExcludeFlags);
            bool retainedBody=hit&&entity.IsValid&&entity.Pointer==ray.Expected;
            bool retainedVolume=hit&&distance>=ray.Entry-.03f&&distance<=ray.Exit+.03f;
            bool passed=!retainedBody&&!retainedVolume;success&=passed;
            Mycraft.Demo.DemoLog.Diagnostic("TNT_SMOKE_RAY_REMOVE cell="+(x,y,z)+", box="+i+", start="+ray.Start+", end="+ray.End+
                ", removed="+ray.Expected+", hit="+hit+", actual="+(entity.IsValid?entity.Pointer:UIntPtr.Zero)+", distance="+distance+
                ", point="+point+", passed="+passed);
        }
        return success;
    }
    public bool CanPlace(int x,int y,int z,string modelKey)
    {
        if(!_originSet||_disposed||!BlockGeometry.CanAdd(Count,_desired.ContainsKey((x,y,z))))return false;
        // Minecraft decides slab merging, replacement and attachment in owned cells.
        if(_desired.ContainsKey((x,y,z)))return true;
        var boxes=_models.TryGetValue(modelKey,out var model)?model.Data.Collision:new[]{new BlockBox(0,0,0,1,1,1)};
        return CanPlaceBoxes(x,y,z,boxes,true);
    }
    public bool CanPlaceTnt(int x,int y,int z)
    {
        if(!_originSet||_disposed||!BlockGeometry.CanAdd(Count,_desired.ContainsKey((x,y,z)))||!CellInsideBoundaries(x,z))return false;
        if(_desired.ContainsKey((x,y,z)))return true;
        return CanPlaceBoxes(x,y,z,new[]{new BlockBox(0,0,0,1,1,1)},false,true);
    }
    private bool CanPlaceBoxes(int x,int y,int z,BlockBox[] boxes,bool boundaries,bool allowTntSink=false)
    {
        if(boundaries&&!CellInsideBoundaries(x,z))return false;
        foreach(var local in boxes)
        {
            var box=BlockGeometry.Translate(local,x,y,z);float bottom=BlockGeometry.Height(y+local.MinY,_groundY,_anchorZ),top=BlockGeometry.Height(y+local.MaxY,_groundY,_anchorZ);
            if(!TryTerrainHeight(box.MinX,box.MinZ,box.MaxX,box.MaxZ,bottom,out var ground))return false;
            if(!(allowTntSink?BlockGeometry.ClearsTntTerrain(bottom,ground):BlockGeometry.ClearsTerrain(bottom,ground)))return false;
            foreach(var agent in _mission.Agents)
            {
                if(!agent.IsActive())continue;var p=agent.Position;
                var mcAgent=BlockGeometry.ToMinecraft(p.x,p.y,p.z,_groundY,_anchorZ,_offsetX,_offsetZ);
                if(BlockGeometry.OverlapsAgent(box,mcAgent.X,mcAgent.Y,mcAgent.Z,agent.IsMount))return false;
            }
            float epsilon=Math.Min(.01f,Math.Min(box.MaxX-box.MinX,Math.Min(box.MaxZ-box.MinZ,top-bottom))*.1f);
            bool hit=_mission.Scene.BoxCast(new Vec3(box.MinX-_offsetX+epsilon,box.MinZ-_offsetZ+epsilon,top-epsilon*2),
                new Vec3(box.MaxX-_offsetX-epsilon,box.MaxZ-_offsetZ-epsilon,top-epsilon),false,Vec3.Zero,-Vec3.Up,Math.Max(0,top-bottom-3*epsilon),
                out _,out _,out var entity,BodyFlags.CommonCollisionExcludeFlags|BodyFlags.BodyOwnerTerrain);
            if(hit&&(!entity.IsValid||!OwnsCollider(entity)))return false;
        }
        return true;
    }
    private bool OwnsCollider(WeakGameEntity entity)
    { foreach(var placed in _blocks.Values)foreach(var collider in placed.Colliders)if(collider==entity)return true;return false; }
    private bool OwnsCollider(GameEntity entity)=>OwnsCollider(entity.WeakEntity);
    private bool TryTerrainHeight(float minX,float minZ,float maxX,float maxZ,float nearHeight,out float highest)
    {
        // Callers pass actual MC cells; sample the corresponding native terrain.
        // Subtract before the small corner inset to preserve local float precision.
        minX-=_offsetX;maxX-=_offsetX;minZ-=_offsetZ;maxZ-=_offsetZ;
        highest=float.NegativeInfinity;float epsilon=Math.Min(.001f,Math.Min(maxX-minX,maxZ-minZ)*.1f);
        for(int i=0;i<5;i++)
        {
            float px=i==4?(minX+maxX)*.5f:i%2==0?minX+epsilon:maxX-epsilon;
            float py=i==4?(minZ+maxZ)*.5f:i/2==0?minZ+epsilon:maxZ-epsilon;
            float height=_mission.Scene.GetGroundHeightAtPosition(new Vec3(px,py,nearHeight+2),BodyFlags.CommonCollisionExcludeFlags|BodyFlags.BodyOwnerEntity|BodyFlags.BodyOwnerFlora);
            if(!BlockGeometry.Finite(height))return false;highest=Math.Max(highest,height);
        }
        return true;
    }
    private bool CellInsideBoundaries(int x,int z)
    {
        int nativeX=x-_offsetX,nativeZ=z-_offsetZ;
        return _mission.IsPositionInsideBoundaries(new Vec2(nativeX,nativeZ))&&_mission.IsPositionInsideBoundaries(new Vec2(nativeX+1,nativeZ))&&
            _mission.IsPositionInsideBoundaries(new Vec2(nativeX,nativeZ+1))&&_mission.IsPositionInsideBoundaries(new Vec2(nativeX+1,nativeZ+1));
    }
    private Vec3 World(float x,float y,float z)
    {
        var native=BlockGeometry.ToNative(x,y,z,_groundY,_anchorZ,_offsetX,_offsetZ);
        return new Vec3(native.X,native.Y,native.Z);
    }
    private static bool Finite(Vec3 p)=>BlockGeometry.Finite(p.x)&&BlockGeometry.Finite(p.y)&&BlockGeometry.Finite(p.z);
    private void Report(string status,Exception e){Status=status;MinecraftHeroState.LogFailure("block models",e);}
    private void RemoveCell((int x,int y,int z) cell)
    {
        if(!_blocks.TryGetValue(cell,out var placed))return;
        _blocks.Remove(cell);_liveShapes-=placed.Colliders.Count;RemovePlaced(placed);RetryBudgets();
    }
    private void RetryBudgets()
    {foreach(var entry in _failures.ToArray())if(entry.Value.IndexOf("budget",StringComparison.OrdinalIgnoreCase)>=0)_failures.Remove(entry.Key);}
    private static void RemoveEntity(GameEntity entity)
    {Mycraft.Demo.DemoLog.Diagnostic("BLOCK native: remove owned entity");entity.Remove(0);}
    private static void RemovePlaced(Placed placed)
    {
        foreach(var entity in placed.Colliders)try{RemoveEntity(entity);}catch(Exception e){MinecraftHeroState.LogFailure("block collider cleanup",e);}
        if(placed.Visual!=null)try{RemoveEntity(placed.Visual);}catch(Exception e){MinecraftHeroState.LogFailure("block visual cleanup",e);}
    }
    private void EvictUnused()
    {
        foreach(var entry in _models.ToArray())
        {
            if(_desired.Values.Any(x=>x.Model==entry.Key)||_blocks.Values.Any(x=>x.Model==entry.Key))continue;
            _models.Remove(entry.Key);_dataBytes-=entry.Value.Data.ByteLength;_textureBytes-=entry.Value.TextureBytes;_materialCount-=entry.Value.Materials.Count;
        }
    }
    private void Reset()
    {
        foreach(var task in _pending.Values)
            task.ContinueWith(done=>{var observed=done.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        _cancel.Cancel();_cancel.Dispose();_cancel=new CancellationTokenSource();
        foreach(var placed in _blocks.Values)RemovePlaced(placed);
        _blocks.Clear();_desired.Clear();_pending.Clear();_failures.Clear();_smokeRays.Clear();_smokeControls.Clear();
        // Retired model resources remain in the bounded Mission pool. Normal
        // native reference-counted release happens after the Mission is gone.
        _models.Clear();
        _dataBytes=_textureBytes=0;_materialCount=_liveShapes=0;_offsetX=_offsetZ=0;_originSet=false;
    }
    public void Dispose(){if(_disposed)return;_disposed=true;Reset();_cancel.Dispose();_session="";}
}
