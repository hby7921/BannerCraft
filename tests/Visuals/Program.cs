using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mycraft.Visuals;
using Mycraft.Demo;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Validate(VoxelGeometry geometry)
{
    foreach (var v in geometry.Vertices)
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || v.U < 0 || v.U > 1 || v.V < 0 || v.V > 1)
            throw new Exception("Invalid vertex/UV");
    for (var i=0; i<geometry.Indices.Count; i+=3)
    {
        var a=geometry.Vertices[geometry.Indices[i]];
        var b=geometry.Vertices[geometry.Indices[i+1]];
        var c=geometry.Vertices[geometry.Indices[i+2]];
        var ux=b.X-a.X; var uy=b.Y-a.Y; var uz=b.Z-a.Z;
        var vx=c.X-a.X; var vy=c.Y-a.Y; var vz=c.Z-a.Z;
        var nx=uy*vz-uz*vy; var ny=uz*vx-ux*vz; var nz=ux*vy-uy*vx;
        if (nx*a.Nx+ny*a.Ny+nz*a.Nz <= 0) throw new Exception("Inverted or zero-area triangle");
    }
}
var head=VoxelGeometry.SkinBox(.48f,.48f,.48f,0,0,8,8,8);
Validate(head);
Check(head.Vertices.Count==24 && head.Indices.Count==36,"six-faced head with outward triangles");
var torso=VoxelGeometry.SkinBox(.48f,.26f,.7f,16,16,8,4,12);
Validate(torso);
Check(torso.Vertices[8].U == 20f/64 && torso.Vertices[10].V == 20f/64,"torso front uses modern skin region");
foreach(var coordinates in new[]{(40,16),(32,48),(0,16),(16,48)})
{
    Validate(VoxelGeometry.SkinBox(.24f,.24f,.35f,coordinates.Item1,coordinates.Item2,4,4,12,0,6));
    Validate(VoxelGeometry.SkinBox(.24f,.24f,.35f,coordinates.Item1,coordinates.Item2,4,4,12,6,6));
}
Check(true,"eight limb segments have valid UV and winding");
var empty=new byte[1024];
var block=VoxelGeometry.HeldBlock();
Validate(block);
Check(block.Vertices.Count==24 && block.Indices.Count==36,"held block is a six-faced cube, not a flat brick icon");
var maxBlockExtent=0f;
foreach(var v in block.Vertices)maxBlockExtent=Math.Max(maxBlockExtent,Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z))));
Check(maxBlockExtent<=.321f,"held cube uses hand-scale dimensions");
var tntAtlas=File.ReadAllBytes("src/Mycraft/_Module/Visuals/blocks/tnt.atlas.rgba");
var tnt=HeldBlockModel.BottomTop(tntAtlas);
var tntGeometry=tnt.Geometry();
Validate(tntGeometry);
Check(tnt.TextureSize==64&&tnt.Pixels.Length==16384&&tntGeometry.Vertices.Count==24&&tntGeometry.Indices.Count==36,
    "TNT held asset selects its 64x64 atlas and full six-face model");
Check(tntGeometry.Vertices.Max(v=>v.Z)<=.321f&&tntGeometry.Vertices.Min(v=>v.Z)>=0&&
    Math.Abs(tntGeometry.Vertices.Max(v=>v.X)-.16f)<.00001,
    "TNT uses held-block scale and hand origin instead of flat-item geometry");
var tntSources=new[]{"side","top","bottom"}.Select(face=>File.ReadAllBytes("src/Mycraft/_Module/Visuals/blocks/tnt_"+face+".rgba")).ToArray();
for(int tile=0;tile<3;tile++)
{
    for(int y=0;y<18;y++)for(int x=0;x<18;x++)for(int channel=0;channel<4;channel++)
    {
        int source=(Math.Clamp(y-1,0,15)*16+Math.Clamp(x-1,0,15))*4+channel;
        int target=(y*64+tile*18+x)*4+channel;
        if(tntAtlas[target]!=tntSources[tile][source])throw new Exception("TNT atlas changed original face pixels or edge padding");
    }
}
Check(true,"TNT atlas preserves all original side/top/bottom pixels and pads every tile edge");
Check(!tntSources[0].SequenceEqual(tntSources[1])&&!tntSources[1].SequenceEqual(tntSources[2])&&!tntSources[0].SequenceEqual(tntSources[2]),
    "TNT has three distinct native textures instead of repeating the side on every face");
for(int face=0;face<6;face++)
{
    var vertices=tntGeometry.Vertices.Skip(face*4).Take(4).ToArray();
    int left=face<4?1:face==4?19:37;
    Check(Math.Abs(vertices.Min(v=>v.U)*64-left)<.00001&&Math.Abs(vertices.Max(v=>v.U)*64-left-16)<.00001&&
        vertices.Min(v=>v.V)==1f/64&&vertices.Max(v=>v.V)==17f/64,
        "TNT face "+face+" maps only to its correct 16x16 atlas tile");
}
Check(tntGeometry.Vertices[8].U>tntGeometry.Vertices[9].U&&tntGeometry.Vertices[16].V<tntGeometry.Vertices[18].V,
    "TNT side lettering and top face retain Minecraft UV orientation after axis conversion");
try { HeldBlockModel.BottomTop(tntSources[0]);Check(false,"TNT rejects a flat side texture as its atlas"); }
catch(ArgumentException) { Check(true,"TNT rejects a flat side texture as its atlas"); }
var shield=VoxelGeometry.Shield();
Validate(shield);
Check(shield.Vertices.Count==48 && shield.Indices.Count==72,"shield has all six plate faces and all six handle faces");
Check(Math.Abs(shield.Vertices.Min(v=>v.X)+.30f)<.00001 && Math.Abs(shield.Vertices.Max(v=>v.X)-.30f)<.00001 &&
    Math.Abs(shield.Vertices.Min(v=>v.Z)+.55f)<.00001 && Math.Abs(shield.Vertices.Max(v=>v.Z)-.55f)<.00001,
    "shield spans both sides of its grip with original 12:22 plate proportions");
Check(Math.Abs(shield.Vertices.Min(v=>v.Y)+.25f)<.00001 && Math.Abs(shield.Vertices.Max(v=>v.Y)-.10f)<.00001,
    "shield preserves original depth, plate thickness and protruding handle");
Check(shield.Vertices[8].U==13f/64 && shield.Vertices[8].V==23f/64 &&
    shield.Vertices[10].U==1f/64 && shield.Vertices[10].V==1f/64,
    "full shield front UV includes left/right metal borders with original orientation");
Check(shield.Vertices[12].U==26f/64 && shield.Vertices[14].U==14f/64,
    "shield back uses its own full 12-pixel-wide UV region");
var shieldAtlas=File.ReadAllBytes("src/Mycraft/_Module/Visuals/shield.rgba");
Check(shieldAtlas.Length==16384,"held shield uses original 64x64 entity atlas");
for(var faceIndex=0;faceIndex<shield.Vertices.Count;faceIndex+=4)
{
    var face=shield.Vertices.Skip(faceIndex).Take(4).ToArray();
    var u0=(int)Math.Round(face.Min(v=>v.U)*64); var u1=(int)Math.Round(face.Max(v=>v.U)*64);
    var v0=(int)Math.Round(face.Min(v=>v.V)*64); var v1=(int)Math.Round(face.Max(v=>v.V)*64);
    for(var y=v0;y<v1;y++)for(var x=u0;x<u1;x++)
        if(shieldAtlas[(y*64+x)*4+3]!=255)throw new Exception("Shield UV samples transparent atlas outside a face");
}
Check(true,"every plate and handle UV texel is inside the original opaque face regions");
var spriteData=XDocument.Load("src/Mycraft/_Module/GUI/MycraftSpriteData.xml");
foreach(var name in new[]{"stone","dirt","cobblestone","oak_planks","bricks","tnt","shield"})
{
    var sprite=spriteData.Descendants("SpritePart").Single(p=>(string?)p.Element("Name")=="mycraft_item_"+name);
    int Field(string key)=>int.Parse(sprite.Element(key)!.Value);
    Check(Field("Width")==48 && Field("Height")==48,name+" HUD model keeps 16px slot dimensions at 3x scale");
    var atlas=File.ReadAllBytes("src/Mycraft/_Module/GUI/SpriteSheets/MycraftHUD/MycraftHUD_"+Field("SheetID")+".rgba");
    int Alpha(int x,int y)=>atlas[((Field("SheetY")+y)*2048+Field("SheetX")+x)*4+3];
    var solidPixels=0;
    for(var y=0;y<48;y++)for(var x=0;x<48;x++)if(Alpha(x,y)>0)solidPixels++;
    Check(Alpha(0,0)==0 && Alpha(47,0)==0 && Alpha(0,47)==0 && Alpha(47,47)==0 &&
        Alpha(24,24)==255 && solidPixels>500 && solidPixels<2100,
        name+" GUI has a complete model silhouette instead of a flat square texture");
}
var white=Enumerable.Repeat((byte)255,1024).ToArray();
var projected= MinecraftGuiProjection.Block(white,new[]{30f,225f,0f},new[]{0f,0f,0f},new[]{.625f,.625f,.625f});
var shades=Enumerable.Range(0,48*48).Where(i=>projected[i*4+3]>0).Select(i=>projected[i*4]).Distinct().Count();
Check(shades==3,"block GUI shows top and two independently shaded sides");
Check(VoxelGeometry.Item(empty).Vertices.Count==0,"air pixels produce no weapon mesh");
var diamond=File.ReadAllBytes("src/Mycraft/_Module/Visuals/items/diamond_sword.pixels");
var sword=VoxelGeometry.Item(diamond);
Validate(sword);
var solid=0;
for(var i=3;i<diamond.Length;i+=4)if(diamond[i]>=128)solid++;
Check(sword.Vertices.Count==solid*24 && sword.Indices.Count==solid*36,"diamond sword extrudes only solid pixels");
var skin=File.ReadAllBytes("src/Mycraft/_Module/Visuals/steve.rgba");
Check(skin.Length==16384,"user skin expanded to modern 64x64 RGBA");
Check(Math.Abs(VoxelGeometry.NativeV(8f/64)-.875f)<1e-6 && Math.Abs(VoxelGeometry.NativeV(32f/64)-.5f)<1e-6,"native model V converts top-down image rows");
foreach(var vertex in head.Vertices)
{
    var v=VoxelGeometry.NativeV(vertex.V);
    if(v<0 || v>1)throw new Exception("Native UV outside texture");
}
Check(true,"converted head UV remains in bounds");
using(var skinManifest=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("src/Mycraft/_Module/Visuals/manifest.json")))
{
    var metadata=skinManifest.RootElement;
    Check(!string.IsNullOrWhiteSpace(metadata.GetProperty("skinSource").GetString()) &&
        string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(skin)),
            metadata.GetProperty("skinRgbaSha256").GetString(),StringComparison.OrdinalIgnoreCase),
        "body texture matches the selected source skin manifest");
}
if(args.Contains("--expect-green-skin"))
{
    var greenPixels=0;
    for(var i=0;i<skin.Length;i+=4)if(skin[i+3]>0 && skin[i+1]>skin[i] && skin[i+1]>skin[i+2])greenPixels++;
    Check(greenPixels>1000,"optional green skin palette check");
}
Check(MinecraftWeaponPresentation.Sprite("minecraft:bow",false,99,1,false,false)=="bow"&&
    MinecraftWeaponPresentation.Sprite("minecraft:bow",true,12,0,false,false)=="bow_pulling_0"&&
    MinecraftWeaponPresentation.Sprite("minecraft:bow",true,13,0,false,false)=="bow_pulling_1"&&
    MinecraftWeaponPresentation.Sprite("minecraft:bow",true,17,0,false,false)=="bow_pulling_1"&&
    MinecraftWeaponPresentation.Sprite("minecraft:bow",true,18,0,false,false)=="bow_pulling_2",
    "bow follows original native13/18tick pulling thresholds and releases to idle");
Check(MinecraftWeaponPresentation.Sprite("minecraft:crossbow",false,0,0,false,false)=="crossbow_standby"&&
    MinecraftWeaponPresentation.Sprite("minecraft:crossbow",true,100,.579f,false,false)=="crossbow_pulling_0"&&
    MinecraftWeaponPresentation.Sprite("minecraft:crossbow",true,0,.58f,false,false)=="crossbow_pulling_1"&&
    MinecraftWeaponPresentation.Sprite("minecraft:crossbow",true,0,1,false,false)=="crossbow_pulling_2"&&
    MinecraftWeaponPresentation.Sprite("minecraft:crossbow",true,0,0,true,false)=="crossbow_arrow"&&
    MinecraftWeaponPresentation.Sprite("minecraft:crossbow",false,0,0,true,true)=="crossbow_firework",
    "crossbow uses native charge including quick charge and authoritative loaded ammunition");
Check(MinecraftWeaponPresentation.Pose("bow",true,false)==MinecraftWeaponPresentation.UsePose.Bow&&
    MinecraftWeaponPresentation.Pose("crossbow",true,false)==MinecraftWeaponPresentation.UsePose.CrossbowCharge&&
    MinecraftWeaponPresentation.Pose("crossbow",false,true)==MinecraftWeaponPresentation.UsePose.CrossbowAim&&
    MinecraftWeaponPresentation.Pose("trident",true,false)==MinecraftWeaponPresentation.UsePose.Trident&&
    MinecraftWeaponPresentation.Pose("spear",true,false)==MinecraftWeaponPresentation.UsePose.Spear&&
    MinecraftWeaponPresentation.Pose("melee",true,false)==MinecraftWeaponPresentation.UsePose.None,
    "authoritative bow/crossbow/trident/spear use selects distinct aimed poses");
foreach(var material in new[]{"wooden","stone","copper","iron","golden","diamond","netherite"})
{
    string name=material+"_spear";
    Check(MinecraftWeaponPresentation.Asset("minecraft:"+name,"spear",false,0,0,false,false)==name+"_in_hand"&&
        MinecraftWeaponPresentation.Sprite("minecraft:"+name,false,0,0,false,false)==name,
        name+" selects original held texture and preserves its registered GUI icon");
    var pixels=File.ReadAllBytes("src/Mycraft/_Module/Visuals/weapons/"+name+"_in_hand.rgba");
    var geometry=VoxelGeometry.Item(pixels,1.7f,32,15.5f,15.5f,(float)Math.PI/4);Validate(geometry);
    Check(pixels.Length==4096&&geometry.Vertices.Max(v=>v.Z)-geometry.Vertices.Min(v=>v.Z)>2.2f&&
        geometry.Vertices.Max(v=>v.X)-geometry.Vertices.Min(v=>v.X)<.7f,
        name+" preserves real32px long shaft and grip alignment");
}
var trident=VoxelGeometry.Trident();var thrownTrident=VoxelGeometry.Trident(false);Validate(trident);Validate(thrownTrident);
Check(trident.Vertices.Count==120&&trident.Indices.Count==180&&
    Math.Abs(trident.Vertices.Max(v=>v.Z)-trident.Vertices.Min(v=>v.Z)-31f/16)<.00001&&
    Math.Abs(trident.Vertices.Max(v=>v.X)-trident.Vertices.Min(v=>v.X)-5f/16)<.00001,
    "trident reproduces five original pole/base/tine cuboids and31:5 proportions");
Check(Math.Abs(trident.Vertices.Max(v=>v.Z)-thrownTrident.Vertices.Max(v=>v.Z)-14f/16)<.00001,
    "held trident aligns middle shaft to grip while flight preserves native root");
var tridentTexture=File.ReadAllBytes("src/Mycraft/_Module/Visuals/weapons/trident.rgba");
Check(tridentTexture.Length==4096&&tridentTexture.Where((_,i)=>i%4==3).Count(a=>a==255)>100,
    "trident loads original entity atlas rather than its inventory sprite");
for(int face=0;face<trident.Vertices.Count;face+=4)
{
    var vertices=trident.Vertices.Skip(face).Take(4).ToArray();
    for(int y=(int)Math.Round(vertices.Min(v=>v.V)*32);y<(int)Math.Round(vertices.Max(v=>v.V)*32);y++)
    for(int x=(int)Math.Round(vertices.Min(v=>v.U)*32);x<(int)Math.Round(vertices.Max(v=>v.U)*32);x++)
        if(tridentTexture[(y*32+x)*4+3]!=255)throw new Exception("Trident cuboid UV samples transparent pixels outside original face");
}
Check(true,"all five trident cuboids sample the original opaque atlas face regions");
var arrow=VoxelGeometry.Arrow();Validate(arrow);
Check(arrow.Vertices.Count==24&&arrow.Indices.Count==36&&Math.Abs(arrow.Vertices.Max(v=>v.Z)-arrow.Vertices.Min(v=>v.Z)-.9f)<.00001,
    "arrow preserves original crossed planes, rear plane, and .9 root scale");
foreach(string name in new[]{"arrow","spectral_arrow","tipped_arrow"})
{
    var pixels=File.ReadAllBytes("src/Mycraft/_Module/Visuals/weapons/"+name+".rgba");var cutout=VoxelGeometry.Arrow(pixels);Validate(cutout);
    for(int face=0;face<cutout.Vertices.Count;face+=4)
    {
        var vertices=cutout.Vertices.Skip(face).Take(4).ToArray();
        int x=(int)(vertices.Average(v=>v.U)*32),y=(int)(vertices.Average(v=>v.V)*32);
        if(pixels[(y*32+x)*4+3]<128)throw new Exception("Arrow opaque geometry samples a transparent source texel");
    }
    Check(cutout.Vertices.Count>arrow.Vertices.Count&&Math.Abs(cutout.Vertices.Max(v=>v.Z)-cutout.Vertices.Min(v=>v.Z)-.9f)<.00001,
        name+" keeps original model dimensions and source silhouette without opaque rectangles");
}
var direction=MinecraftWeaponPresentation.ProjectileDirection(90,30);
Check(Math.Abs(direction.X-(float)Math.Cos(Math.PI/6))<.00001&&Math.Abs(direction.Y)<.00001&&Math.Abs(direction.Z-.5f)<.00001,
    "projectile orientation converts native arrow yaw/pitch instead of player look yaw");
Check(!MinecraftWeaponPresentation.ValidProjectile("id","arrow",float.NaN,0,0,0,0)&&
    !MinecraftWeaponPresentation.ValidProjectile("id","unknown",0,0,0,0,0)&&
    MinecraftWeaponPresentation.ValidProjectile("id","trident",10,64,15,-30,8),
    "projectile validation bounds transforms and accepts only supported original families");
var motion=new MinecraftProjectileMotion();motion.Apply(1,2,3,179,0);motion.Advance(.05f);motion.Apply(3,4,5,-179,20);motion.Advance(.025f);
motion.Sample(out var px,out var py,out var pz,out var yaw,out var pitch);
Check(Math.Abs(px-2)<.00001&&Math.Abs(py-3)<.00001&&Math.Abs(pz-4)<.00001&&Math.Abs(yaw-180)<.00001&&Math.Abs(pitch-10)<.00001,
    "projectile interpolation is bounded and takes shortest rotation across180degrees");
motion.Advance(.25f);motion.Sample(out px,out py,out pz,out yaw,out pitch);
Check(px==3&&py==4&&pz==5,"missing authoritative state never extrapolates flight or invents impact");
motion.Reset();motion.Apply(20,30,40,0,0);motion.Sample(out px,out py,out pz,out yaw,out pitch);
Check(px==20&&py==30&&pz==40,"pooled projectile starts at its new authoritative spawn instead of streaking from old flight");
Check(LightningGeometry.TrySeed("ffffffffffffffff",out var lightningSeed)&&lightningSeed==ulong.MaxValue&&
    !LightningGeometry.TrySeed("-1",out _)&&!LightningGeometry.TrySeed("000000000000000g",out _)&&!LightningGeometry.TrySeed("00000000000000000",out _),
    "lightning seed preserves native signed64-bit bits with exact bounded16-digit hex");
// These hashes come from LightningOracle.java invoking the pinned original
// LightningBoltRenderer.lambda$submit$0 and recording all896 native vertices.
foreach(var golden in new[]{
    ("0000000000000000","3a9087d7eb8ae935a6fd2e5949b256cd00b59d753bfa0d17c1d9f6231c8bfadc"),
    ("0000000000000001","6922abf865dbd567f20f168163bb4bb5d70fc3484f9e0b4768508bb652e2f124"),
    ("0123456789abcdef","a5c97e0dd4a2d5fe4d100b575c4c8bae8af19b44263717cb5deffc18e8ba26a8"),
    ("ffffffffffffffff","082ade6acf25475e88cb813efe2c0801a0ffaf8521dfa4876a86f22d90d9cbbd")})
{
    LightningGeometry.TrySeed(golden.Item1,out lightningSeed);var bolt=LightningGeometry.Build(lightningSeed);
    using var bytes=new MemoryStream();using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
        foreach(var v in bolt.Vertices){writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}
    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    Check(hash==golden.Item2,"lightning "+golden.Item1+" matches every original renderer vertex byte for byte");
    foreach(var v in bolt.Vertices)
        if(!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)||!float.IsFinite(v.Nx)||!float.IsFinite(v.Ny)||!float.IsFinite(v.Nz))
            throw new Exception("Invalid seeded lightning vertex/normal");
    for(int i=0;i<bolt.Indices.Count;i+=3)
    {
        var a=bolt.Vertices[bolt.Indices[i]];var b=bolt.Vertices[bolt.Indices[i+1]];var c=bolt.Vertices[bolt.Indices[i+2]];
        float ax=b.X-a.X,ay=b.Y-a.Y,az=b.Z-a.Z,bx=c.X-a.X,by=c.Y-a.Y,bz=c.Z-a.Z;
        if((ay*bz-az*by)*a.Nx+(az*bx-ax*bz)*a.Ny+(ax*by-ay*bx)*a.Nz<=0)throw new Exception("Inverted lightning triangle after native axis reflection");
    }
    Check(bolt.Vertices.Count==896&&bolt.Indices.Count==1344&&bolt.Faces.Count==224&&
        bolt.Faces.Count(f=>f.Branch==0)==128&&bolt.Faces.Count(f=>f.Branch==1)==48&&bolt.Faces.Count(f=>f.Branch==2)==48&&
        bolt.Vertices.Min(v=>v.Z)==0&&bolt.Vertices.Max(v=>v.Z)==128,
        "lightning "+golden.Item1+" preserves eight main segments, two3-segment branches, four layers, outward faces and native128-block height");
}
Console.WriteLine("ALL " + checks + " MODEL CHECKS PASSED");
