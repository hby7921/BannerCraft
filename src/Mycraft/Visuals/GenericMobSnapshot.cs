using System;
using System.IO;
using System.Text;

namespace Mycraft.Visuals;

// Pure, bounded local wire readers. EMA1 is immutable original geometry/pixels;
// EMP1 contains the exact native renderer's current affine part transforms.
public sealed class GenericMobAsset
{
    public sealed class Texture { public int Width,Height; public byte[] Pixels=Array.Empty<byte>(); }
    public sealed class Part { public int Material; public VoxelGeometry Geometry=new VoxelGeometry(); }
    public Texture[] Textures=Array.Empty<Texture>();
    public Part[] Parts=Array.Empty<Part>();
    public static GenericMobAsset Read(Stream stream)
    {
        if(stream.Length>4*1024*1024)throw new FormatException("Native entity asset exceeds budget");
        using(var r=new BinaryReader(stream,Encoding.UTF8,true))
        {
            if(r.ReadInt32()!=0x31414d45)throw new FormatException("Invalid native entity asset header");
            int textures=Count(r,1,8,"texture count");var asset=new GenericMobAsset {Textures=new Texture[textures]};
            for(int i=0;i<textures;i++)
            {
                int width=Count(r,1,256,"texture width"),height=Count(r,1,256,"texture height");var bytes=r.ReadBytes(width*height*4);
                if(bytes.Length!=width*height*4)throw new FormatException("Incomplete native entity pixels");
                asset.Textures[i]=new Texture {Width=width,Height=height,Pixels=bytes};
            }
            int count=Count(r,1,256,"part count"),vertices=0;asset.Parts=new Part[count];
            for(int i=0;i<count;i++)
            {
                int material=Count(r,0,textures-1,"material index"),faces=Count(r,0,4096,"polygon count");vertices+=faces*4;
                if(vertices>16384)throw new FormatException("Native entity vertex budget exceeded");var p=new Part {Material=material};
                for(int f=0;f<faces;f++)
                {
                    int start=p.Geometry.Vertices.Count;
                    for(int j=0;j<4;j++)
                    {
                        float x=Scalar(r),y=Scalar(r),z=Scalar(r),nx=Scalar(r),ny=Scalar(r),nz=Scalar(r),u=Scalar(r),v=Scalar(r);
                        p.Geometry.Vertices.Add(new VoxelGeometry.Vertex(x,z,y,nx,nz,ny,u,v));
                    }
                    p.Geometry.Indices.Add(start);p.Geometry.Indices.Add(start+2);p.Geometry.Indices.Add(start+1);
                    p.Geometry.Indices.Add(start);p.Geometry.Indices.Add(start+3);p.Geometry.Indices.Add(start+2);
                }
                asset.Parts[i]=p;
            }
            if(stream.Position!=stream.Length)throw new FormatException("Trailing native entity asset bytes");return asset;
        }
    }
    internal static int Count(BinaryReader r,int min,int max,string label){int n=r.ReadInt32();if(n<min||n>max)throw new FormatException("Invalid native entity "+label);return n;}
    internal static float Scalar(BinaryReader r){float f=r.ReadSingle();if(!EntityPose.IsFinite(f)||Math.Abs(f)>65536)throw new FormatException("Invalid native entity scalar");return f;}
    public static VoxelGeometry Cutout(VoxelGeometry source,Texture texture)
    {
        var result=new VoxelGeometry();
        if(texture.Width<1||texture.Height<1||texture.Pixels.Length!=texture.Width*texture.Height*4||source.Vertices.Count%4!=0)throw new FormatException("Invalid native entity alpha source");
        // Unlike ModelPart polygons, baked item quads can rotate their UV axes. Solve the
        // actual two-dimensional UV basis rather than assuming vertices 0/1 are horizontal.
        for(int i=0;i<source.Vertices.Count;i+=4)
        {
            var a=source.Vertices[i];var b=source.Vertices[i+1];var c=source.Vertices[i+2];var d=source.Vertices[i+3];
            float bx=b.X-a.X,by=b.Y-a.Y,bz=b.Z-a.Z,dx=d.X-a.X,dy=d.Y-a.Y,dz=d.Z-a.Z;
            float cx=by*dz-bz*dy,cy=bz*dx-bx*dz,cz=bx*dy-by*dx;
            // Original zero-thickness cubes contain zero-area edge polygons; the native
            // compiler emits them but they cover no pixels and need no host triangle.
            if(cx*cx+cy*cy+cz*cz<.000000000001f)continue;
            float minU=Math.Min(Math.Min(a.U,b.U),Math.Min(c.U,d.U)),maxU=Math.Max(Math.Max(a.U,b.U),Math.Max(c.U,d.U));
            float minV=Math.Min(Math.Min(a.V,b.V),Math.Min(c.V,d.V)),maxV=Math.Max(Math.Max(a.V,b.V),Math.Max(c.V,d.V));
            float du=b.U-a.U,dv=b.V-a.V,eu=d.U-a.U,ev=d.V-a.V,det=du*ev-dv*eu;
            if(Math.Abs(det)<.00000001f){int px=Math.Max(0,Math.Min(texture.Width-1,(int)(a.U*texture.Width))),py=Math.Max(0,Math.Min(texture.Height-1,(int)(a.V*texture.Height)));if(texture.Pixels[(py*texture.Width+px)*4+3]>=128)Copy(source,i,result);continue;}
            int x0=Math.Max(0,(int)Math.Floor(minU*texture.Width)),x1=Math.Min(texture.Width,(int)Math.Ceiling(maxU*texture.Width));
            int y0=Math.Max(0,(int)Math.Floor(minV*texture.Height)),y1=Math.Min(texture.Height,(int)Math.Ceiling(maxV*texture.Height));
            bool opaque=true;for(int y=y0;y<y1&&opaque;y++)for(int x=x0;x<x1;x++)if(texture.Pixels[(y*texture.Width+x)*4+3]<128){opaque=false;break;}
            if(opaque){Copy(source,i,result);continue;}
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;)
            {
                if(texture.Pixels[(y*texture.Width+x)*4+3]<128){x++;continue;}
                int startX=x;while(x<x1&&texture.Pixels[(y*texture.Width+x)*4+3]>=128)x++;
                float u0=Math.Max(minU,startX/(float)texture.Width),u1=Math.Min(maxU,x/(float)texture.Width),v0=Math.Max(minV,y/(float)texture.Height),v1=Math.Min(maxV,(y+1f)/texture.Height);
                if(u1<=u0||v1<=v0)continue;
                if(det<0){float swap=v0;v0=v1;v1=swap;}
                int offset=result.Vertices.Count;
                Add(result,a,b,d,du,dv,eu,ev,det,u0,v0);Add(result,a,b,d,du,dv,eu,ev,det,u1,v0);Add(result,a,b,d,du,dv,eu,ev,det,u1,v1);Add(result,a,b,d,du,dv,eu,ev,det,u0,v1);Indices(result,offset);
                if(result.Vertices.Count>65536)throw new FormatException("Native entity alpha geometry budget exceeded");
            }
        }
        return result;
    }
    private static void Add(VoxelGeometry geometry,VoxelGeometry.Vertex a,VoxelGeometry.Vertex b,VoxelGeometry.Vertex d,float du,float dv,float eu,float ev,float det,float u,float v)
    {
        float s=((u-a.U)*ev-(v-a.V)*eu)/det,t=(du*(v-a.V)-dv*(u-a.U))/det;
        geometry.Vertices.Add(new VoxelGeometry.Vertex(a.X+(b.X-a.X)*s+(d.X-a.X)*t,a.Y+(b.Y-a.Y)*s+(d.Y-a.Y)*t,a.Z+(b.Z-a.Z)*s+(d.Z-a.Z)*t,a.Nx,a.Ny,a.Nz,u,v));
    }
    private static void Copy(VoxelGeometry source,int index,VoxelGeometry target){int start=target.Vertices.Count;for(int i=0;i<4;i++)target.Vertices.Add(source.Vertices[index+i]);Indices(target,start);if(target.Vertices.Count>65536)throw new FormatException("Native entity alpha geometry budget exceeded");}
    private static void Indices(VoxelGeometry geometry,int start){geometry.Indices.Add(start);geometry.Indices.Add(start+2);geometry.Indices.Add(start+1);geometry.Indices.Add(start);geometry.Indices.Add(start+3);geometry.Indices.Add(start+2);}
}

public sealed class GenericMobPose
{
    public string Appearance="";
    public long Sequence;
    public EntityMatrix[] Frames=Array.Empty<EntityMatrix>();
    public bool[] Visible=Array.Empty<bool>();
    public uint[] Colors=Array.Empty<uint>();
    public static bool IsKey(string key)
    {if(key==null||key.Length!=32)return false;foreach(char c in key)if(c<'0'||c>'9'&&c<'a'||c>'f')return false;return true;}
    public static GenericMobPose Read(Stream stream)
    {
        if(stream.Length>14400)throw new FormatException("Native entity pose exceeds budget");
        using(var r=new BinaryReader(stream,Encoding.UTF8,true))
        {
            if(r.ReadInt32()!=0x31504d45)throw new FormatException("Invalid native entity pose header");
            string appearance=Encoding.ASCII.GetString(r.ReadBytes(32));if(!IsKey(appearance))throw new FormatException("Invalid native entity appearance key");
            long sequence=r.ReadInt64();if(sequence<1)throw new FormatException("Invalid native entity pose sequence");
            int count=GenericMobAsset.Count(r,1,256,"pose count");var pose=new GenericMobPose {Appearance=appearance,Sequence=sequence,Frames=new EntityMatrix[count],Visible=new bool[count],Colors=new uint[count]};
            for(int i=0;i<count;i++)
            {
                pose.Visible[i]=GenericMobAsset.Count(r,0,1,"visibility")!=0;pose.Colors[i]=r.ReadUInt32();
                var matrix=new float[12];for(int j=0;j<12;j++)matrix[j]=GenericMobAsset.Scalar(r);pose.Frames[i]=new EntityMatrix(matrix);
            }
            if(stream.Position!=stream.Length)throw new FormatException("Trailing native entity pose bytes");return pose;
        }
    }
}

// Pure diagnostic measurement of the same original alpha-clipped geometry and
// affine basis. Bounds describe world geometry relative to the supplied origin;
// they do not establish that a camera has rendered those pixels.
public sealed class GenericMobPresentation
{
    public int RenderableParts,VisibleParts,AlphaZeroParts,VisibleVertices;
    public readonly float[] Minimum={float.MaxValue,float.MaxValue,float.MaxValue};
    public readonly float[] Maximum={-float.MaxValue,-float.MaxValue,-float.MaxValue};
    public bool GeometryReady=>RenderableParts>0;
    public bool HasVisibleBounds=>VisibleVertices>0;
    public static GenericMobPresentation Measure(GenericMobAsset asset,GenericMobPose pose,float scale=1)
    {
        if(asset.Parts.Length!=pose.Frames.Length||pose.Visible.Length!=pose.Frames.Length||pose.Colors.Length!=pose.Frames.Length)
            throw new FormatException("Native entity diagnostic asset/pose part mismatch");
        if(float.IsNaN(scale)||float.IsInfinity(scale)||scale<=0||scale>10)throw new FormatException("Invalid native entity diagnostic scale");
        var result=new GenericMobPresentation();
        for(int i=0;i<asset.Parts.Length;i++)
        {
            var part=asset.Parts[i];var geometry=GenericMobAsset.Cutout(part.Geometry,asset.Textures[part.Material]);
            if(geometry.Indices.Count==0)continue;
            result.RenderableParts++;
            if(!pose.Visible[i])continue;
            result.VisibleParts++;if((pose.Colors[i]>>24)==0)result.AlphaZeroParts++;
            var m=pose.Frames[i].HostValues(scale);
            foreach(var vertex in geometry.Vertices)
            {
                float x=m[0]*vertex.X+m[1]*vertex.Y+m[2]*vertex.Z+m[3],y=m[4]*vertex.X+m[5]*vertex.Y+m[6]*vertex.Z+m[7],z=m[8]*vertex.X+m[9]*vertex.Y+m[10]*vertex.Z+m[11];
                result.Minimum[0]=Math.Min(result.Minimum[0],x);result.Minimum[1]=Math.Min(result.Minimum[1],y);result.Minimum[2]=Math.Min(result.Minimum[2],z);
                result.Maximum[0]=Math.Max(result.Maximum[0],x);result.Maximum[1]=Math.Max(result.Maximum[1],y);result.Maximum[2]=Math.Max(result.Maximum[2],z);
                result.VisibleVertices++;
            }
        }
        return result;
    }
}
