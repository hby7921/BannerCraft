using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mycraft.Visuals;

// Port of Minecraft26.3 LightningBoltRenderer's seeded custom geometry.
// Coordinates are native (MCx,MCz,MCy); rendering phase remains server-owned.
public sealed class LightningGeometry
{
    public const float Red=.45f,Green=.45f,Blue=.5f,Alpha=.3f;
    public const uint NativeColor=0x4c72727f;
    public readonly struct Vertex
    {
        public readonly float X,Y,Z,Nx,Ny,Nz;
        public Vertex(float x,float y,float z,float nx,float ny,float nz)
        {X=x;Y=y;Z=z;Nx=nx;Ny=ny;Nz=nz;}
    }
    public readonly struct Face
    {
        public readonly int Layer,Branch,Segment,FirstVertex;
        public Face(int layer,int branch,int segment,int firstVertex)
        {Layer=layer;Branch=branch;Segment=segment;FirstVertex=firstVertex;}
    }
    public List<Vertex> Vertices {get;}=new List<Vertex>(896);
    public List<int> Indices {get;}=new List<int>(1344);
    public List<Face> Faces {get;}=new List<Face>(224);

    public static bool TrySeed(string text,out ulong seed)
    {
        seed=0;
        if(text==null||text.Length!=16)return false;
        foreach(char c in text)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f')&&!(c>='A'&&c<='F'))return false;
        return ulong.TryParse(text,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out seed);
    }

    public static LightningGeometry Build(ulong seed)
    {
        var result=new LightningGeometry();var xs=new float[8];var zs=new float[8];
        float totalX=0,totalZ=0;var random=new LegacyRandom(seed);
        for(int segment=7;segment>=0;segment--)
        {
            xs[segment]=totalX;zs[segment]=totalZ;
            totalX+=random.NextInt(11)-5;totalZ+=random.NextInt(11)-5;
        }
        for(int layer=0;layer<4;layer++)
        {
            // Each halo layer reuses exactly the same branch centers.
            random=new LegacyRandom(seed);
            for(int branch=0;branch<3;branch++)
            {
                bool main=branch==0;int start=7-branch,end=main?0:start-2;
                float x=xs[start]-totalX,z=zs[start]-totalZ;
                for(int segment=start;segment>=end;segment--)
                {
                    float topX=x,topZ=z;
                    x+=random.NextInt(main?11:31)-(main?5:15);
                    z+=random.NextInt(main?11:31)-(main?5:15);
                    float topWidth=.1f+layer*.2f,bottomWidth=topWidth;
                    if(main){topWidth*=segment*.1f+1;bottomWidth*=(segment-1)*.1f+1;}
                    result.Quad(layer,branch,segment,x,z,topX,topZ,topWidth,bottomWidth,false,false,true,false);
                    result.Quad(layer,branch,segment,x,z,topX,topZ,topWidth,bottomWidth,true,false,true,true);
                    result.Quad(layer,branch,segment,x,z,topX,topZ,topWidth,bottomWidth,true,true,false,true);
                    result.Quad(layer,branch,segment,x,z,topX,topZ,topWidth,bottomWidth,false,true,false,false);
                }
            }
        }
        return result;
    }

    private void Quad(int layer,int branch,int segment,float bottomX,float bottomZ,float topX,float topZ,
        float topWidth,float bottomWidth,bool firstX,bool firstZ,bool secondX,bool secondZ)
    {
        var points=new[]{
            bottomX+(firstX?bottomWidth:-bottomWidth),bottomZ+(firstZ?bottomWidth:-bottomWidth),segment*16f,
            topX+(firstX?topWidth:-topWidth),topZ+(firstZ?topWidth:-topWidth),(segment+1)*16f,
            topX+(secondX?topWidth:-topWidth),topZ+(secondZ?topWidth:-topWidth),(segment+1)*16f,
            bottomX+(secondX?bottomWidth:-bottomWidth),bottomZ+(secondZ?bottomWidth:-bottomWidth),segment*16f};
        // MC->native swaps Y/Z, reversing handedness. Reverse triangle winding
        // so the tube's four side faces still point outward after conversion.
        float ax=points[6]-points[0],ay=points[7]-points[1],az=points[8]-points[2];
        float bx=points[3]-points[0],by=points[4]-points[1],bz=points[5]-points[2];
        float nx=ay*bz-az*by,ny=az*bx-ax*bz,nz=ax*by-ay*bx;
        float length=(float)Math.Sqrt(nx*nx+ny*ny+nz*nz);nx/=length;ny/=length;nz/=length;
        int start=Vertices.Count;Faces.Add(new Face(layer,branch,segment,start));
        for(int i=0;i<4;i++)Vertices.Add(new Vertex(points[i*3],points[i*3+1],points[i*3+2],nx,ny,nz));
        Indices.Add(start);Indices.Add(start+2);Indices.Add(start+1);
        Indices.Add(start);Indices.Add(start+3);Indices.Add(start+2);
    }

    private sealed class LegacyRandom
    {
        private const ulong Multiplier=25214903917UL,Mask=281474976710655UL;
        private ulong _state;
        public LegacyRandom(ulong seed){_state=(seed^Multiplier)&Mask;}
        private int Next(int bits){_state=unchecked(_state*Multiplier+11)&Mask;return (int)(_state>>(48-bits));}
        public int NextInt(int bound)
        {
            if((bound&-bound)==bound)return (int)(((long)bound*Next(31))>>31);
            int bits,value;
            do {bits=Next(31);value=bits%bound;}while(unchecked(bits-value+bound-1)<0);
            return value;
        }
    }
}
