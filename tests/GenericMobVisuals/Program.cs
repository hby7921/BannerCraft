using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mycraft.Visuals;

int assertions=0;
void Check(bool value,string label){assertions++;if(!value)throw new Exception(label);}
void Near(float value,float expected,string label)=>Check(float.IsFinite(value)&&Math.Abs(value-expected)<.00003f,label+": "+value+" vs "+expected);
string fixtures=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../tests/GenericMobVisuals/Fixtures"));
using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures,"native-geometry.json")));
foreach(var sample in doc.RootElement.EnumerateArray())
{
    string name=sample.GetProperty("name").GetString()!;
    using var assetFile=File.OpenRead(Path.Combine(fixtures,name+".asset"));var asset=GenericMobAsset.Read(assetFile);
    using var poseFile=File.OpenRead(Path.Combine(fixtures,name+".pose"));var pose=GenericMobPose.Read(poseFile);
    Check(asset.Parts.Length==pose.Frames.Length&&asset.Parts.Length==sample.GetProperty("partCount").GetInt32(),name+" native part count");
    var expected=sample.GetProperty("nativeVertices").EnumerateArray().ToArray();int offset=0;
    for(int i=0;i<asset.Parts.Length;i++)
    {
        var part=asset.Parts[i];Check(pose.Visible[i],name+" visible authored part");Check(pose.Colors[i]==uint.MaxValue,name+" native default tint");
        foreach(var vertex in part.Geometry.Vertices)
        {
            var point=pose.Frames[i].Point(vertex.X,vertex.Z,vertex.Y);var native=expected[offset++].EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            for(int j=0;j<3;j++)Near(point[j],native[j],name+" original ModelPart compiled world vertex");
            Near(vertex.U,native[6],name+" original native U");Near(vertex.V,native[7],name+" original native V");
            var host=pose.Frames[i].HostValues(1.25f);
            Near(host[0]*vertex.X+host[1]*vertex.Y+host[2]*vertex.Z+host[3],native[0]*1.25f,name+" host X basis");
            Near(host[4]*vertex.X+host[5]*vertex.Y+host[6]*vertex.Z+host[7],native[2]*1.25f,name+" host Y basis");
            Near(host[8]*vertex.X+host[9]*vertex.Y+host[10]*vertex.Z+host[11],native[1]*1.25f,name+" host Z basis");
        }
        var texture=asset.Textures[part.Material];var clipped=GenericMobAsset.Cutout(part.Geometry,texture);
        Check(clipped.Vertices.Count<65536&&clipped.Vertices.All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)),name+" original alpha geometry remains finite/bounded");
        for(int f=0;f<clipped.Vertices.Count;f+=4){float u=0,v=0;for(int q=0;q<4;q++){u+=clipped.Vertices[f+q].U/4;v+=clipped.Vertices[f+q].V/4;}int x=Math.Clamp((int)(u*texture.Width),0,texture.Width-1),y=Math.Clamp((int)(v*texture.Height),0,texture.Height-1);Check(texture.Pixels[(y*texture.Width+x)*4+3]>=128,name+" no black transparent native body planes part="+i+" face="+f+" uv="+u+","+v+" pixel="+x+","+y);}
    }
    Check(offset==sample.GetProperty("nativeVertexCount").GetInt32(),name+" every native vertex included");
    var presentation=GenericMobPresentation.Measure(asset,pose);
    Check(presentation.GeometryReady&&presentation.VisibleParts>0&&presentation.HasVisibleBounds,name+" original model has actual drawable native geometry");
    Check(presentation.AlphaZeroParts==0,name+" authored body tint remains opaque");
    if(name.StartsWith("wither_")&&!name.StartsWith("wither_skull_"))
        Check(presentation.Minimum[2]>0&&presentation.Maximum[2]>2.5f&&presentation.Maximum[2]<4,name+" actual Wither host geometry stays above original feet within native height envelope");
}
foreach(string key in new[]{"","../x",new string('a',31),new string('G',32),new string('/',32)})Check(!GenericMobPose.IsKey(key),"Reject unsafe native file keys");
Check(GenericMobPose.IsKey("0123456789abcdef0123456789abcdef"),"Accept immutable native appearance key");
byte[] valid=File.ReadAllBytes(Path.Combine(fixtures,"wither_0.pose"));
void Reject(byte[] data,string label){try{using var s=new MemoryStream(data);GenericMobPose.Read(s);throw new Exception("Accepted "+label);}catch(FormatException){assertions++;}catch(EndOfStreamException){assertions++;}}
Reject(valid[..10],"truncated pose");Reject(valid.Concat(new byte[]{0}).ToArray(),"trailing pose");
var invalid=(byte[])valid.Clone();BitConverter.GetBytes(float.NaN).CopyTo(invalid,56);Reject(invalid,"nonfinite native affine matrix");
invalid=(byte[])valid.Clone();BitConverter.GetBytes(257).CopyTo(invalid,44);Reject(invalid,"excessive native part count");
var rotated=new VoxelGeometry();
rotated.Vertices.Add(new VoxelGeometry.Vertex(0,0,0,0,0,1,1,1));rotated.Vertices.Add(new VoxelGeometry.Vertex(1,0,0,0,0,1,1,0));
rotated.Vertices.Add(new VoxelGeometry.Vertex(1,1,0,0,0,1,0,0));rotated.Vertices.Add(new VoxelGeometry.Vertex(0,1,0,0,0,1,0,1));
var cut=GenericMobAsset.Cutout(rotated,new GenericMobAsset.Texture {Width=2,Height=2,Pixels=new byte[]{255,255,255,255,0,0,0,0,0,0,0,0,0,0,0,0}});
Check(cut.Vertices.Count==4,"Rotated native item UV keeps only the original visible texel");
Check(cut.Vertices.All(v=>v.X>=.5f&&v.X<=1&&v.Y>=.5f&&v.Y<=1&&v.U>=0&&v.U<=.5f&&v.V>=0&&v.V<=.5f),"Rotated native UV clipping follows actual geometric basis");
using(var assetStream=File.OpenRead(Path.Combine(fixtures,"wither_0.asset")))using(var poseStream=File.OpenRead(Path.Combine(fixtures,"wither_0.pose")))
{
    var nativeAsset=GenericMobAsset.Read(assetStream);var nativePose=GenericMobPose.Read(poseStream);
    Array.Fill(nativePose.Visible,false);var hidden=GenericMobPresentation.Measure(nativeAsset,nativePose);
    Check(hidden.GeometryReady&&hidden.VisibleParts==0&&!hidden.HasVisibleBounds,"Native hidden pose preserves original geometry readiness without claiming visible pixels");
    Array.Fill(nativePose.Visible,true);Array.Fill(nativePose.Colors,0x00ffffffu);var alpha=GenericMobPresentation.Measure(nativeAsset,nativePose);
    Check(alpha.VisibleParts>0&&alpha.AlphaZeroParts==alpha.VisibleParts,"Zero alpha body tint is reported separately from native part visibility");
    foreach(var texture in nativeAsset.Textures)for(int p=3;p<texture.Pixels.Length;p+=4)texture.Pixels[p]=0;
    var empty=GenericMobPresentation.Measure(nativeAsset,nativePose);
    Check(!empty.GeometryReady&&empty.RenderableParts==0&&empty.VisibleParts==0,"An entirely transparent original asset cannot be reported as model ready");
}
Console.WriteLine("PASS "+assertions+" native generic creature geometry/texture/affine/budget assertions");
