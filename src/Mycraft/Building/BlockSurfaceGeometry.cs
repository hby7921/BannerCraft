using System;
using System.Collections.Generic;
using System.Linq;

namespace Mycraft.Building;

// Render data only. Clip the original baked triangles in UV space, so holes
// work even with a native material which ignores texture alpha. Collision and
// outline boxes are never rebuilt or inferred from these visible surfaces.
public static class BlockSurfaceGeometry
{
    public sealed class Surface
    {
        public int Material;
        public uint Tint;
        public BlockModelSnapshot.Image Image=null!;
        public readonly List<BlockVertex> Vertices=new List<BlockVertex>();
        public readonly List<int> Indices=new List<int>();
    }
    private readonly struct Region
    {
        public readonly float Left,Top,Right,Bottom;
        public Region(int left,int top,int right,int bottom,int width,int height)
        {Left=left/(float)width;Top=top/(float)height;Right=right/(float)width;Bottom=bottom/(float)height;}
    }
    private readonly struct Corner
    {
        public readonly float X,Y,Z,U,V;
        public Corner(float x,float y,float z,float u,float v){X=x;Y=y;Z=z;U=u;V=v;}
        public static Corner Mix(Corner a,Corner b,float t)=>new Corner(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,
            a.Z+(b.Z-a.Z)*t,a.U+(b.U-a.U)*t,a.V+(b.V-a.V)*t);
    }

    public static Surface[] Build(BlockModelSnapshot data,int vertexLimit=65536)
    {
        var groups=new Dictionary<(int Material,uint Tint),Surface>();
        var regions=data.Textures.Select(Regions).ToArray();int total=0;
        foreach(var quad in data.Quads)
        {
            var visible=regions[quad.Material];if(visible.Count==0)continue;
            var key=(quad.Material,quad.Tint);
            if(!groups.TryGetValue(key,out var surface))
            {surface=new Surface {Material=quad.Material,Tint=quad.Tint,Image=data.Textures[quad.Material]};groups.Add(key,surface);}
            int before=surface.Vertices.Count;
            if(visible.Count==1&&visible[0].Left==0&&visible[0].Top==0&&visible[0].Right==1&&visible[0].Bottom==1)
            {
                int start=surface.Vertices.Count;
                for(int i=0;i<4;i++)surface.Vertices.Add(quad.NativeVertex(i));
                foreach(int index in BlockModelSnapshot.NativeWinding)surface.Indices.Add(start+index);
            }
            else
            {
                var points=new Corner[4];
                for(int i=0;i<4;i++)
                {var v=quad.NativeVertex(i);points[i]=new Corner(v.X,v.Y,v.Z,v.U,1-v.V);}
                foreach(var triangle in new[]{new[]{points[0],points[1],points[2]},new[]{points[0],points[2],points[3]}})
                foreach(var rectangle in visible)
                {
                    var polygon=new List<Corner>(triangle);
                    polygon=Clip(polygon,0,rectangle.Left,true);polygon=Clip(polygon,0,rectangle.Right,false);
                    polygon=Clip(polygon,1,rectangle.Top,true);polygon=Clip(polygon,1,rectangle.Bottom,false);
                    if(polygon.Count<3)continue;
                    var a=polygon[0];
                    for(int i=1;i+1<polygon.Count;i++)
                    {
                        var b=polygon[i];var c=polygon[i+1];
                        float ax=b.X-a.X,ay=b.Y-a.Y,az=b.Z-a.Z,bx=c.X-a.X,by=c.Y-a.Y,bz=c.Z-a.Z;
                        float nx=ay*bz-az*by,ny=az*bx-ax*bz,nz=ax*by-ay*bx;
                        if(nx*nx+ny*ny+nz*nz<1e-16f)continue;
                        if(total+surface.Vertices.Count-before+3>vertexLimit)throw new InvalidOperationException("Native block surface geometry budget reached");
                        int start=surface.Vertices.Count;
                        foreach(var p in new[]{a,b,c})surface.Vertices.Add(new BlockVertex(p.X,p.Y,p.Z,quad.NormalX,quad.NormalZ,quad.NormalY,p.U,1-p.V));
                        surface.Indices.Add(start);surface.Indices.Add(start+2);surface.Indices.Add(start+1);
                    }
                }
            }
            total+=surface.Vertices.Count-before;
            if(total>vertexLimit)throw new InvalidOperationException("Native block surface geometry budget reached");
        }
        return groups.Values.Where(s=>s.Vertices.Count>0).ToArray();
    }

    public static byte[] TintPixels(BlockModelSnapshot.Image image,uint tint)
    {
        var pixels=(byte[])image.Pixels.Clone();
        int r=(int)((tint>>16)&255),g=(int)((tint>>8)&255),b=(int)(tint&255);
        for(int p=0;p<pixels.Length;p+=4)
        {pixels[p]=(byte)(pixels[p]*r/255);pixels[p+1]=(byte)(pixels[p+1]*g/255);pixels[p+2]=(byte)(pixels[p+2]*b/255);}
        return pixels;
    }

    private static List<Region> Regions(BlockModelSnapshot.Image image)
    {
        var output=new List<Region>();var open=new Dictionary<(int Left,int Right),(int Top,int Bottom)>();
        for(int y=0;y<image.Height;y++)
        {
            var row=new HashSet<(int Left,int Right)>();
            for(int x=0;x<image.Width;)
            {
                if(image.Pixels[(y*image.Width+x)*4+3]==0){x++;continue;}
                int left=x;while(x<image.Width&&image.Pixels[(y*image.Width+x)*4+3]!=0)x++;
                var span=(left,x);row.Add(span);
                if(open.TryGetValue(span,out var previous))open[span]=(previous.Top,y+1);else open.Add(span,(y,y+1));
            }
            foreach(var pair in open.ToArray())if(!row.Contains(pair.Key))
            {output.Add(new Region(pair.Key.Left,pair.Value.Top,pair.Key.Right,pair.Value.Bottom,image.Width,image.Height));open.Remove(pair.Key);}
        }
        foreach(var pair in open)output.Add(new Region(pair.Key.Left,pair.Value.Top,pair.Key.Right,pair.Value.Bottom,image.Width,image.Height));
        return output;
    }

    private static List<Corner> Clip(List<Corner> points,int axis,float bound,bool lower)
    {
        if(points.Count==0)return points;
        var result=new List<Corner>(points.Count+1);var a=points[points.Count-1];float av=axis==0?a.U:a.V;
        bool aInside=lower?av>=bound:av<=bound;
        foreach(var b in points)
        {
            float bv=axis==0?b.U:b.V;bool bInside=lower?bv>=bound:bv<=bound;
            if(aInside!=bInside)result.Add(Corner.Mix(a,b,(bound-av)/(bv-av)));
            if(bInside)result.Add(b);a=b;av=bv;aInside=bInside;
        }
        return result;
    }

    // Offline GUI rendering of actual JSON-derived model faces. The item
    // model's own display.gui transform and face UVs are supplied by exporter.
    public static byte[] ProjectGui(BlockModelSnapshot data,float[] rotation,float[] translation,float[] scale,int size=48,bool flatLight=false)
    {
        if(size<1||size>512||rotation.Length!=3||translation.Length!=3||scale.Length!=3)throw new ArgumentException("Invalid GUI transform");
        var output=new byte[size*size*4];var depth=Enumerable.Repeat(double.NegativeInfinity,size*size).ToArray();
        foreach(var surface in Build(data))
        {
            var pixels=TintPixels(surface.Image,surface.Tint);var transformed=new (double X,double Y,double Z)[surface.Vertices.Count];
            for(int i=0;i<transformed.Length;i++)
            {
                var v=surface.Vertices[i];var p=Rotate((v.X-.5)*16*scale[0],(v.Z-.5)*16*scale[1],(v.Y-.5)*16*scale[2],rotation);
                transformed[i]=((8+p.X+translation[0])*size/16,(8-p.Y-translation[1])*size/16,p.Z+translation[2]);
            }
            for(int i=0;i<surface.Indices.Count;i+=3)
            {
                int ia=surface.Indices[i],ib=surface.Indices[i+1],ic=surface.Indices[i+2];
                var a=transformed[ia];var b=transformed[ib];var c=transformed[ic];
                var va=surface.Vertices[ia];var vb=surface.Vertices[ib];var vc=surface.Vertices[ic];
                var normal=Rotate(va.Nx,va.Nz,va.Ny,rotation);if(normal.Z<=.00001)continue;
                double area=Edge(a,b,c.X,c.Y);if(Math.Abs(area)<.000001)continue;
                double shade=flatLight?Math.Min(1,.75+.25*normal.Z):va.Nz>.5?1:Math.Abs(va.Nx)>.5?.6:.8;
                int minX=Math.Max(0,(int)Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))),maxX=Math.Min(size-1,(int)Math.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X))));
                int minY=Math.Max(0,(int)Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))),maxY=Math.Min(size-1,(int)Math.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y))));
                for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)
                {
                    double wa=Edge(b,c,x+.5,y+.5)/area,wb=Edge(c,a,x+.5,y+.5)/area,wc=1-wa-wb;
                    if(wa<-.00001||wb<-.00001||wc<-.00001)continue;
                    double z=wa*a.Z+wb*b.Z+wc*c.Z;int target=y*size+x;if(z<depth[target])continue;
                    double u=wa*va.U+wb*vb.U+wc*vc.U,v=1-(wa*va.V+wb*vb.V+wc*vc.V);
                    int tx=Math.Max(0,Math.Min(surface.Image.Width-1,(int)(u*surface.Image.Width)));
                    int ty=Math.Max(0,Math.Min(surface.Image.Height-1,(int)(v*surface.Image.Height))),source=(ty*surface.Image.Width+tx)*4;
                    if(pixels[source+3]==0)continue;depth[target]=z;
                    for(int channel=0;channel<3;channel++)output[target*4+channel]=(byte)Math.Round(pixels[source+channel]*shade);
                    output[target*4+3]=pixels[source+3];
                }
            }
        }
        return output;
    }
    private static double Edge((double X,double Y,double Z) a,(double X,double Y,double Z) b,double x,double y)=>(x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);
    private static (double X,double Y,double Z) Rotate(double x,double y,double z,float[] degrees)
    {
        double rx=degrees[0]*Math.PI/180,ry=degrees[1]*Math.PI/180,rz=degrees[2]*Math.PI/180;
        double next=x*Math.Cos(rz)-y*Math.Sin(rz);y=x*Math.Sin(rz)+y*Math.Cos(rz);x=next;
        next=x*Math.Cos(ry)+z*Math.Sin(ry);z=-x*Math.Sin(ry)+z*Math.Cos(ry);x=next;
        next=y*Math.Cos(rx)-z*Math.Sin(rx);z=y*Math.Sin(rx)+z*Math.Cos(rx);return (x,next,z);
    }
}
