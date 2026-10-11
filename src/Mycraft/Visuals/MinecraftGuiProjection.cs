using System;

namespace Mycraft.Visuals;

// Offline, dependency-free rasterizer used by the local asset builders and tests.
// GUI angles/scales come from the installed client's model JSON; this is not a
// replacement for Minecraft's dynamic tint, animation, glint or model dispatch.
public static class MinecraftGuiProjection
{
    private readonly struct Point
    {
        public readonly double X, Y, Z;
        public Point(double x, double y, double z) { X=x; Y=y; Z=z; }
    }

    public static byte[] Block(byte[] texture, float[] rotation, float[] translation, float[] scale, int size=48)
        => Render(VoxelGeometry.HeldBlock(16),texture,16,rotation,translation,scale,size,8,false,false);

    public static byte[] BlockBottomTop(byte[] atlas, float[] rotation, float[] translation, float[] scale, int size=48)
        => Render(VoxelGeometry.HeldBlockBottomTop(16),atlas,64,rotation,translation,scale,size,8,false,false);

    public static byte[] Shield(byte[] texture, float[] rotation, float[] translation, float[] scale, int size=48)
        => Render(VoxelGeometry.Shield(1),texture,64,rotation,translation,scale,size,0,true,true);

    private static byte[] Render(VoxelGeometry geometry, byte[] texture, int textureSize,
        float[] rotation, float[] translation, float[] scale, int size, float heightOffset, bool flatLight, bool fit)
    {
        if(texture.Length!=textureSize*textureSize*4 || size<1 || size>512 ||
            rotation.Length!=3 || translation.Length!=3 || scale.Length!=3)
            throw new ArgumentException("Invalid GUI projection input");
        var points=new Point[geometry.Vertices.Count];
        var minX=double.MaxValue; var minY=double.MaxValue;
        var maxX=double.MinValue; var maxY=double.MinValue;
        for(var i=0;i<points.Length;i++)
        {
            var v=geometry.Vertices[i];
            // Native X/Y/Z -> Minecraft X/Z/Y. The shield already includes its
            // special renderer's (1,-1,-1) transform and is centered at the origin.
            var p=Rotate(v.X*scale[0],(v.Z-heightOffset)*scale[1],v.Y*scale[2],rotation);
            points[i]=new Point(p.X+translation[0],p.Y+translation[1],p.Z+translation[2]);
            minX=Math.Min(minX,points[i].X); maxX=Math.Max(maxX,points[i].X);
            minY=Math.Min(minY,points[i].Y); maxY=Math.Max(maxY,points[i].Y);
        }
        // Special models can extend beyond a 16px item square. Preserve the
        // whole shield (including its handle) in our fixed-size Gauntlet sprite.
        var fitScale=fit ? Math.Min(1,15/Math.Max(maxX-minX,maxY-minY)) : 1;
        var centerX=fit ? (minX+maxX)/2 : 0;
        var centerY=fit ? (minY+maxY)/2 : 0;
        for(var i=0;i<points.Length;i++)
        {
            var p=points[i];
            points[i]=new Point((8+(p.X-centerX)*fitScale)*size/16,
                (8-(p.Y-centerY)*fitScale)*size/16,p.Z);
        }
        var output=new byte[size*size*4];
        var depth=new double[size*size];
        for(var i=0;i<depth.Length;i++)depth[i]=double.NegativeInfinity;
        for(var i=0;i<geometry.Indices.Count;i+=3)
        {
            var ia=geometry.Indices[i]; var ib=geometry.Indices[i+1]; var ic=geometry.Indices[i+2];
            var a=points[ia]; var b=points[ib]; var c=points[ic];
            var va=geometry.Vertices[ia]; var vb=geometry.Vertices[ib]; var vc=geometry.Vertices[ic];
            var normal=Rotate(va.Nx,va.Nz,va.Ny,rotation);
            if(normal.Z<=.00001)continue;
            var area=Edge(a,b,c.X,c.Y);
            if(Math.Abs(area)<.000001)continue;
            var brightness=flatLight ? Math.Min(1,.75+.25*normal.Z) : va.Nz>.5 ? 1 : Math.Abs(va.Nx)>.5 ? .6 : .8;
            var x0=Math.Max(0,(int)Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X))));
            var x1=Math.Min(size-1,(int)Math.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X))));
            var y0=Math.Max(0,(int)Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y))));
            var y1=Math.Min(size-1,(int)Math.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y))));
            for(var y=y0;y<=y1;y++)
            for(var x=x0;x<=x1;x++)
            {
                var wa=Edge(b,c,x+.5,y+.5)/area; var wb=Edge(c,a,x+.5,y+.5)/area; var wc=1-wa-wb;
                if(wa<-.00001 || wb<-.00001 || wc<-.00001)continue;
                var z=wa*a.Z+wb*b.Z+wc*c.Z; var target=y*size+x;
                if(z<depth[target])continue;
                var u=wa*va.U+wb*vb.U+wc*vc.U; var v=wa*va.V+wb*vb.V+wc*vc.V;
                var tx=Math.Max(0,Math.Min(textureSize-1,(int)(u*textureSize)));
                var ty=Math.Max(0,Math.Min(textureSize-1,(int)(v*textureSize)));
                var source=(ty*textureSize+tx)*4;
                if(texture[source+3]<128)continue;
                depth[target]=z;
                for(var channel=0;channel<3;channel++)output[target*4+channel]=(byte)Math.Round(texture[source+channel]*brightness);
                output[target*4+3]=texture[source+3];
            }
        }
        return output;
    }

    private static double Edge(Point a,Point b,double x,double y)=>(x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);
    private static Point Rotate(double x,double y,double z,float[] degrees)
    {
        // ItemTransform uses JOML rotationXYZ = Rx*Ry*Rz (column vectors).
        var rx=degrees[0]*Math.PI/180; var ry=degrees[1]*Math.PI/180; var rz=degrees[2]*Math.PI/180;
        var xx=x*Math.Cos(rz)-y*Math.Sin(rz); y=x*Math.Sin(rz)+y*Math.Cos(rz); x=xx;
        xx=x*Math.Cos(ry)+z*Math.Sin(ry); z=-x*Math.Sin(ry)+z*Math.Cos(ry); x=xx;
        var yy=y*Math.Cos(rx)-z*Math.Sin(rx); z=y*Math.Sin(rx)+z*Math.Cos(rx);
        return new Point(x,yy,z);
    }
}
