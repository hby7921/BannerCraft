using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Mycraft.Bridge;
using Mycraft.Building;
using Mycraft.Demo;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path=System.IO.Path;

namespace Mycraft.Visuals;

/// <summary>Displays the actual tracked MC entity renderer, never a substitute model.</summary>
public sealed class GenericMobVisual:IDisposable
{
    private sealed class MissionAssets
    {
        public readonly Dictionary<string,GenericMobAsset> Assets=new Dictionary<string,GenericMobAsset>();
        public readonly Dictionary<string,(Material Normal,Material Hurt)> Textures=new Dictionary<string,(Material,Material)>();
        public readonly HashSet<string> DiagnosticAssets=new HashSet<string>();
        public long DiagnosticBytes;
        public int DiagnosticRecords;
    }
    private sealed class RenderPart
    {
        public GameEntity? Entity; public Mesh? Mesh; public Material Normal=null!,Hurt=null!;
        public float[] Min=new float[3],Max=new float[3];
    }
    private static readonly ConditionalWeakTable<Mission,MissionAssets> Cache=new ConditionalWeakTable<Mission,MissionAssets>();
    private readonly Mission _mission;
    private readonly string _directory,_key;
    private readonly List<RenderPart> _parts=new List<RenderPart>();
    private readonly Dictionary<string,List<RenderPart>> _renderSets=new Dictionary<string,List<RenderPart>>();
    private GenericMobPose? _pose;
    private string _appearance="";
    private DateTime _nextRead;
    private DateTime _poseObservedAt,_nextDiagnostic;
    private string _lastReported="",_lastError="",_attemptAppearance="",_assetBytesAppearance="";
    private byte[]? _poseBytes,_assetBytes;
    private long _poseFileLength,_assetFileLength;
    private int _renderableParts,_visibleParts,_alphaZeroParts,_diagnosticRecords;
    private Vec3 _lastOrigin;
    private float _lastScale=1;
    private bool _disposed,_hurt,_reportedReady;
    // A retained list entry can represent an entirely transparent, meshless part.
    // Readiness proves actual native geometry, not its position in the user's camera.
    public bool Ready=>_pose!=null && _renderableParts>0;
    public string DiagnosticStatus {get;private set;}="waiting_pose";
    public string LastReadError=>_lastError;
    public int RenderablePartCount=>_renderableParts;
    public int VisiblePartCount=>_visibleParts;
    public long PoseSequence=>_pose?.Sequence??0;
    public GenericMobVisual(Mission mission,string sid,string visualKey)
    {
        if(!GenericMobPose.IsKey(sid)||!GenericMobPose.IsKey(visualKey))throw new ArgumentException("Invalid native entity visual identity");
        _mission=mission;_key=visualKey;_directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Mycraft","entity-models",sid);
    }
    public bool Update(Vec3 origin,float scale,bool hurt=false)
    {
        if(_disposed)return false;
        _lastOrigin=origin;_lastScale=scale;
        if(DateTime.UtcNow>=_nextRead)
        {
            _nextRead=DateTime.UtcNow.AddMilliseconds(45);
            try
            {
                string path=Path.Combine(_directory,_key+".pose");
                if(File.Exists(path))
                {
                    _poseBytes=ReadBytes(path,14400,out _poseFileLength);
                    GenericMobPose pose;using(var stream=new MemoryStream(_poseBytes,false))pose=GenericMobPose.Read(stream);
                    if(_pose==null||pose.Sequence>_pose.Sequence)
                    {
                        _attemptAppearance=pose.Appearance;
                        if(pose.Appearance!=_appearance)Build(pose.Appearance,pose.Frames.Length);
                        if(pose.Frames.Length!=_parts.Count)throw new FormatException("Native entity pose part mismatch");
                        _pose=pose;_poseObservedAt=DateTime.UtcNow;_lastError="";
                    }
                }
            }
            catch(IOException error){ReadFailed(error);}
            catch(FormatException error){ReadFailed(error);}
            catch(InvalidOperationException error){ReadFailed(error);_nextRead=DateTime.UtcNow.AddSeconds(2);}
        }
        if(_pose==null||!Ready){DiagnosticStatus=_lastError.Length>0?"read_error":_pose==null?"waiting_pose":"empty_geometry";ReportDiagnostic();return false;}
        _visibleParts=0;_alphaZeroParts=0;
        for(int i=0;i<_parts.Count;i++)
        {
            var p=_parts[i];if(p.Entity==null||p.Mesh==null)continue;if(_hurt!=hurt)p.Mesh.SetMaterial(hurt?p.Hurt:p.Normal);
            p.Mesh.Color=_pose.Colors[i];p.Entity.SetGlobalFrame(NativeEntityVisual.Frame(_pose.Frames[i],origin,scale));p.Entity.SetVisibilityExcludeParents(_pose.Visible[i]);
            if(_pose.Visible[i]){_visibleParts++;if((_pose.Colors[i]>>24)==0)_alphaZeroParts++;}
        }
        _hurt=hurt;
        DiagnosticStatus=_lastError.Length>0?"read_error_using_last_pose":_visibleParts==0?"native_pose_hidden":
            _alphaZeroParts==_visibleParts?"zero_alpha_tint":DateTime.UtcNow-_poseObservedAt>TimeSpan.FromSeconds(2)?"stale_pose":"ready";
        ReportDiagnostic();return true;
    }
    private void Build(string appearance,int count)
    {
        _assetBytes=null;_assetBytesAppearance="";_assetFileLength=0;
        if(_renderSets.TryGetValue(appearance,out var ready))
        {
            if(ready.Count!=count)throw new FormatException("Native entity pose part mismatch");
            foreach(var old in _parts)old.Entity?.SetVisibilityExcludeParents(false);
            _parts.Clear();_parts.AddRange(ready);_appearance=appearance;_hurt=false;CountMeshes();
            foreach(var p in _parts)p.Mesh?.SetMaterial(p.Normal);
            return;
        }
        var cache=Cache.GetValue(_mission,_=>new MissionAssets());
        if(!cache.Assets.TryGetValue(appearance,out var asset))
        {
            _assetBytes=ReadBytes(Path.Combine(_directory,appearance+".asset"),4*1024*1024,out _assetFileLength);
            _assetBytesAppearance=appearance;
            using(var stream=new MemoryStream(_assetBytes,false))asset=GenericMobAsset.Read(stream);
            if(cache.Assets.Count>=256)throw new FormatException("Native entity appearance budget reached");cache.Assets.Add(appearance,asset);
        }
        if(asset.Parts.Length!=count)throw new FormatException("Native entity asset/pose part mismatch");
        var geometries=new VoxelGeometry[asset.Parts.Length];int totalVertices=0;long nativeBytes=asset.Parts.Length*3072L;
        for(int i=0;i<asset.Parts.Length;i++)
        {
            var source=asset.Parts[i];geometries[i]=GenericMobAsset.Cutout(source.Geometry,asset.Textures[source.Material]);
            totalVertices+=geometries[i].Vertices.Count;if(totalVertices>262144)throw new FormatException("Native entity visible geometry budget exceeded");
            nativeBytes+=geometries[i].Vertices.Count*48L;
        }
        // Preflight the whole appearance before creating any retained native wrappers.
        foreach(var t in asset.Textures){int m=Math.Max(t.Width,t.Height)>128?2:4;nativeBytes+=2L*t.Width*t.Height*m*m*4+4096;}
        NativeSceneResources.For(_mission).Check(nativeBytes);
        // Build the new appearance first, so a delayed atomic file never hides a ready one.
        var created=new List<RenderPart>();
        try
        {
            for(int i=0;i<asset.Parts.Length;i++)
            {
                var source=asset.Parts[i];
                var pixels=asset.Textures[source.Material];var materials=Materials(cache,pixels);
                var geometry=geometries[i];
                if(geometry.Indices.Count==0){created.Add(new RenderPart {Normal=materials.Normal,Hurt=materials.Hurt});continue;}
                var pool=NativeSceneResources.For(_mission);pool.Check(geometry.Vertices.Count*48L+2048);
                var mesh=MinecraftModelAssets.Mesh(geometry,materials.Normal);pool.Hold(mesh,geometry.Vertices.Count*48L);
                var entity=GameEntity.CreateEmpty(_mission.Scene,false,false,false);pool.Hold(entity,1024);entity.SetVisibilityExcludeParents(false);entity.AddMesh(mesh);
                var part=new RenderPart {Mesh=mesh,Entity=entity,Normal=materials.Normal,Hurt=materials.Hurt};
                for(int axis=0;axis<3;axis++){part.Min[axis]=float.MaxValue;part.Max[axis]=-float.MaxValue;}
                foreach(var vertex in geometry.Vertices)
                {
                    part.Min[0]=Math.Min(part.Min[0],vertex.X);part.Min[1]=Math.Min(part.Min[1],vertex.Y);part.Min[2]=Math.Min(part.Min[2],vertex.Z);
                    part.Max[0]=Math.Max(part.Max[0],vertex.X);part.Max[1]=Math.Max(part.Max[1],vertex.Y);part.Max[2]=Math.Max(part.Max[2],vertex.Z);
                }
                created.Add(part);
            }
        }
        catch{foreach(var p in created){p.Entity?.SetVisibilityExcludeParents(false);p.Entity?.Remove(0);}throw;}
        foreach(var old in _parts)old.Entity?.SetVisibilityExcludeParents(false);
        _parts.Clear();_parts.AddRange(created);_renderSets.Add(appearance,created);_appearance=appearance;_hurt=false;CountMeshes();
    }
    private void CountMeshes(){_renderableParts=0;foreach(var part in _parts)if(part.Mesh!=null&&part.Entity!=null)_renderableParts++;}
    private void ReadFailed(Exception error){_lastError=error.GetType().Name+": "+error.Message;if(_lastError.Length>512)_lastError=_lastError.Substring(0,512);}
    private static byte[] ReadBytes(string path,int maximum,out long length)
    {
        using(var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            length=stream.Length;if(length>maximum)throw new FormatException("Native entity snapshot exceeds byte budget: "+length+" > "+maximum);
            var bytes=new byte[(int)length];int offset=0;
            while(offset<bytes.Length){int count=stream.Read(bytes,offset,bytes.Length-offset);if(count==0)throw new EndOfStreamException("Incomplete native entity snapshot");offset+=count;}
            return bytes;
        }
    }
    public Dictionary<string,object> DiagnosticDetails()
    {
        var details=new Dictionary<string,object>{{"visualKey",_key},{"status",DiagnosticStatus},{"ready",Ready},{"error",_lastError},
            {"appearance",_appearance},{"attemptedAppearance",_attemptAppearance},{"sequence",PoseSequence},{"parts",_parts.Count},{"renderableParts",_renderableParts},{"visibleParts",_visibleParts},{"alphaZeroParts",_alphaZeroParts},
            {"originX",(double)_lastOrigin.x},{"originY",(double)_lastOrigin.y},{"originZ",(double)_lastOrigin.z},{"scale",(double)_lastScale},
            {"posePath",Path.Combine(_directory,_key+".pose")},{"poseFileLength",_poseFileLength},{"assetFileLength",_assetFileLength}};
        if(_pose==null)return details;
        var min=new[]{float.MaxValue,float.MaxValue,float.MaxValue};var max=new[]{-float.MaxValue,-float.MaxValue,-float.MaxValue};bool bounds=false;
        for(int i=0;i<_parts.Count;i++)
        {
            var part=_parts[i];if(part.Entity==null||!_pose.Visible[i])continue;var m=_pose.Frames[i].HostValues(_lastScale);
            for(int corner=0;corner<8;corner++)
            {
                float x=(corner&1)==0?part.Min[0]:part.Max[0],y=(corner&2)==0?part.Min[1]:part.Max[1],z=(corner&4)==0?part.Min[2]:part.Max[2];
                float px=_lastOrigin.x+m[0]*x+m[1]*y+m[2]*z+m[3],py=_lastOrigin.y+m[4]*x+m[5]*y+m[6]*z+m[7],pz=_lastOrigin.z+m[8]*x+m[9]*y+m[10]*z+m[11];
                min[0]=Math.Min(min[0],px);min[1]=Math.Min(min[1],py);min[2]=Math.Min(min[2],pz);max[0]=Math.Max(max[0],px);max[1]=Math.Max(max[1],py);max[2]=Math.Max(max[2],pz);bounds=true;
            }
        }
        details["hasVisibleBounds"]=bounds;
        if(bounds)for(int axis=0;axis<3;axis++){details["min"+axis]=(double)min[axis];details["max"+axis]=(double)max[axis];}
        return details;
    }
    private void ReportDiagnostic()
    {
        if(_lastReported==DiagnosticStatus||DateTime.UtcNow<_nextDiagnostic&&!(Ready&&!_reportedReady))return;
        if(_diagnosticRecords>=4)return;
        var cache=Cache.GetValue(_mission,_=>new MissionAssets());if(cache.DiagnosticRecords>=128)return;
        _lastReported=DiagnosticStatus;_nextDiagnostic=DateTime.UtcNow.AddSeconds(2);_diagnosticRecords++;cache.DiagnosticRecords++;
        if(Ready)_reportedReady=true;
        var details=DiagnosticDetails();details["capturedAtUtc"]=DateTime.UtcNow.ToString("O");
        DemoLog.Info("MC_ENTITY_VISUAL_STATUS: "+BridgeJson.Write(details));
        // Preserve immutable, bounded raw evidence before native client disconnect
        // cleanup deletes its session files. Disk writes run off the mission tick.
        byte[]? asset=null,pose=_poseBytes;string appearance=_attemptAppearance.Length>0?_attemptAppearance:_appearance;
        if(appearance.Length>0&&!cache.DiagnosticAssets.Contains(appearance))
        {
            try
            {
                asset=_assetBytes!=null&&_assetBytesAppearance==appearance?_assetBytes:ReadBytes(Path.Combine(_directory,appearance+".asset"),4*1024*1024,out _assetFileLength);
                if(cache.DiagnosticBytes+asset.Length>8*1024*1024)asset=null;
                else{cache.DiagnosticAssets.Add(appearance);cache.DiagnosticBytes+=asset.Length;}
            }
            catch(IOException error){details["captureAssetError"]=error.GetType().Name+": "+error.Message;}
            catch(FormatException error){details["captureAssetError"]=error.Message;}
        }
        if(pose!=null)
        {
            if(cache.DiagnosticBytes+pose.Length>8*1024*1024)pose=null;else cache.DiagnosticBytes+=pose.Length;
        }
        _assetBytes=null;_assetBytesAppearance="";
        details["captureAssetBytes"]=asset?.Length??0;details["capturePoseBytes"]=pose?.Length??0;
        string json=BridgeJson.Write(details),record=_key+"-"+_diagnosticRecords;
        string target=Path.Combine(DemoConfig.ModuleFolder,"Logs","entity-visual-diagnostics",Path.GetFileName(_directory));
        ThreadPool.QueueUserWorkItem(_=>
        {
            try
            {
                Directory.CreateDirectory(target);
                if(asset!=null)File.WriteAllBytes(Path.Combine(target,appearance+".asset"),asset);
                if(pose!=null)File.WriteAllBytes(Path.Combine(target,record+".pose"),pose);
                File.WriteAllText(Path.Combine(target,record+".json"),json);
            }
            catch(Exception error){DemoLog.Info("MC_ENTITY_VISUAL_CAPTURE_FAILED: key="+_key+", "+error.GetType().Name+": "+error.Message);}
        });
    }
    private (Material Normal,Material Hurt) Materials(MissionAssets cache,GenericMobAsset.Texture data)
    {
        string key;using(var hash=SHA256.Create())key=Convert.ToBase64String(hash.ComputeHash(data.Pixels))+":"+data.Width+":"+data.Height;
        if(cache.Textures.TryGetValue(key,out var saved))return saved;
        var pair=(TextureMaterial(data,false),TextureMaterial(data,true));cache.Textures.Add(key,pair);return pair;
    }
    private Material TextureMaterial(GenericMobAsset.Texture data,bool hurt)
    {
        int magnify=Math.Max(data.Width,data.Height)>128?2:4,width=data.Width*magnify,height=data.Height*magnify;
        var rgba=new byte[width*height*4];for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int src=((y/magnify)*data.Width+x/magnify)*4,dst=(y*width+x)*4;
            for(int c=0;c<4;c++){byte value=data.Pixels[src+c];if(hurt&&c<3)value=(byte)((value*178+(c==0?255:0)*77)/255);rgba[dst+c]=value;}
        }
        var pool=NativeSceneResources.For(_mission);pool.Check(rgba.Length+2048);
        var texture=Texture.CreateFromByteArray(rgba,width,height);texture.SetTextureAsAlwaysValid();pool.Hold(texture,rgba.Length);
        var material=Material.GetDefaultMaterial().CreateCopy();material.SetEnableSkinning(false);material.UsingDynamicLight=true;material.SetTexture(Material.MBTextureType.DiffuseMap,texture);pool.Hold(material,1024);return material;
    }
    private void ClearParts(){foreach(var set in _renderSets.Values)foreach(var p in set){p.Entity?.SetVisibilityExcludeParents(false);p.Entity?.Remove(0);}_parts.Clear();_renderSets.Clear();}
    public void Dispose(){if(_disposed)return;_disposed=true;ClearParts();_pose=null;_poseBytes=null;_assetBytes=null;_renderableParts=0;_visibleParts=0;}
}
