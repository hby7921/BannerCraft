using System;
using Mycraft.Building;

namespace Mycraft.Demo;

// Vanilla renderer timing, independent of native scene APIs for regression tests.
public static class TntSceneTiming
{
    public const float VisualSpreadScale=.5f,VisualQuadScale=.5f;
    // Host presentation only. The original MC blast radius/damage receipt is
    // never rewritten by these renderer sizing helpers.
    public static float ExplosionVisualSpread(float blastRadius)=>blastRadius*VisualSpreadScale;
    public static float ExplosionVisualQuadSize(int emissionTick)=>
        4*(1-Math.Max(0,Math.Min(7,emissionTick))/8f*.5f)*VisualQuadScale;
    public static float Remaining(int fuse,float updateAge)=>Math.Max(0,fuse+1-Math.Min(Math.Max(updateAge,0),.25f)*20);
    public static bool White(float remaining)=>remaining>=0&&(int)(remaining/5)%2==0;
    public static float Scale(float remaining)
    {
        float growth=Math.Max(0,Math.Min(1,1-remaining/10));growth*=growth;growth*=growth;
        return 1+.3f*growth;
    }
    public static int ParticleFrame(int age,int lifetime,int frames)=>Math.Max(0,Math.Min(lifetime,age))*(frames-1)/Math.Max(1,lifetime);
    public static bool ParticleAlive(int age,int lifetime)=>age<=lifetime;
    public static bool CanSpawnParticle(bool smoke,int smokeCount,int explosionCount)=>
        smokeCount>=0&&explosionCount>=0&&smokeCount<=128&&explosionCount<=256&&(smoke?smokeCount<128:explosionCount<256);
    public static int ExplosionFrame(float age,float lifetime)=>ParticleFrame((int)Math.Floor(age*20+.000001f),Math.Max(1,(int)Math.Round(lifetime*20)),16);
    public static float SmokeGrowth(int age,float partialTick,int lifetime)=>Math.Max(0,Math.Min(1,(age+partialTick)/Math.Max(1,lifetime)*32));
    public const float SmokeFriction=.96f,SmokeRisePerTick=.004f;
    public static byte[] WhiteOverlay(byte[] source)
    {
        if(source==null||source.Length%4!=0)throw new ArgumentException("RGBA texture required");
        var result=(byte[])source.Clone();
        // OverlayTexture white texel (u=15,v=10) has alpha63. The entity shader
        // mixes white with the original textured RGB using that alpha.
        for(int i=0;i<result.Length;i+=4)for(int c=0;c<3;c++)result[i+c]=(byte)((source[i+c]*63+255*192+127)/255);
        return result;
    }
    public static TntSmokeSample SmokeSample(Random random)
    {
        float Next()=>(float)random.NextDouble();
        Next(); // Particle's initial lifetime is replaced by BaseAshSmokeParticle.
        float x=(Next()*2-1)*.4f,y=(Next()*2-1)*.4f,z=(Next()*2-1)*.4f;
        float speed=(Next()+Next()+1)*.15f,length=(float)Math.Sqrt(x*x+y*y+z*z);
        float scale=length>1e-8f?speed*.4f/length:0;
        float vx=x*scale*.1f,vy=(y*scale+.1f)*.1f,vz=z*scale*.1f;
        float width=.1f*(Next()*.5f+.5f)*2*.75f*2;
        float shade=Next()*.3f;
        int life=Math.Max(1,(int)(8/(Next()*.8+.2)));
        return new TntSmokeSample(vx,vy,vz,width,shade,life);
    }
    public static bool ValidPosition(float x,float y,float z)=>BlockGeometry.Finite(x)&&BlockGeometry.Finite(y)&&BlockGeometry.Finite(z)&&
        Math.Abs(x)<=30000000&&Math.Abs(z)<=30000000&&Math.Abs(y)<=100000;
    public static bool ValidId(string id)
    {
        if(string.IsNullOrEmpty(id)||id.Length>96)return false;
        foreach(char c in id)if(!(c>='a'&&c<='z')&&!(c>='A'&&c<='Z')&&!(c>='0'&&c<='9')&&c!='-'&&c!='_'&&c!=':')return false;
        return true;
    }
}

public readonly struct TntSmokeSample
{
    public readonly float X,Y,Z,Width,Shade;
    public readonly int Lifetime;
    public TntSmokeSample(float x,float y,float z,float width,float shade,int lifetime)
    {X=x;Y=y;Z=z;Width=width;Shade=shade;Lifetime=lifetime;}
}

public sealed class TntParticleClock
{
    private double _remainder;
    public float PartialTick=>(float)(_remainder/.05);
    public int Advance(float seconds)
    {
        if(!BlockGeometry.Finite(seconds)||seconds<0)throw new ArgumentException("Invalid particle time");
        _remainder+=Math.Min(seconds,.25f);
        int steps=(int)Math.Floor((_remainder+1e-9)/.05);
        _remainder=Math.Max(0,_remainder-steps*.05);
        return steps;
    }
}

public sealed class TntSceneMotion
{
    public float X,Y,Z,FromX,FromY,FromZ,Age;
    public int Fuse;
    public bool Initialized,Alive;
    public bool Apply(float x,float y,float z,int fuse,bool alive)
    {
        if(!TntSceneTiming.ValidPosition(x,y,z)||fuse<0||fuse>32767)return false;
        if(Initialized&&!Alive)return false;
        // Coalesced state packets may arrive late: fuse time cannot rewind.
        if(Initialized&&alive&&fuse>Fuse)return false;
        Sample(out FromX,out FromY,out FromZ);
        if(!Initialized){FromX=x;FromY=y;FromZ=z;}
        X=x;Y=y;Z=z;Fuse=fuse;Age=0;Initialized=true;Alive=alive;return true;
    }
    public void Advance(float dt){if(BlockGeometry.Finite(dt)&&dt>0)Age+=Math.Min(dt,.25f);}
    public void Sample(out float x,out float y,out float z)
    {
        float blend=Math.Min(1,Age/.05f);
        x=FromX+(X-FromX)*blend;y=FromY+(Y-FromY)*blend;z=FromZ+(Z-FromZ)*blend;
    }
}
