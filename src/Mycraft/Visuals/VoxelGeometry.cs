using System;
using System.Collections.Generic;

namespace Mycraft.Visuals;

// Pure geometry data is shared by the native mesh adapter and regression checks.
public sealed class VoxelGeometry
{
    public readonly struct Vertex
    {
        public readonly float X, Y, Z, Nx, Ny, Nz, U, V;
        public Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
        { X = x; Y = y; Z = z; Nx = nx; Ny = ny; Nz = nz; U = u; V = v; }
    }
    private readonly struct Rect
    {
        public readonly float X, Y, W, H;
        public Rect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }
    public List<Vertex> Vertices { get; } = new List<Vertex>();
    public List<int> Indices { get; } = new List<int>();
    public static float NativeV(float textureRowV)=>1f-textureRowV;

    // Minecraft 26.3 ShieldModel.createLayer: plate (-6,-11,-2; 12,22,1),
    // handle (-1,-3,-1; 2,6,6), texture 64x64. X/right, Y/front, Z/up.
    // Unlike Item(), the plate is centered on X/Z. The native adapter aligns the
    // handle center (Y=-2 pixels) with the hand instead of using a sword origin.
    public static VoxelGeometry Shield(float pixelSize = .05f)
    {
        if (pixelSize <= 0) throw new ArgumentOutOfRangeException(nameof(pixelSize));
        var result = new VoxelGeometry();
        result.AddBox(0, 1.5f * pixelSize, 0, 12 * pixelSize, pixelSize, 22 * pixelSize,
            CuboidUv(0, 0, 12, 1, 22), 64, 64);
        result.AddBox(0, -2 * pixelSize, 0, 2 * pixelSize, 6 * pixelSize, 6 * pixelSize,
            CuboidUv(26, 0, 2, 6, 6), 64, 64);
        return result;
    }

    // Exact Minecraft 26.3 TridentModel.createLayer, 32x32 atlas. The held
    // version puts the middle of the shaft at the grip; flight keeps the
    // model's original root at the base of its three tines.
    public static VoxelGeometry Trident(bool held = true,float pixelSize = 1f/16)
    {
        if(pixelSize<=0)throw new ArgumentOutOfRangeException(nameof(pixelSize));
        var result=new VoxelGeometry();float grip=held?14:0;
        result.TridentBox(-.5f,2,-.5f,1,25,1,0,6,grip,pixelSize);
        result.TridentBox(-1.5f,0,-.5f,3,2,1,4,0,grip,pixelSize);
        result.TridentBox(-2.5f,-3,-.5f,1,4,1,4,3,grip,pixelSize);
        result.TridentBox(-.5f,-4,-.5f,1,4,1,0,0,grip,pixelSize);
        // The original right spike mirrors the left spike's UV and geometry.
        var right=new VoxelGeometry();right.TridentBox(-2.5f,-3,-.5f,1,4,1,4,3,grip,pixelSize);
        int start=result.Vertices.Count;
        foreach(var v in right.Vertices)result.Vertices.Add(new Vertex(-v.X,v.Y,v.Z,-v.Nx,v.Ny,v.Nz,v.U,v.V));
        for(int i=0;i<right.Indices.Count;i+=3)
        {result.Indices.Add(start+right.Indices[i]);result.Indices.Add(start+right.Indices[i+2]);result.Indices.Add(start+right.Indices[i+1]);}
        return result;
    }

    private void TridentBox(float x,float y,float z,int width,int height,int depth,int uvX,int uvY,float grip,float pixelSize)
    {
        AddBox((x+width*.5f)*pixelSize,-(z+depth*.5f)*pixelSize,(grip-y-height*.5f)*pixelSize,
            width*pixelSize,depth*pixelSize,height*pixelSize,CuboidUv(uvX,uvY,width,depth,height),32,32);
    }

    // 26.3 ArrowModel: two 16x4 crossed planes rotated 45/135 degrees,
    // one 5x5 back plane at x=-11 scaled .8, and overall root scale .9.
    // Longitudinal model X becomes local Z so ProjectileScene can aim it.
    public static VoxelGeometry Arrow(byte[]? pixels=null)
    {
        var result=new VoxelGeometry();
        foreach(float angle in new[]{(float)Math.PI/4,(float)Math.PI*3/4})
            result.ArrowPlane(-12,4,-2,2,angle,1,new Rect(16,0,16,4),new Rect(16,0,-16,4),32,32*.8f);
        float c=(float)Math.Cos(Math.PI/4),s=(float)Math.Sin(Math.PI/4),scale=.9f/16;
        var points=new float[12];float[] ys={-2.5f,2.5f,2.5f,-2.5f},zs={-2.5f,-2.5f,2.5f,2.5f};
        for(int i=0;i<4;i++)
        {points[i*3]=(ys[i]*c-zs[i]*s)*.8f*scale;points[i*3+1]=(ys[i]*s+zs[i]*c)*.8f*scale;points[i*3+2]=-11*scale;}
        result.Quad(points,0,0,1,new Rect(0,5,5,5),32,32);
        result.ReverseQuad(points,0,0,-1,new Rect(5,5,-5,5),32,32);
        return pixels==null?result:result.Cutout(pixels,32);
    }

    // Native default materials do not reliably alpha-test generated meshes.
    // Trim each original model plane to its opaque source texels, retaining
    // all positions, UV orientation, and authored 0.8 texture-height scaling.
    private VoxelGeometry Cutout(byte[] pixels,int size)
    {
        if(pixels.Length!=size*size*4)throw new ArgumentException("Invalid model cutout texture");
        var result=new VoxelGeometry();
        for(int face=0;face<Vertices.Count;face+=4)
        {
            var a=Vertices[face];var b=Vertices[face+1];var d=Vertices[face+3];
            float minU=Math.Min(a.U,b.U),maxU=Math.Max(a.U,b.U),minV=Math.Min(a.V,d.V),maxV=Math.Max(a.V,d.V);
            int x0=Math.Max(0,(int)Math.Floor(minU*size)),x1=Math.Min(size,(int)Math.Ceiling(maxU*size));
            int y0=Math.Max(0,(int)Math.Floor(minV*size)),y1=Math.Min(size,(int)Math.Ceiling(maxV*size));
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
            {
                if(pixels[(y*size+x)*4+3]<128)continue;
                float left=Math.Max(minU,x/(float)size),right=Math.Min(maxU,(x+1f)/size);
                float top=Math.Max(minV,y/(float)size),bottom=Math.Min(maxV,(y+1f)/size);
                if(right-left<.000001f||bottom-top<.000001f)continue;
                float u0=a.U<b.U?left:right,u1=a.U<b.U?right:left;
                float v0=a.V<d.V?top:bottom,v1=a.V<d.V?bottom:top;
                float[] us={u0,u1,u1,u0},vs={v0,v0,v1,v1};var points=new float[12];
                for(int i=0;i<4;i++)
                {
                    float u=(us[i]-a.U)/(b.U-a.U),v=(vs[i]-a.V)/(d.V-a.V);
                    points[i*3]=a.X+(b.X-a.X)*u+(d.X-a.X)*v;
                    points[i*3+1]=a.Y+(b.Y-a.Y)*u+(d.Y-a.Y)*v;
                    points[i*3+2]=a.Z+(b.Z-a.Z)*u+(d.Z-a.Z)*v;
                }
                result.Quad(points,a.Nx,a.Ny,a.Nz,new Rect(u0*size,v1*size,(u1-u0)*size,(v0-v1)*size),size,size);
            }
        }
        return result;
    }

    private void ArrowPlane(float tail,float nose,float lower,float upper,float angle,float widthScale,Rect front,Rect back,float tw,float th)
    {
        float c=(float)Math.Cos(angle),s=(float)Math.Sin(angle),scale=.9f/16;
        var points=new[]{lower*c*widthScale*scale,lower*s*widthScale*scale,tail*scale,
            lower*c*widthScale*scale,lower*s*widthScale*scale,nose*scale,
            upper*c*widthScale*scale,upper*s*widthScale*scale,nose*scale,
            upper*c*widthScale*scale,upper*s*widthScale*scale,tail*scale};
        Quad(points,-s,c,0,front,tw,th);ReverseQuad(points,s,-c,0,back,tw,th);
    }

    private void ReverseQuad(float[] points,float nx,float ny,float nz,Rect uv,float tw,float th)
    {
        var reversed=new float[12];
        for(int i=0;i<4;i++)for(int axis=0;axis<3;axis++)reversed[i*3+axis]=points[(3-i)*3+axis];
        Quad(reversed,nx,ny,nz,uv,tw,th);
    }

    private static Rect[] CuboidUv(int x, int y, int width, int depth, int height)
    {
        return new[]
        {
            new Rect(x + depth * 2 + width, y + depth, -depth, height),
            new Rect(x + depth, y + depth, -depth, height),
            new Rect(x + depth + width, y + depth, -width, height),
            new Rect(x + depth * 2 + width * 2, y + depth, -width, height),
            new Rect(x + depth, y + depth, width, -depth),
            new Rect(x + depth + width, y, width, depth)
        };
    }

    public static VoxelGeometry HeldBlock(float size = .32f)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var geometry = new VoxelGeometry();
        var face = new Rect(0, 0, 16, 16);
        geometry.AddBox(0, 0, size * .5f, size, size, size,
            new[] { face, face, face, face, face, face }, 16, 16);
        return geometry;
    }

    // cube_bottom_top (Minecraft's TNT parent): the four sides share one image,
    // while UP and DOWN use independent images. Three 18x18 padded tiles are
    // laid across a 64x64 atlas; authored 16x16 pixels are never rescaled/cropped.
    public static byte[] CubeBottomTopAtlas(byte[] side, byte[] top, byte[] bottom)
    {
        var atlas = new byte[64 * 64 * 4];
        var faces = new[] { side, top, bottom };
        for (int tile = 0; tile < faces.Length; tile++)
        {
            if (faces[tile] == null || faces[tile].Length != 1024) throw new ArgumentException("Expected three 16x16 RGBA cube textures");
            for (int y = 0; y < 18; y++)
            for (int x = 0; x < 18; x++)
            {
                int sx = Math.Max(0, Math.Min(15, x - 1)), sy = Math.Max(0, Math.Min(15, y - 1));
                Buffer.BlockCopy(faces[tile], (sy * 16 + sx) * 4, atlas, (y * 64 + tile * 18 + x) * 4, 4);
            }
        }
        return atlas;
    }

    public static VoxelGeometry HeldBlockBottomTop(float size = .32f)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var geometry = new VoxelGeometry();
        // Preserve Minecraft FaceInfo/FaceBakery's UV orientation after the
        // MC (x,y,z) -> native (x,z,y) reflection, including readable side text.
        var side = new Rect(17, 1, -16, 16);
        var top = new Rect(19, 17, 16, -16);
        var bottom = new Rect(37, 17, 16, -16);
        geometry.AddBox(0, 0, size * .5f, size, size, size,
            new[] { side, side, side, side, top, bottom }, 64, 64);
        return geometry;
    }

    public static VoxelGeometry SkinBox(float width, float depth, float height, int uvX, int uvY,
        int pixelWidth, int pixelDepth, int pixelHeight, int fromRow = 0, int rows = 0)
    {
        if (width <= 0 || depth <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rows == 0) rows = pixelHeight;
        if (fromRow < 0 || rows <= 0 || fromRow + rows > pixelHeight) throw new ArgumentOutOfRangeException(nameof(rows));
        var result = new VoxelGeometry();
        // MC cuboid UV layout: top, bottom, right, front, left, back.
        var faces = new[]
        {
            new Rect(uvX + pixelDepth + pixelWidth, uvY + pixelDepth + fromRow, pixelDepth, rows),
            new Rect(uvX, uvY + pixelDepth + fromRow, pixelDepth, rows),
            new Rect(uvX + pixelDepth, uvY + pixelDepth + fromRow, pixelWidth, rows),
            new Rect(uvX + pixelDepth * 2 + pixelWidth, uvY + pixelDepth + fromRow, pixelWidth, rows),
            new Rect(uvX + pixelDepth, uvY, pixelWidth, pixelDepth),
            new Rect(uvX + pixelDepth + pixelWidth, uvY, pixelWidth, pixelDepth)
        };
        result.AddBox(0, 0, 0, width, depth, height, faces, 64, 64);
        return result;
    }

    public static VoxelGeometry Item(byte[] pixels, float size = 0.8f,int textureSize=16,float gripX=2,float gripY=13,float angle=0)
    {
        if ((textureSize!=16&&textureSize!=32)||pixels.Length != textureSize * textureSize * 4 || size <= 0) throw new ArgumentException("Expected a 16x16 or 32x32 RGBA item");
        var result = new VoxelGeometry();
        var step = size / textureSize;
        for (var y = 0; y < textureSize; y++)
        for (var x = 0; x < textureSize; x++)
        {
            if (pixels[(y * textureSize + x) * 4 + 3] < 128) continue;
            var uv = new Rect(x + 0.49f, y + 0.49f, 0.02f, 0.02f);
            var faces = new[] { uv, uv, uv, uv, uv, uv };
            // Item plane is X/Z; the lower-left handle meets the native item bone.
            result.AddBox((x - gripX) * step, 0, (gripY - y) * step, step, step * 0.6f, step, faces, textureSize, textureSize);
        }
        if(angle!=0)
        {
            float c=(float)Math.Cos(angle),s=(float)Math.Sin(angle);
            for(int i=0;i<result.Vertices.Count;i++)
            {
                var v=result.Vertices[i];result.Vertices[i]=new Vertex(c*v.X+s*v.Z,v.Y,-s*v.X+c*v.Z,
                    c*v.Nx+s*v.Nz,v.Ny,-s*v.Nx+c*v.Nz,v.U,v.V);
            }
        }
        return result;
    }

    private void AddBox(float cx, float cy, float cz, float w, float d, float h, Rect[] uv, int tw, int th)
    {
        var x0 = cx - w / 2; var x1 = cx + w / 2;
        var y0 = cy - d / 2; var y1 = cy + d / 2;
        var z0 = cz - h / 2; var z1 = cz + h / 2;
        Quad(new[] { x1,y0,z0, x1,y1,z0, x1,y1,z1, x1,y0,z1 }, 1,0,0, uv[0],tw,th);
        Quad(new[] { x0,y1,z0, x0,y0,z0, x0,y0,z1, x0,y1,z1 }, -1,0,0, uv[1],tw,th);
        Quad(new[] { x1,y1,z0, x0,y1,z0, x0,y1,z1, x1,y1,z1 }, 0,1,0, uv[2],tw,th);
        Quad(new[] { x0,y0,z0, x1,y0,z0, x1,y0,z1, x0,y0,z1 }, 0,-1,0, uv[3],tw,th);
        Quad(new[] { x0,y0,z1, x1,y0,z1, x1,y1,z1, x0,y1,z1 }, 0,0,1, uv[4],tw,th);
        Quad(new[] { x0,y1,z0, x1,y1,z0, x1,y0,z0, x0,y0,z0 }, 0,0,-1, uv[5],tw,th);
    }

    private void Quad(float[] p, float nx, float ny, float nz, Rect uv, float tw, float th)
    {
        var start = Vertices.Count;
        for (var i = 0; i < 4; i++)
        {
            var u = i == 0 || i == 3 ? uv.X : uv.X + uv.W;
            var v = i < 2 ? uv.Y + uv.H : uv.Y;
            Vertices.Add(new Vertex(p[i*3],p[i*3+1],p[i*3+2],nx,ny,nz,u/tw,v/th));
        }
        Indices.Add(start); Indices.Add(start+1); Indices.Add(start+2);
        Indices.Add(start); Indices.Add(start+2); Indices.Add(start+3);
    }
}
