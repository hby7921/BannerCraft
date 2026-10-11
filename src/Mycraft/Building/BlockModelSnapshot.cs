using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mycraft.Building;

public sealed class BlockInteractionTarget
{
    public int X, Y, Z, Face, PlaceX, PlaceY, PlaceZ, TntPlacementY;
    public float HitX, HitY, HitZ;
    // Terrain support height within its MC cell. HitY is the absolute top.
    public float SurfaceHeight=1;
    public bool Terrain;
}

public sealed class GroundSupport
{
    public int X,Y,Z;
    public float H;
    public float Top=>Y+H;
    public int TntPlacementY=>BlockGeometry.TntPlacementCell(Y,H);
}

public readonly struct BlockBox
{
    public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    public BlockBox(float minX,float minY,float minZ,float maxX,float maxY,float maxZ)
    { MinX=minX;MinY=minY;MinZ=minZ;MaxX=maxX;MaxY=maxY;MaxZ=maxZ; }
}

public readonly struct BlockVertex
{
    public readonly float X,Y,Z,Nx,Ny,Nz,U,V;
    public BlockVertex(float x,float y,float z,float nx,float ny,float nz,float u,float v)
    {X=x;Y=y;Z=z;Nx=nx;Ny=ny;Nz=nz;U=u;V=v;}
}

public sealed class BlockModelSnapshot
{
    public const int MaxBytes=4*1024*1024;
    public static readonly int[] NativeWinding={0,2,1,0,3,2};
    public sealed class Image
    {
        public int Width=0, Height=0;
        public byte[] Pixels=Array.Empty<byte>();
    }
    public sealed class Quad
    {
        public int Material;
        public uint Tint;
        public float[] Vertices=Array.Empty<float>();
        public float NormalX,NormalY,NormalZ;
        public BlockVertex NativeVertex(int index)
        {
            int p=index*5;
            return new BlockVertex(Vertices[p],Vertices[p+2],Vertices[p+1],NormalX,NormalZ,NormalY,Vertices[p+3],1-Vertices[p+4]);
        }
    }
    public Image[] Textures=Array.Empty<Image>();
    public Quad[] Quads=Array.Empty<Quad>();
    public BlockBox[] Collision=Array.Empty<BlockBox>(), Outline=Array.Empty<BlockBox>();
    public int ByteLength;

    public static bool ValidKey(string key)
    {
        if(key==null || key.Length!=32)return false;
        foreach(char c in key)if(!(c>='0'&&c<='9') && !(c>='a'&&c<='f'))return false;
        return true;
    }

    public static BlockModelSnapshot Parse(byte[] data)
    {
        if(data==null || data.Length<28 || data.Length>MaxBytes)throw new InvalidDataException("Block snapshot size is invalid");
        using(var stream=new MemoryStream(data,false))
        using(var reader=new BinaryReader(stream))
        {
            if(reader.ReadInt32()!=0x4D43424D || reader.ReadInt32()!=1)throw new InvalidDataException("Unsupported block snapshot header");
            var result=new BlockModelSnapshot { ByteLength=data.Length };
            result.Textures=new Image[Count(reader,1,64)];
            for(int i=0;i<result.Textures.Length;i++)
            {
                int width=Count(reader,1,256),height=Count(reader,1,256),length=width*height*4;
                if(stream.Length-stream.Position<length)throw new EndOfStreamException("Incomplete block texture");
                result.Textures[i]=new Image {Width=width,Height=height,Pixels=reader.ReadBytes(length)};
            }
            int count=Count(reader,1,2048);
            if(stream.Length-stream.Position<(long)count*100+8)throw new EndOfStreamException("Incomplete block quads");
            result.Quads=new Quad[count];
            for(int i=0;i<count;i++)
            {
                var quad=new Quad { Material=Count(reader,0,result.Textures.Length-1),Tint=reader.ReadUInt32(),Vertices=new float[20] };
                for(int v=0;v<4;v++)
                {
                    for(int axis=0;axis<3;axis++)quad.Vertices[v*5+axis]=Scalar(reader,-16,16);
                    for(int axis=3;axis<5;axis++)quad.Vertices[v*5+axis]=Math.Max(0,Math.Min(1,Scalar(reader,-.001f,1.001f)));
                }
                quad.NormalX=Scalar(reader,-1,1);quad.NormalY=Scalar(reader,-1,1);quad.NormalZ=Scalar(reader,-1,1);
                float length=quad.NormalX*quad.NormalX+quad.NormalY*quad.NormalY+quad.NormalZ*quad.NormalZ;
                if(length<.99f || length>1.01f)throw new InvalidDataException("Invalid block face normal");
                result.Quads[i]=quad;
            }
            result.Collision=Boxes(reader);result.Outline=Boxes(reader);
            if(stream.Position!=stream.Length)throw new InvalidDataException("Unexpected trailing block snapshot data");
            return result;
        }
    }

    // Called only by background tasks. The producer atomically renames a finished
    // snapshot, so absence can be retried without ever parsing a partially written file.
    public static async Task<BlockModelSnapshot> LoadAsync(string root,string session,string key,DateTime sessionStart,CancellationToken cancellation)
    {
        if(!ValidKey(session)||!ValidKey(key))throw new InvalidDataException("Invalid block model session/key");
        string path=Path.Combine(root,session,key+".bin");
        DateTime deadline=DateTime.UtcNow.AddSeconds(30);
        do
        {
            cancellation.ThrowIfCancellationRequested();
            string errorPath=Path.Combine(root,session,key+".error");
            if(File.Exists(errorPath))
            {
                var error=new FileInfo(errorPath);
                if(error.Length>4096 || error.LastWriteTimeUtc<sessionStart.AddSeconds(-5) || error.LastWriteTimeUtc>DateTime.UtcNow.AddSeconds(5))
                    throw new InvalidDataException("Invalid block export error snapshot");
                using(var file=new FileStream(errorPath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    if(file.Length>4096)throw new InvalidDataException("Oversized block export error");
                    using(var reader=new StreamReader(file))throw new InvalidDataException("Minecraft export: "+reader.ReadToEnd());
                }
            }
            if(File.Exists(path))
            {
                var info=new FileInfo(path);
                if(info.Length<28 || info.Length>MaxBytes)throw new InvalidDataException("Block model exceeds snapshot budget");
                if(info.LastWriteTimeUtc<sessionStart.AddSeconds(-5) || info.LastWriteTimeUtc>DateTime.UtcNow.AddSeconds(5))
                    throw new InvalidDataException("Stale block model snapshot");
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                {
                    if(stream.Length!=info.Length)throw new InvalidDataException("Block model changed during read");
                    var bytes=new byte[(int)stream.Length];int read=0;
                    while(read<bytes.Length)
                    {
                        int n=stream.Read(bytes,read,bytes.Length-read);
                        if(n==0)throw new EndOfStreamException("Incomplete block snapshot");
                        read+=n;
                    }
                    cancellation.ThrowIfCancellationRequested();
                    return Parse(bytes);
                }
            }
            await Task.Delay(100,cancellation).ConfigureAwait(false);
        } while(DateTime.UtcNow<deadline);
        throw new FileNotFoundException("Minecraft has not exported this block model (special renderers are not supported)");
    }

    private static int Count(BinaryReader reader,int min,int max)
    {
        int value=reader.ReadInt32();
        if(value<min || value>max)throw new InvalidDataException("Block snapshot count is outside its budget");
        return value;
    }
    private static float Scalar(BinaryReader reader,float min,float max)
    {
        float value=reader.ReadSingle();
        if(!BlockGeometry.Finite(value)||value<min||value>max)throw new InvalidDataException("Invalid block snapshot coordinate");
        return value;
    }
    private static BlockBox[] Boxes(BinaryReader reader)
    {
        int count=Count(reader,0,64);
        var result=new BlockBox[count];
        for(int i=0;i<count;i++)
        {
            float x0=Scalar(reader,-16,16),y0=Scalar(reader,-16,16),z0=Scalar(reader,-16,16);
            float x1=Scalar(reader,-16,16),y1=Scalar(reader,-16,16),z1=Scalar(reader,-16,16);
            if(x0>=x1 || y0>=y1 || z0>=z1)throw new InvalidDataException("Invalid block shape box");
            result[i]=new BlockBox(x0,y0,z0,x1,y1,z1);
        }
        return result;
    }
}
