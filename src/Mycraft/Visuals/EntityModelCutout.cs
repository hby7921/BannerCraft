using System;

namespace Mycraft.Visuals;

public static class EntityModelCutout
{
    // Clip baked cube faces against original alpha texels. This is necessary
    // because Bannerlord's generated-mesh default shader has no alpha test.
    public static VoxelGeometry Prepare(VoxelGeometry source,byte[] rgba,int width,int height)
    {
        if(rgba.Length!=width*height*4||width<=0||height<=0||source.Vertices.Count%4!=0)throw new ArgumentException("Invalid native entity cutout source");
        var output=new VoxelGeometry();
        for(int f=0;f<source.Vertices.Count;f+=4)
        {
            var a=source.Vertices[f];var b=source.Vertices[f+1];var d=source.Vertices[f+3];
            float minU=Math.Min(a.U,b.U),maxU=Math.Max(a.U,b.U),minV=Math.Min(a.V,d.V),maxV=Math.Max(a.V,d.V);
            int x0=Math.Max(0,(int)Math.Floor(minU*width)),x1=Math.Min(width,(int)Math.Ceiling(maxU*width));
            int y0=Math.Max(0,(int)Math.Floor(minV*height)),y1=Math.Min(height,(int)Math.Ceiling(maxV*height));
            bool all=true;for(int y=y0;y<y1&&all;y++)for(int x=x0;x<x1;x++)if(rgba[(y*width+x)*4+3]<128){all=false;break;}
            if(all){CopyFace(source,f,output);continue;}
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;)
            {
                if(rgba[(y*width+x)*4+3]<128){x++;continue;}
                int begin=x;while(x<x1&&rgba[(y*width+x)*4+3]>=128)x++;
                float u0=Math.Max(minU,begin/(float)width),u1=Math.Min(maxU,x/(float)width),v0=Math.Max(minV,y/(float)height),v1=Math.Min(maxV,(y+1f)/height);
                if(u1<=u0||v1<=v0)continue;
                if(a.U>b.U){float temp=u0;u0=u1;u1=temp;}if(a.V>d.V){float temp=v0;v0=v1;v1=temp;}
                int start=output.Vertices.Count;
                Add(output,a,b,d,u0,v0);Add(output,a,b,d,u1,v0);Add(output,a,b,d,u1,v1);Add(output,a,b,d,u0,v1);
                Indices(output,start);
            }
        }
        return output;
    }
    private static void Add(VoxelGeometry output,VoxelGeometry.Vertex a,VoxelGeometry.Vertex b,VoxelGeometry.Vertex d,float u,float v)
    {
        float s=(u-a.U)/(b.U-a.U),t=(v-a.V)/(d.V-a.V);
        output.Vertices.Add(new VoxelGeometry.Vertex(a.X+(b.X-a.X)*s+(d.X-a.X)*t,a.Y+(b.Y-a.Y)*s+(d.Y-a.Y)*t,a.Z+(b.Z-a.Z)*s+(d.Z-a.Z)*t,a.Nx,a.Ny,a.Nz,u,v));
    }
    private static void CopyFace(VoxelGeometry source,int offset,VoxelGeometry output)
    {int start=output.Vertices.Count;for(int i=0;i<4;i++)output.Vertices.Add(source.Vertices[offset+i]);Indices(output,start);}
    private static void Indices(VoxelGeometry output,int start)
    {output.Indices.Add(start);output.Indices.Add(start+2);output.Indices.Add(start+1);output.Indices.Add(start);output.Indices.Add(start+3);output.Indices.Add(start+2);}
    public static VoxelGeometry GeneratedSword(byte[] rgba)
    {
        // Original item/generated sprite spans (0..16,0..16,7.5..8.5),
        // translated by (-.5,-.5,-.5) by ItemStackRenderState.
        var mesh=VoxelGeometry.Item(rgba,1,16,7.5f,7.5f);
        for(int i=0;i<mesh.Vertices.Count;i++)
        {var v=mesh.Vertices[i];mesh.Vertices[i]=new VoxelGeometry.Vertex(v.X,v.Y/.6f,v.Z,v.Nx,v.Ny,v.Nz,v.U,v.V);}
        return mesh;
    }
}
