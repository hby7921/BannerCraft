using System;
using System.IO;
using System.Linq;
using System.Threading;
using Mycraft.Building;
using Mycraft.Demo;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
bool Near(float a, float b) => Math.Abs(a-b) < .0001f;
bool Hit(float ox,float oy,float oz,float dx,float dy,float dz,int x,int y,int z,float reach,
    float expected,int nx,int ny,int nz) =>
    BlockGeometry.Ray(ox,oy,oz,dx,dy,dz,x,y,z,reach,out var distance,out var actualX,out var actualY,out var actualZ)
    && Near(distance,expected) && actualX==nx && actualY==ny && actualZ==nz;

Check(BlockGeometry.Cell(-.01f)==-1 && BlockGeometry.Cell(-1)==-1 && BlockGeometry.Cell(.99f)==0,
    "negative world cells use floor, not truncation");
Check(Near(BlockGeometry.Height(-59,-60,42.5f),43.5f),"MC vertical offset preserves Bannerlord activation height");
Check(BlockGeometry.GroundCell(42.52f,-60,42.5f)==-60,"terrain noise within tolerance does not lift whole block");
Check(BlockGeometry.GroundCell(42.54f,-60,42.5f)==-59,"rising ground advances grid cell rather than embedding block");
Check(BlockGeometry.ClearsTerrain(42.5f,42.52f) && !BlockGeometry.ClearsTerrain(42.5f,42.54f),"slope clearance matches grid tolerance");
Check(!BlockGeometry.ClearsTerrain(float.NaN,0) && !BlockGeometry.ClearsTerrain(0,float.PositiveInfinity),"invalid terrain cannot approve placement");
var birthFloor=BlockGeometry.TntGroundSupport(0,0,.25f,128,0);
var airborneGround=BlockGeometry.EntityGroundPatch(new[]{birthFloor},148.25f);
var nearbyGround=BlockGeometry.EntityGroundPatch(new[]{birthFloor},143.75f);
Check(airborneGround.Count==0,"airborne Wither ground farther than16 cells is omitted before wire send");
Check(BlockGeometry.EntityGroundPatch(new[]{birthFloor},107.9f).Count==0,"falling creature does not send ground more than16 cells above it");
Check(nearbyGround.Count==1&&ReferenceEquals(nearbyGround[0],birthFloor)&&nearbyGround[0].H==.25f,
    "nearby native terrain keeps its exact sampled cell and fractional height");
Check(BlockGeometry.EntityGroundPatch(new[]{birthFloor},144.99f).Count==1&&
    BlockGeometry.EntityGroundPatch(new[]{birthFloor},145f).Count==0,"positive fractional anchor uses native floor at16/17 boundary");
var belowZero=BlockGeometry.TntGroundSupport(0,0,-31.75f,0,0);
Check(BlockGeometry.EntityGroundPatch(new[]{belowZero},-15.01f).Count==1&&
    BlockGeometry.EntityGroundPatch(new[]{belowZero},-15f).Count==0,"negative fractional anchor uses floor rather than truncation");
var cliff=new[]{birthFloor,BlockGeometry.TntGroundSupport(1,0,32.5f,128,0)};
Check(BlockGeometry.EntityGroundPatch(cliff,128.5f).SequenceEqual(new[]{birthFloor}),"steep patch preserves nearby floor while excluding distant height");
Check(BlockGeometry.EntityGroundPatch(new[]{birthFloor},float.NaN).Count==0&&
    BlockGeometry.EntityGroundPatch(new[]{birthFloor},float.PositiveInfinity).Count==0,"invalid entity height cannot emit a ground patch");
Check(BlockGeometry.CanAdd(255,false) && !BlockGeometry.CanAdd(256,false),"new cells stop at live block budget");
Check(BlockGeometry.CanAdd(256,true),"authoritative update of existing cell works at capacity");
Check(!BlockGeometry.CanAdd(-1,true) && !BlockGeometry.CanAdd(257,true),"invalid scene counts reject updates");
Check(BlockGeometry.Allowed("minecraft:tnt") && BlockGeometry.Allowed("minecraft:oak_slab") && BlockGeometry.Allowed("minecraft:fire") &&
    !BlockGeometry.Allowed("stone") && !BlockGeometry.Allowed("minecraft:air") && !BlockGeometry.Allowed("minecraft:../bad path"),"native model IDs replace the old five-cube whitelist");

Check(Hit(-2,.5f,.5f,1,0,0,0,0,0,8,2,-1,0,0),"west face has outward west normal");
Check(Hit(3,.5f,.5f,-1,0,0,0,0,0,8,2,1,0,0),"east face has outward east normal");
Check(Hit(.5f,-2,.5f,0,1,0,0,0,0,8,2,0,-1,0),"bottom face places below hit block");
Check(Hit(.5f,3,.5f,0,-1,0,0,0,0,8,2,0,1,0),"top face places above hit block");
Check(Hit(.5f,.5f,-2,0,0,1,0,0,0,8,2,0,0,-1),"north face has outward north normal");
Check(Hit(.5f,.5f,3,0,0,-1,0,0,0,8,2,0,0,1),"south face has outward south normal");
Check(Hit(-5,-59.5f,-2.5f,1,0,0,-3,-60,-3,8,2,-1,0,0),"negative MC height and horizontal cells raycast correctly");
Check(!BlockGeometry.Ray(-2,2,.5f,1,0,0,0,0,0,8,out _,out _,out _,out _),"parallel ray outside slab misses");
Check(!BlockGeometry.Ray(3,.5f,.5f,1,0,0,0,0,0,8,out _,out _,out _,out _),"block behind player is not targeted");
Check(!BlockGeometry.Ray(.5f,.5f,.5f,1,0,0,0,0,0,8,out _,out _,out _,out _),"inside-block eye cannot produce placement normal");
Check(!BlockGeometry.Ray(-2,.5f,.5f,1,0,0,0,0,0,1.99f,out _,out _,out _,out _)
    && Hit(-2,.5f,.5f,1,0,0,0,0,0,2,2,-1,0,0),"native obstacle distance clips farther blocks including reach boundary");
Check(Hit(0,.5f,.5f,1,0,0,0,0,0,8,0,-1,0,0),"on-surface inward ray retains valid placement face");
Check(!BlockGeometry.Ray(0,.5f,.5f,-1,0,0,0,0,0,8,out _,out _,out _,out _),"on-surface outward ray does not hit a block behind view");
Check(!BlockGeometry.Ray(float.NaN,0,0,1,0,0,0,0,0,8,out _,out _,out _,out _)
    && !BlockGeometry.Ray(-2,.5f,.5f,float.PositiveInfinity,0,0,0,0,0,8,out _,out _,out _,out _)
    && !BlockGeometry.Ray(-2,.5f,.5f,1,0,0,0,0,0,-1,out _,out _,out _,out _),"nonfinite rays and negative reach reject");

Check(BlockGeometry.OverlapsAgent(0,0,0,.5f,.5f,0,false),"cannot box in standing player or enemy");
Check(!BlockGeometry.OverlapsAgent(0,0,0,2,.5f,0,false)
    && BlockGeometry.OverlapsAgent(0,0,0,2,.5f,0,true),"horse clearance is wider than human clearance");
Check(!BlockGeometry.OverlapsAgent(0,0,3,.5f,.5f,0,false),"block above agent head has no false overlap");

var expectedNormals = new[] {(1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1)};
for (int face=0;face<6;face++)
{
    var vertices=BlockGeometry.Faces[face];
    foreach (var coordinate in vertices) if (Math.Abs(coordinate)!=.5f) throw new Exception("Non-unit cube coordinate");
    foreach (var triangle in new[] {(0,1,2),(0,2,3)})
    {
        int a=triangle.Item1*3,b=triangle.Item2*3,c=triangle.Item3*3;
        float ux=vertices[b]-vertices[a],uy=vertices[b+1]-vertices[a+1],uz=vertices[b+2]-vertices[a+2];
        float vx=vertices[c]-vertices[a],vy=vertices[c+1]-vertices[a+1],vz=vertices[c+2]-vertices[a+2];
        var n=expectedNormals[face];
        if ((uy*vz-uz*vy)*n.Item1+(uz*vx-ux*vz)*n.Item2+(ux*vy-uy*vx)*n.Item3<=0)
            throw new Exception("Inverted face " + face);
    }
}
Check(true,"all twelve unit-cube triangles face outward with nonzero area");
foreach (var name in BlockGeometry.Items)
{
    Check(BlockGeometry.Allowed("minecraft:"+name),"model ID accepts " + name);
    var pixels=File.ReadAllBytes("src/Mycraft/_Module/Visuals/blocks/"+name+".rgba");
    Check(pixels.Length==1024,"16x16 RGBA asset " + name);
    for(int i=3;i<pixels.Length;i+=4) if(pixels[i]!=255) throw new Exception("Transparent cube asset " + name);
}
Check(true,"all block assets are fully opaque");

var bytes=SnapshotFixtures.Slab();
var model=BlockModelSnapshot.Parse(bytes);
Check(model.Textures.Length==1&&model.Textures[0].Width==2&&model.Textures[0].Height==1&&model.Textures[0].Pixels[7]==0,
    "native snapshot preserves nonsquare textures and actual transparent pixels");
Check(model.Quads.Length==1&&model.Quads[0].Tint==0xff559922u&&model.Collision.Length==1&&model.Outline.Length==1,
    "snapshot preserves face tint plus independent collision and outline shapes");
var slab=model.Collision[0];
var nativeBox=BlockGeometry.NativeBox(slab);
Check(Near(nativeBox.MaxX,1)&&Near(nativeBox.MaxY,1)&&Near(nativeBox.MaxZ,.5f),"Minecraft slab height maps to Bannerlord Z instead of a full cube");
var quad=model.Quads[0];
var v0=quad.NativeVertex(0);var v1=quad.NativeVertex(1);var v2=quad.NativeVertex(2);
Check(Near(v0.Z,.5f)&&Near(v1.Y,1)&&Near(v0.Nz,1)&&Near(v0.V,1)&&Near(v1.V,0),
    "native quad conversion swaps Y/Z, converts normals and flips top-down texture V");
foreach(var start in new[]{0,3})
{
    var a=quad.NativeVertex(BlockModelSnapshot.NativeWinding[start]);
    var b=quad.NativeVertex(BlockModelSnapshot.NativeWinding[start+1]);
    var c=quad.NativeVertex(BlockModelSnapshot.NativeWinding[start+2]);
    float ux=b.X-a.X,uy=b.Y-a.Y,uz=b.Z-a.Z,vx=c.X-a.X,vy=c.Y-a.Y,vz=c.Z-a.Z;
    Check((uy*vz-uz*vy)*a.Nx+(uz*vx-ux*vz)*a.Ny+(ux*vy-uy*vx)*a.Nz>0,"reflected native triangle "+start+" retains outward winding");
}
Check(!BlockGeometry.RayBox(-1,.75f,.5f,1,0,0,slab,8,out _,out _,out _,out _),"crosshair passes through empty upper half of a bottom slab");
Check(BlockGeometry.RayBox(.5f,2,.5f,0,-1,0,slab,8,out var slabDistance,out _,out var slabNormal,out _)&&Near(slabDistance,1.5f)&&slabNormal==1,
    "top click hits the actual half-height slab surface");
Check(BlockModelSnapshot.Parse(SnapshotFixtures.Slab(false)).Collision.Length==0,
    "fire-like noncolliding models do not acquire a full-cube collider");
Check(!BlockGeometry.OverlapsAgent(slab,.5f,.6f,.5f,false)&&BlockGeometry.OverlapsAgent(new BlockBox(0,0,0,1,1,1),.5f,.6f,.5f,false),
    "agent clearance uses actual slab collision rather than empty volume");
Check(BlockGeometry.CameraReach(-3,0,0,1,0,0,0,0,0,8,out var cameraFar)&&Near(cameraFar,11),
    "third-person camera reach remains eight metres from the player eye");
Check(!BlockGeometry.CameraReach(0,9,0,1,0,0,0,0,0,8,out _)&&!BlockGeometry.CameraReach(0,0,0,1,0,0,0,0,0,-1,out _),
    "camera ray missing player reach sphere and negative reach are rejected");
Check(new[]{BlockGeometry.Face(0,-1,0),BlockGeometry.Face(0,1,0),BlockGeometry.Face(0,0,-1),BlockGeometry.Face(0,0,1),BlockGeometry.Face(-1,0,0),BlockGeometry.Face(1,0,0)}.SequenceEqual(new[]{0,1,2,3,4,5}),
    "hit faces use Minecraft Direction ordinals");
void Reject(byte[] input,string name)
{
    try{BlockModelSnapshot.Parse(input);}
    catch(InvalidDataException){Check(true,name);return;}
    catch(EndOfStreamException){Check(true,name);return;}
    Check(false,name);
}
Reject(SnapshotFixtures.ChangeInt(bytes,0,0),"wrong snapshot magic is rejected");
Reject(SnapshotFixtures.ChangeInt(bytes,4,2),"unknown snapshot version is rejected");
Reject(SnapshotFixtures.ChangeInt(bytes,8,65),"texture count is bounded before allocation");
Reject(SnapshotFixtures.ChangeInt(bytes,12,257),"texture dimensions are bounded before allocation");
Reject(SnapshotFixtures.ChangeInt(bytes,28,2049),"quad count is bounded before allocation");
Reject(SnapshotFixtures.ChangeInt(bytes,32,1),"out-of-range quad material is rejected");
Reject(SnapshotFixtures.ChangeInt(bytes,40,0x7fc00000),"NaN vertex cannot enter native mesh generation");
Reject(SnapshotFixtures.ChangeInt(bytes,132,65),"collision box count is bounded");
Reject(SnapshotFixtures.ChangeInt(bytes,148,0),"degenerate collision AABB is rejected");
Reject(bytes.Take(bytes.Length-1).ToArray(),"truncated shape data is rejected");
Reject(bytes.Concat(new byte[]{0}).ToArray(),"unexpected trailing data is rejected");
Reject(new byte[BlockModelSnapshot.MaxBytes+1],"oversized binary is rejected");
Check(BlockModelSnapshot.ValidKey(new string('a',32))&&!BlockModelSnapshot.ValidKey(new string('A',32))&&!BlockModelSnapshot.ValidKey("../snapshot"),
    "session and model keys cannot escape the local snapshot directory");
var fixtureRoot=Path.GetFullPath(Path.Combine("tests","Building",".snapshot-test-"+Guid.NewGuid().ToString("N")));
var sid=Guid.NewGuid().ToString("N");var key=Guid.NewGuid().ToString("N");var sessionStart=DateTime.UtcNow;
Directory.CreateDirectory(Path.Combine(fixtureRoot,sid));
try
{
    var file=Path.Combine(fixtureRoot,sid,key+".bin");File.WriteAllBytes(file,bytes);
    var loaded=await BlockModelSnapshot.LoadAsync(fixtureRoot,sid,key,sessionStart,CancellationToken.None);
    Check(loaded.ByteLength==bytes.Length,"background transport reads a finished session-scoped snapshot");
    File.SetLastWriteTimeUtc(file,sessionStart.AddMinutes(-5));
    try{await BlockModelSnapshot.LoadAsync(fixtureRoot,sid,key,sessionStart,CancellationToken.None);Check(false,"stale snapshot is rejected");}
    catch(InvalidDataException){Check(true,"stale snapshot is rejected");}
    var errorKey=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(fixtureRoot,sid,errorKey+".error"),"Block requires a special renderer");
    try{await BlockModelSnapshot.LoadAsync(fixtureRoot,sid,errorKey,sessionStart,CancellationToken.None);Check(false,"export error is reported immediately");}
    catch(InvalidDataException e){Check(e.Message.Contains("special renderer"),"export error is reported immediately without a missing-file timeout");}
    using var cancellation=new CancellationTokenSource();cancellation.Cancel();
    try{await BlockModelSnapshot.LoadAsync(fixtureRoot,sid,Guid.NewGuid().ToString("N"),sessionStart,cancellation.Token);Check(false,"session cancellation stops reader");}
    catch(OperationCanceledException){Check(true,"session cancellation stops reader");}
}
finally
{
    var testsRoot=Path.GetFullPath(Path.Combine("tests","Building"))+Path.DirectorySeparatorChar;
    if(fixtureRoot.StartsWith(testsRoot,StringComparison.OrdinalIgnoreCase))Directory.Delete(fixtureRoot,true);
}
var prefabCentered=BlockGeometry.PrefabFrame(new BlockBox(0,0,0,1,1,1),new BlockBox(-.5f,-.5f,-.5f,.5f,.5f,.5f));
Check(prefabCentered.SequenceEqual(new[]{1f,1f,1f,.5f,.5f,.5f}),"unit cube aligns a centered native prefab using only its entity frame");
var prefabFeet=BlockGeometry.PrefabFrame(new BlockBox(0,0,0,1,.5f,1),new BlockBox(-.5f,-.5f,0,.5f,.5f,1));
Check(prefabFeet.SequenceEqual(new[]{1f,1f,.5f,.5f,.5f,0f}),"partial MC height scales native prefab Z while preserving its foot origin");
var prefabOffcenter=BlockGeometry.PrefabFrame(new BlockBox(.6f,0,.2f,1,1,.8f),new BlockBox(0,0,0,1,1,1));
Check(Near(prefabOffcenter[0],.4f)&&Near(prefabOffcenter[1],.6f)&&Near(prefabOffcenter[2],1)&&
    Near(prefabOffcenter[3],.6f)&&Near(prefabOffcenter[4],.2f),"compound AABB offsets survive MC-to-native axis conversion");
try{BlockGeometry.PrefabFrame(slab,new BlockBox(0,0,0,0,1,1));Check(false,"zero-size prefab rejected");}
catch(ArgumentException){Check(true,"zero-size prefab cannot be scaled into a collider");}
Check(TntSceneTiming.White(80)&&!TntSceneTiming.White(75)&&TntSceneTiming.White(70),"TNT white flash follows original five-tick alternating cadence");
Check(Near(TntSceneTiming.Scale(80),1)&&Near(TntSceneTiming.Scale(10),1)&&Near(TntSceneTiming.Scale(0),1.3f)&&
    TntSceneTiming.Scale(5)>1&&TntSceneTiming.Scale(5)<TntSceneTiming.Scale(1),"TNT swelling is limited to the final ten ticks with vanilla quartic growth");
Check(Near(TntSceneTiming.Remaining(40,.1f),39)&&Near(TntSceneTiming.Remaining(40,10),36),
    "visual fuse interpolation is bounded when authoritative state is delayed");
var tntMotion=new TntSceneMotion();
Check(tntMotion.Apply(10,2,20,80,true),"primed TNT accepts an authoritative initial pose");
tntMotion.Sample(out var tx,out var ty,out var tz);
Check(Near(tx,10)&&Near(ty,2)&&Near(tz,20),"first TNT state starts at its real position without flying from origin");
tntMotion.Apply(11,3,20,79,true);tntMotion.Advance(.025f);tntMotion.Sample(out tx,out ty,out tz);
Check(Near(tx,10.5f)&&Near(ty,2.5f),"TNT bounce/motion interpolates authority coordinates instead of host physics");
Check(!tntMotion.Apply(100,100,100,80,true)&&Near(tntMotion.X,11),"late TNT packets cannot rewind the fuse or move the entity backward in event time");
Check(tntMotion.Apply(11,3,20,0,false)&&!tntMotion.Apply(11,3,20,0,true),"terminal TNT state cannot resurrect from a late packet");
Check(!TntSceneTiming.ValidPosition(float.NaN,0,0)&&!TntSceneTiming.ValidId("../tnt")&&!TntSceneTiming.ValidId(new string('x',97)),
    "TNT scene rejects malformed positions and identifiers");
Check(TntSceneTiming.ExplosionFrame(0,.4f)==0&&TntSceneTiming.ExplosionFrame(.2f,.4f)==7&&TntSceneTiming.ExplosionFrame(.4f,.4f)==15,
    "explosion frame selection uses native integer age times fifteen divided by lifetime");
Check(Enumerable.Range(0,7).Select(age=>TntSceneTiming.ParticleFrame(age,6,16)).SequenceEqual(new[]{0,2,5,7,10,12,15}),
    "six-tick explosion follows the exact native skipped-frame sequence");
Check(TntSceneTiming.ParticleAlive(6,6)&&!TntSceneTiming.ParticleAlive(7,6),
    "native final sprite remains alive for its last tick before removal");
var particleClock=new TntParticleClock();
Check(particleClock.Advance(.05f)==1&&particleClock.Advance(.15f)==3,
    "first 50ms emits one batch and a later hitch advances three distinct birth ages");
var particle60Hz=new TntParticleClock();int nativeSteps=0;
for(int f=0;f<60;f++)nativeSteps+=particle60Hz.Advance(1f/60);
Check(nativeSteps==20,"particle simulation is 20Hz independently of 60Hz rendering");
Check(TntSceneTiming.CanSpawnParticle(false,128,255)&&!TntSceneTiming.CanSpawnParticle(false,0,256)&&
    TntSceneTiming.CanSpawnParticle(true,127,256)&&!TntSceneTiming.CanSpawnParticle(true,128,0),
    "smoke and explosion retain independent 128 and 256 live-particle budgets");
var overlaySource=new byte[]{0,0,0,255,255,255,255,128,32,128,224,0};
var overlaid=TntSceneTiming.WhiteOverlay(overlaySource);
Check(overlaid[0]==192&&overlaid[4]==255&&overlaid[3]==255&&overlaid[7]==128&&overlaid[11]==0&&
    overlaid[8]<overlaid[9]&&overlaid[9]<overlaid[10]&&overlaySource[0]==0,
    "native white overlay retains TNT contrast, alpha, and unmodified source pixels");
var smokeRandom=new Random(42);
foreach(var kind in new[]{"explosion","smoke"})
{
    int size=kind=="explosion"?32:8,count=kind=="explosion"?16:8;
    for(int frame=0;frame<count;frame++)
    {
        var rgba=File.ReadAllBytes("src/Mycraft/_Module/Visuals/particles/"+kind+"_"+frame+".rgba");
        var coverage=new bool[size*size];
        var spans=ParticleSilhouette.Read(rgba,size);
        foreach(var span in spans)
        {
            if(span.Start<0||span.End>size||span.End<=span.Start||span.Row<0||span.Row>=size)throw new Exception("Invalid silhouette bounds");
            for(int x=span.Start;x<span.End;x++)
            {
                if(coverage[span.Row*size+x])throw new Exception("Overlapping particle faces");
                coverage[span.Row*size+x]=true;
            }
        }
        Check(Enumerable.Range(0,size*size).All(p=>coverage[p]==(rgba[p*4+3]>=26))&&spans.Count>0,
            kind+" frame "+frame+" silhouette preserves every opaque pixel and transparent hole");
    }
}
for(int sampleIndex=0;sampleIndex<1000;sampleIndex++)
{
    var smoke=TntSceneTiming.SmokeSample(smokeRandom);
    if(smoke.Lifetime<8||smoke.Lifetime>40||smoke.Width<.14999f||smoke.Width>.30001f||smoke.Shade<0||smoke.Shade>.3f||
        Math.Abs(smoke.X)>.0181f||Math.Abs(smoke.Z)>.0181f||smoke.Y<-.0081f||smoke.Y>.0281f)
        throw new Exception("Smoke provider parameters differ from native bounds");
}
Check(true,"smoke provider preserves native lifetime, full-quad size, shade and initial velocity bounds");
Check(TntSceneTiming.SmokeGrowth(0,0,40)==0&&Near(TntSceneTiming.SmokeGrowth(0,.5f,40),.4f)&&TntSceneTiming.SmokeGrowth(2,0,40)==1&&
    Near(TntSceneTiming.SmokeRisePerTick,.004f)&&Near(TntSceneTiming.SmokeFriction,.96f),
    "smoke keeps native initial growth, upward gravity and tick friction");
for(int frame=0;frame<8;frame++)
{
    var smokePixels=File.ReadAllBytes("src/Mycraft/_Module/Visuals/particles/smoke_"+frame+".rgba");
    if(smokePixels.Length!=256)throw new Exception("Native smoke frame must be 8x8 RGBA");
}
Check(true,"all eight original smoke sprite frames are present in 8x8 RGBA format");
float authoritativeBlastRadius=4;
Check(TntSceneTiming.VisualSpreadScale==.5f&&Near(TntSceneTiming.ExplosionVisualSpread(authoritativeBlastRadius),2)&&authoritativeBlastRadius==4,
    "host explosion spread is halved independently of the unchanged authoritative damage radius");
Check(TntSceneTiming.VisualQuadScale==.5f&&Near(TntSceneTiming.ExplosionVisualQuadSize(0),2)&&Near(TntSceneTiming.ExplosionVisualQuadSize(7),1.125f),
    "host explosion billboard width is halved while preserving vanilla emission-age sizing");
var explosionHashes=new System.Collections.Generic.HashSet<string>();
for(int frame=0;frame<16;frame++)
{
    var pixels=File.ReadAllBytes("src/Mycraft/_Module/Visuals/particles/explosion_"+frame+".rgba");
    Check(pixels.Length==4096,"original Minecraft explosion frame "+frame+" has 32x32 RGBA pixels");
    bool solidFrame=false,transparentFrame=false;
    for(int p=3;p<pixels.Length;p+=4){solidFrame|=pixels[p]>0;transparentFrame|=pixels[p]==0;}
    if(!solidFrame||!transparentFrame)throw new Exception("Explosion frame lost its sprite alpha silhouette");
    explosionHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
}
Check(explosionHashes.Count==16,"explosion frames are distinct source animation images with retained alpha");
const int stageX=-4096,stageZ=98304;
var nativeStage=BlockGeometry.ToNative(stageX+12.25f,129.5f,stageZ-3.5f,128,42.5f,stageX,stageZ);
Check(Near(nativeStage.X,12.25f)&&Near(nativeStage.Y,-3.5f)&&Near(nativeStage.Z,44),
    "nonzero stage origin maps actual MC TNT/block coordinates to both native horizontal axes");
var minecraftStage=BlockGeometry.ToMinecraft(nativeStage.X,nativeStage.Y,nativeStage.Z,128,42.5f,stageX,stageZ);
Check(Near(minecraftStage.X,stageX+12.25f)&&Near(minecraftStage.Y,129.5f)&&Near(minecraftStage.Z,stageZ-3.5f),
    "nonzero stage origin round-trips native ray/agent coordinates into actual MC space");
var stageRay=BlockGeometry.ToMinecraft(10,-3.5f,44,128,42.5f,stageX,stageZ);
Check(BlockGeometry.Ray(stageRay.X,stageRay.Y,stageRay.Z,1,0,0,stageX+12,129,stageZ-4,8,out var stageDistance,out var stageNormal,out _,out _)&&
    Near(stageDistance,2)&&stageNormal==-1,"crosshair ray still selects the correct face in a remote MC stage");
var stageAgent=BlockGeometry.ToMinecraft(12.5f,-3.5f,42.5f,128,42.5f,stageX,stageZ);
Check(BlockGeometry.OverlapsAgent(new BlockBox(stageX+12,128,stageZ-4,stageX+13,129,stageZ-3),stageAgent.X,stageAgent.Y,stageAgent.Z,false),
    "agent collision checks compare actual MC positions against remote-stage block AABBs");
var stageTerrain=BlockGeometry.ToNative(stageX+12,128,stageZ-4,128,42.5f,stageX,stageZ);
Check(stageTerrain.X==12&&stageTerrain.Y==-4&&BlockGeometry.GroundCell(stageTerrain.Z,128,42.5f)-1==127,
    "ground patches sample native terrain while keeping remote MC support-cell IDs");
Check(BlockGeometry.Cell(-.25f)+stageX==BlockGeometry.Cell(stageX-.25f)&&BlockGeometry.Cell(.25f)+stageZ==BlockGeometry.Cell(stageZ+.25f),
    "terrain hit-cell flooring preserves negative local positions under integer stage offsets");
var flatSupport=BlockGeometry.TntGroundSupport(stageX+12,stageZ-4,42.5f,128,42.5f);
Check(flatSupport.X==stageX+12&&flatSupport.Z==stageZ-4&&flatSupport.Y==127&&flatSupport.H==1&&flatSupport.Top==128&&flatSupport.TntPlacementY==128,
    "integer terrain uses the below-cell full support and keeps TNT on the original grid");
var partialSupport=BlockGeometry.TntGroundSupport(stageX+12,stageZ-4,42.7f,128,42.5f);
Check(partialSupport.Y==128&&Near(partialSupport.H,.2f)&&Near(partialSupport.Top,128.2f)&&partialSupport.TntPlacementY==128,
    "low fractional terrain allows TNT in its support cell with limited sinking");
Check(BlockGeometry.GroundCell(42.7f,128,42.5f)==129,
    "TNT terrain fitting does not change generic block placement height");
var highSupport=BlockGeometry.TntGroundSupport(0,0,43.3f,128,42.5f);
Check(highSupport.Y==128&&Near(highSupport.H,.8f)&&highSupport.TntPlacementY==129&&Near(highSupport.TntPlacementY-highSupport.Top,.2f),
    "high fractional terrain selects the upper integer cell with less than 0.3m clearance");
Check(BlockGeometry.GroundHeightSteps(.7f)==179&&BlockGeometry.TntPlacementCell(128,.7f)==128&&
    BlockGeometry.TntPlacementCell(128,179.49f/256)==128&&BlockGeometry.TntPlacementCell(128,179.5f/256)==129,
    "TNT same-cell threshold matches Java nearest-1/256 quantization with at most 179 height steps");
Check(BlockGeometry.GroundHeightSteps(0)==1&&BlockGeometry.GroundHeightSteps(1)==256&&BlockGeometry.GroundHeightSteps(2)==256,
    "terrain support height steps are clamped to the native 1..256 state range");
var terrainNoise=BlockGeometry.TntGroundSupport(0,0,42.51f,128,42.5f);
Check(terrainNoise.Y==127&&terrainNoise.H==1,"near-integer terrain noise keeps the below-cell support within tolerance");
var negativeSupport=BlockGeometry.TntGroundSupport(0,0,-59.4f,0,0);
Check(negativeSupport.Y==-60&&Near(negativeSupport.H,.6f)&&negativeSupport.TntPlacementY==-60,
    "fractional support uses floor consistently at negative MC heights");
for(float h=.031f;h<1;h+=.005f)
{
    var support=BlockGeometry.TntGroundSupport(0,0,h,0,0);
    if(support.Top-support.TntPlacementY>.7f+1f/256||support.TntPlacementY-support.Top>.3001f)
        throw new Exception("TNT placement exceeded grid-fit sinking/clearance bounds");
}
Check(true,"TNT grid fitting stays within the 0.7m quantized sinking or 0.3m clearance column-top bounds");
Check(BlockGeometry.ClearsTntTerrain(42.5f,43.1f)&&BlockGeometry.ClearsTntTerrain(42.5f,43.233f)&&
    !BlockGeometry.ClearsTntTerrain(42.5f,43.235f)&&!BlockGeometry.ClearsTntTerrain(float.NaN,43),
    "TNT terrain precheck includes one height step and ground tolerance but rejects deeper or invalid placement");
Check(!BlockGeometry.ClearsTerrain(42.5f,43.1f),"generic block terrain clearance remains strict");
var clipped=BlockSurfaceGeometry.Build(BlockModelSnapshot.Parse(SnapshotFixtures.Slab()));
Check(clipped.Length==1&&clipped[0].Vertices.All(v=>v.X<=.500001f)&&clipped[0].Vertices.Any(v=>Near(v.X,.5f)),
    "transparent texels physically remove half of the original slab surface");
var rotated=BlockModelSnapshot.Parse(SnapshotFixtures.Slab());
for(int p=0;p<20;p+=5)(rotated.Quads[0].Vertices[p+3],rotated.Quads[0].Vertices[p+4])=(rotated.Quads[0].Vertices[p+4],rotated.Quads[0].Vertices[p+3]);
var rotatedSurface=BlockSurfaceGeometry.Build(rotated)[0];
Check(rotatedSurface.Vertices.All(v=>v.Y<=.500001f)&&rotatedSurface.Vertices.Any(v=>Near(v.X,1)),
    "UV clipping preserves90degree face rotations and original physical coordinates");
try{BlockSurfaceGeometry.Build(rotated,0);throw new Exception("Missing surface budget guard");}
catch(InvalidOperationException){Check(true,"expanded cutout geometry respects native vertex budget");}
var wires=new[]{"power0","power15"}.Select(state=>BlockModelSnapshot.Parse(File.ReadAllBytes("tests/Building/Fixtures/redstone_wire_"+state+".bin"))).ToArray();
foreach(var wire in wires)
{
    var collision=wire.Collision.ToArray();var outline=wire.Outline.ToArray();var surfaces=BlockSurfaceGeometry.Build(wire);
    Check(wire.Textures[0].Pixels.Where((_,i)=>i%4==3).Count(a=>a==0)==234&&wire.Textures[1].Pixels.Where((_,i)=>i%4==3).All(a=>a==0)&&
        surfaces.Length==1&&surfaces.All(s=>s.Material==0),"real wire snapshot removes fully empty overlay and234 transparent line texels");
    foreach(var surface in surfaces)for(int i=0;i<surface.Indices.Count;i+=3)
    {
        var a=surface.Vertices[surface.Indices[i]];var b=surface.Vertices[surface.Indices[i+1]];var c=surface.Vertices[surface.Indices[i+2]];
        int x=Math.Clamp((int)((a.U+b.U+c.U)/3*surface.Image.Width),0,surface.Image.Width-1);
        int y=Math.Clamp((int)((1-(a.V+b.V+c.V)/3)*surface.Image.Height),0,surface.Image.Height-1);
        if(surface.Image.Pixels[(y*surface.Image.Width+x)*4+3]==0)throw new Exception("Wire triangle still covers an invisible source texel");
        float ax=b.X-a.X,ay=b.Y-a.Y,az=b.Z-a.Z,bx=c.X-a.X,by=c.Y-a.Y,bz=c.Z-a.Z;
        if((ay*bz-az*by)*a.Nx+(az*bx-ax*bz)*a.Ny+(ax*by-ay*bx)*a.Nz<=0)throw new Exception("Cutout changed native face winding");
    }
    Check(wire.Collision.SequenceEqual(collision)&&wire.Outline.SequenceEqual(outline),"surface cutout leaves native wire collision/outline unchanged");
}
var offSurface=BlockSurfaceGeometry.Build(wires[0])[0];var onSurface=BlockSurfaceGeometry.Build(wires[1])[0];
var offPixels=BlockSurfaceGeometry.TintPixels(offSurface.Image,offSurface.Tint);var onPixels=BlockSurfaceGeometry.TintPixels(onSurface.Image,onSurface.Tint);
Check(offSurface.Tint==0xff4c0000&&onSurface.Tint==0xffff3200&&
    Enumerable.Range(0,256).Where(i=>offPixels[i*4+3]>0).All(i=>offPixels[i*4]>0&&offPixels[i*4+1]==0&&offPixels[i*4+2]==0&&
        onPixels[i*4]>offPixels[i*4]&&onPixels[i*4+1]>0&&onPixels[i*4+3]==offPixels[i*4+3]),
    "actual native redstone power0/15 tint is baked into original pixels without black or missing power color");
var spriteData=System.Xml.Linq.XDocument.Load("src/Mycraft/_Module/GUI/MycraftSpriteData.xml");
byte[] Icon(string name)
{
    var sprite=spriteData.Descendants("SpritePart").Single(p=>(string?)p.Element("Name")=="mycraft_item_"+name);
    int Field(string key)=>int.Parse(sprite.Element(key)!.Value);
    if(Field("Width")!=48||Field("Height")!=48)throw new Exception("Component icon changed native16px slot scale");
    var atlas=File.ReadAllBytes("src/Mycraft/_Module/GUI/SpriteSheets/MycraftHUD/MycraftHUD_"+Field("SheetID")+".rgba");
    var pixels=new byte[48*48*4];for(int y=0;y<48;y++)Buffer.BlockCopy(atlas,((Field("SheetY")+y)*2048+Field("SheetX"))*4,pixels,y*48*4,48*4);
    return pixels;
}
foreach(var component in new[]{"dispenser","dropper","lever","redstone_torch","observer","piston","sticky_piston","redstone_lamp","redstone_block",
    "daylight_detector","note_block","crafter","stone_button","oak_button","stone_pressure_plate","oak_pressure_plate","target","rail","powered_rail","detector_rail","activator_rail","tripwire_hook"})
{
    var pixels=Icon(component);int visible=pixels.Where((_,i)=>i%4==3).Count(a=>a>0);
    Check(visible>0&&visible<48*48,"real "+component+" inventory model has a registered nonempty transparent GUI silhouette");
}
Check(!Icon("dispenser").SequenceEqual(Icon("dropper"))&&!Icon("dispenser").SequenceEqual(Icon("stone")),
    "dispenser/dropper keep distinct original front textures instead of an unknown or generic stone icon");
var leverTexture=File.ReadAllBytes("tests/Building/Fixtures/lever_original.rgba");var leverIcon=Icon("lever");
for(int y=0;y<48;y++)for(int x=0;x<48;x++)
{
    int source=((y/3)*16+x/3)*4,target=(y*48+x)*4;
    if(leverIcon[target+3]!=leverTexture[source+3])throw new Exception("Lever GUI no longer follows original generated item texture");
    if(leverIcon[target+3]>0)for(int channel=0;channel<3;channel++)
        if(leverIcon[target+channel]!=leverTexture[source+channel])throw new Exception("Lever GUI changed original block-texture pixels");
}
Check(true,"lever GUI exactly reproduces item/generated using original lever block texture at3x scale");
Console.WriteLine("ALL " + checks + " BUILDING CHECKS PASSED");
if(args.Length==1)
{
    object GroundWire(float anchor,System.Collections.Generic.IReadOnlyList<GroundSupport> samples)
    {
        var packet=new System.Collections.Generic.Dictionary<string,object>
        { ["type"]="mob_ground",["pv"]=1,["sid"]="ground-regression",["seq"]=1,["x"]=.5d,["y"]=(double)anchor,["z"]=.5d,["groundCount"]=samples.Count };
        for(int i=0;i<samples.Count;i++)
        {packet["ground"+i+"X"]=samples[i].X;packet["ground"+i+"Y"]=samples[i].Y;packet["ground"+i+"Z"]=samples[i].Z;packet["ground"+i+"H"]=(double)samples[i].H;}
        return packet;
    }
    File.WriteAllText(args[0],System.Text.Json.JsonSerializer.Serialize(new
    { schema=1,airborneRaw=GroundWire(148.25f,new[]{birthFloor}),airborneFiltered=GroundWire(148.25f,airborneGround),nearFiltered=GroundWire(143.75f,nearbyGround) }));
    Console.WriteLine("ENTITY_GROUND_NATIVE_FIXTURE_EXPORTED: "+Path.GetFullPath(args[0]));
}
