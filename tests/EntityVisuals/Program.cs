using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using Mycraft.Visuals;

int assertions=0;
void Check(bool valid,string label){assertions++;if(!valid)throw new Exception(label);}
void Near(float actual,float expected,string label)=>Check(float.IsFinite(actual)&&Math.Abs(actual-expected)<.000003f,label+": "+actual+" vs "+expected);
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
string assets=Path.Combine(root,"src/Mycraft/_Module/Visuals/entities");
OriginalEntityModel Model(string name){using var stream=File.OpenRead(Path.Combine(assets,name+".model"));return OriginalEntityModel.Read(stream);}
using var manifest=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(assets,"manifest.json")));
foreach(var texture in manifest.RootElement.GetProperty("textures").EnumerateArray())
{
    var data=File.ReadAllBytes(Path.Combine(assets,texture.GetProperty("name").GetString()+".texture"));
    Check(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()==texture.GetProperty("sha256").GetString(),"Actual original texture hash");
    int width=BitConverter.ToInt32(data,0),height=BitConverter.ToInt32(data,4);
    Check(width==texture.GetProperty("width").GetInt32()&&height==texture.GetProperty("height").GetInt32()&&data.Length==width*height*4+8,"Original texture rectangular dimensions");
}
Check(manifest.RootElement.GetProperty("boatKinds").EnumerateObject().Count()==22,"Original renderer kinds include poplar and bamboo raft");
var wither=Model("wither_skeleton");
Check(wither.Parts.Length==8,"Skeleton has actual six main cubes and original hat child");
Check(wither.Parts[wither.Index("/right_arm")].Geometry.Vertices.Count==24,"Thin skeleton arm original six cube faces");
var arm=wither.Parts[wither.Index("/right_arm")].Geometry.Vertices;
Near(arm.Max(v=>v.X)-arm.Min(v=>v.X),2f/16,"Skeleton arm width is two pixels");
Near(arm.Max(v=>v.Z)-arm.Min(v=>v.Z),12f/16,"Skeleton arm height is twelve pixels");
using(var oracle=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"tests/EntityVisuals/Fixtures/skeleton-pose-oracle.json"))))
foreach(var sample in oracle.RootElement.EnumerateArray())
{
    float[] input=sample.GetProperty("input").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
    var poses=OriginalEntityPose.WitherSkeleton(wither,input[1],input[0],input[2],input[3],input[4],input[6]>0,(int)input[5]);
    var poseFrames=wither.Frames(poses,OriginalEntityPose.WitherRoot(37,0));
    foreach(var part in sample.GetProperty("parts").EnumerateArray())
    {
        var actual=poses[wither.Index(part.GetProperty("path").GetString()!)];
        float[] values={actual.X,actual.Y,actual.Z,actual.RotX,actual.RotY,actual.RotZ,actual.ScaleX,actual.ScaleY,actual.ScaleZ};
        var expected=part.GetProperty("pose").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        for(int i=0;i<9;i++)Near(values[i],expected[i],"Original native skeleton setupAnim part "+part.GetProperty("path")+" field "+i);
    }
    foreach(var frame in sample.GetProperty("frames").EnumerateObject())
    {var actual=poseFrames[wither.Index(frame.Name)].Values;var expected=frame.Value.EnumerateArray().Select(v=>v.GetSingle()).ToArray();for(int i=0;i<12;i++)Near(actual[i],expected[i],"Original skeleton native PoseStack matrix "+frame.Name+" field "+i);}
}
var wire=File.ReadAllBytes(Path.Combine(assets,"wither_skeleton.texture"));
int tw=BitConverter.ToInt32(wire,0),th=BitConverter.ToInt32(wire,4);var rgba=wire[8..];
int visible=0;
foreach(var part in wither.Parts)
{
    var cutout=EntityModelCutout.Prepare(part.Geometry,rgba,tw,th);visible+=cutout.Vertices.Count;
    for(int i=0;i<cutout.Vertices.Count;i+=4)
    {
        float u=cutout.Vertices.Skip(i).Take(4).Average(v=>v.U),v=cutout.Vertices.Skip(i).Take(4).Average(v=>v.V);
        int px=Math.Clamp((int)(u*tw),0,tw-1),py=Math.Clamp((int)(v*th),0,th-1);
        Check(rgba[(py*tw+px)*4+3]>=128,"No transparent skeleton black-plane face");
    }
}
Check(visible>0&&visible<5000,"Visible skeleton keeps authored surfaces in bounded mesh");
var rest=wither.Frames(wither.RestPoses(),OriginalEntityPose.WitherRoot(0,0));
var world=new List<float[]>();
for(int i=0;i<wither.Parts.Length;i++)if(wither.Parts[i].Path!="/head/hat")foreach(var vertex in wither.Parts[i].Geometry.Vertices)world.Add(rest[i].Point(vertex.X,vertex.Z,vertex.Y));
Near(world.Max(v=>v[1])-world.Min(v=>v[1]),2.4f,"Original 1.2-scaled wither skeleton model height");
Near(world.Min(v=>v[1]),.0012f,"Original renderer feet above zero by authored epsilon");
foreach(string name in new[]{"boat","chest_boat","raft","chest_raft"})
{
    var model=Model(name);Check(model.Parts.All(p=>p.Geometry.Vertices.Count%24==0),"Native baked cuboids");
    Check(model.Parts.Any(p=>p.Path=="/left_paddle")&&model.Parts.Any(p=>p.Path=="/right_paddle"),"Both real paddle parts");
    Check(model.Parts.Any(p=>p.Path=="/chest_bottom")==name.StartsWith("chest"),"Chest only present on genuine chest variants");
    var poses=OriginalEntityPose.Boat(model,0,0);Near(poses[model.Index("/left_paddle")].RotX,-(float)Math.PI*5/24,"Native resting paddle pitch");
    var frames=model.Frames(poses,OriginalEntityPose.BoatRoot(0,0,0,0));
    Check(frames.All(f=>f.Values.All(float.IsFinite)),"Native boat transform finite");
}
using(var oracle=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"tests/EntityVisuals/Fixtures/boat-pose-oracle.json"))))
foreach(var sample in oracle.RootElement.EnumerateArray())
{
    var model=Model(sample.GetProperty("model").GetString()!);float[] input=sample.GetProperty("input").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
    var poses=OriginalEntityPose.Boat(model,input[1],input[2]);
    var frames=model.Frames(poses,OriginalEntityPose.BoatRoot(input[0],input[3],input[4],input[5],(int)input[6],input[7]>0));
    foreach(var part in sample.GetProperty("parts").EnumerateArray())
    {
        var actual=poses[model.Index(part.GetProperty("path").GetString()!)];float[] values={actual.X,actual.Y,actual.Z,actual.RotX,actual.RotY,actual.RotZ,actual.ScaleX,actual.ScaleY,actual.ScaleZ};
        var expected=part.GetProperty("pose").EnumerateArray().Select(v=>v.GetSingle()).ToArray();for(int i=0;i<9;i++)Near(values[i],expected[i],"Original boat native setupAnim "+part.GetProperty("path")+" field "+i);
    }
    foreach(var frame in sample.GetProperty("frames").EnumerateObject())
    {var actual=frames[model.Index(frame.Name)].Values;var expected=frame.Value.EnumerateArray().Select(v=>v.GetSingle()).ToArray();for(int i=0;i<12;i++)Near(actual[i],expected[i],"Original boat native PoseStack matrix "+frame.Name+" field "+i);}
}
var map=EntityMatrix.Translation(2,3,4)*EntityMatrix.RotateY(.5f)*EntityMatrix.Scale(1.2f,1.2f,1.2f);
var native=map.HostValues(2);var expectedPoint=map.Point(1,2,3);
Near(native[0]*1+native[1]*3+native[2]*2+native[3],expectedPoint[0]*2,"Host X basis matches original matrix");
Near(native[4]*1+native[5]*3+native[6]*2+native[7],expectedPoint[2]*2,"Host Y basis matches original matrix");
Near(native[8]*1+native[9]*3+native[10]*2+native[11],expectedPoint[1]*2,"Host Z basis matches original matrix");
Near(OriginalEntityPose.RelativeHeadYaw(-179,179),2,"Head angles wrap at world yaw boundary");
Console.WriteLine("PASS "+assertions+" original entity model/texture/pose assertions");
