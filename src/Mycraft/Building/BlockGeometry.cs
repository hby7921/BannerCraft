using System;
using System.Collections.Generic;

namespace Mycraft.Building;

// Pure MC-grid math; scene rendering uses BL (x,z,y) relative to the activation height.
public static class BlockGeometry
{
    public const int Limit = 256;
    public static readonly string[] Items = { "stone", "dirt", "cobblestone", "oak_planks", "bricks" };
    // These legacy textures are still used by held-item visuals. Placed blocks
    // accept any canonical ID and require a real exported model, not this list.
    public static bool Allowed(string item)
    {
        if(string.IsNullOrEmpty(item)||item.Length>256||item=="minecraft:air")return false;
        int separator=item.IndexOf(':');
        if(separator<1 || separator==item.Length-1 || item.LastIndexOf(':')!=separator)return false;
        for(int i=0;i<item.Length;i++)
        {
            char c=item[i];
            if(i==separator)continue;
            if(!(c>='a'&&c<='z') && !(c>='0'&&c<='9') && c!='_' && c!='-' && c!='.' && !(i>separator&&c=='/'))return false;
        }
        return true;
    }
    public static bool CanAdd(int count, bool existing) => count >= 0 && count <= Limit && (existing || count < Limit);
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static float Height(float y, float groundY, float anchorZ) => anchorZ + y - groundY;
    public static (float X,float Y,float Z) ToNative(float x,float y,float z,float groundY,float anchorZ,int offsetX=0,int offsetZ=0)
        =>(x-offsetX,z-offsetZ,Height(y,groundY,anchorZ));
    public static (float X,float Y,float Z) ToMinecraft(float x,float y,float z,float groundY,float anchorZ,int offsetX=0,int offsetZ=0)
        =>(x+offsetX,groundY+z-anchorZ,y+offsetZ);
    public static int Cell(float coordinate) => (int)Math.Floor(coordinate);
    public const float TerrainTolerance = .03f;
    public const float TntSinkLimit = .7f;
    public static int GroundCell(float highestTerrain, float groundY, float anchorZ) =>
        (int)Math.Ceiling(groundY + highestTerrain - anchorZ - TerrainTolerance);
    public static bool ClearsTerrain(float bottom, float highestTerrain) =>
        Finite(bottom) && Finite(highestTerrain) && bottom >= highestTerrain - TerrainTolerance;
    public static GroundSupport TntGroundSupport(int x,int z,float highestTerrain,float groundY,float anchorZ)
    {
        if(!Finite(highestTerrain)||!Finite(groundY)||!Finite(anchorZ))throw new ArgumentException("Invalid terrain support height");
        float surface=groundY+highestTerrain-anchorZ;
        if(!Finite(surface)||Math.Abs(surface)>30000000)throw new ArgumentException("Terrain support lies outside the bounded MC stage");
        int y=Cell(surface-TerrainTolerance);
        return new GroundSupport {X=x,Y=y,Z=z,H=Math.Max(1f/256,Math.Min(1,surface-y))};
    }
    // MC validates entity patches relative to the entity anchor, not the terrain.
    // A flying or falling creature does not require a support farther than16 cells away.
    public static List<GroundSupport> EntityGroundPatch(IEnumerable<GroundSupport> samples,float nearMcY)
    {
        var result=new List<GroundSupport>();
        if(!Finite(nearMcY)||Math.Abs(nearMcY)>2048)return result;
        int anchor=Cell(nearMcY);
        foreach(var sample in samples)
            if(Math.Abs((long)sample.Y-anchor)<=16)result.Add(sample);
        return result;
    }
    public static int TntPlacementCell(int supportY,float height)
    {
        if(!Finite(height)||height<1f/256||height>1)throw new ArgumentException("Invalid fractional terrain support");
        return GroundHeightSteps(height)<=179?supportY:supportY+1;
    }
    public static int GroundHeightSteps(float height)
    {
        if(!Finite(height))throw new ArgumentException("Invalid fractional terrain support");
        return Math.Max(1,Math.Min(256,(int)Math.Floor(Math.Max(0,Math.Min(1,height))*256+.5f)));
    }
    public static bool ClearsTntTerrain(float bottom,float highestTerrain)=>
        Finite(bottom)&&Finite(highestTerrain)&&highestTerrain-bottom<=TntSinkLimit+1f/256+TerrainTolerance;
    public static bool OverlapsAgent(int x, int z, float bottom, float agentX, float agentY, float agentZ, bool mount)
    {
        float radius = mount ? 1.4f : .45f;
        return agentX + radius > x && agentX - radius < x + 1 &&
            agentY + radius > z && agentY - radius < z + 1 &&
            agentZ + 2.5f > bottom && agentZ - .1f < bottom + 1;
    }
    // Outward winding, six independently UV-mapped faces. Coordinates centered at origin.
    public static readonly float[][] Faces = {
        new float[] { .5f,-.5f,-.5f, .5f,.5f,-.5f, .5f,.5f,.5f, .5f,-.5f,.5f },
        new float[] { -.5f,.5f,-.5f, -.5f,-.5f,-.5f, -.5f,-.5f,.5f, -.5f,.5f,.5f },
        new float[] { .5f,.5f,-.5f, -.5f,.5f,-.5f, -.5f,.5f,.5f, .5f,.5f,.5f },
        new float[] { -.5f,-.5f,-.5f, .5f,-.5f,-.5f, .5f,-.5f,.5f, -.5f,-.5f,.5f },
        new float[] { -.5f,-.5f,.5f, .5f,-.5f,.5f, .5f,.5f,.5f, -.5f,.5f,.5f },
        new float[] { -.5f,.5f,-.5f, .5f,.5f,-.5f, .5f,-.5f,-.5f, -.5f,-.5f,-.5f }
    };
    // Inputs MC axes, unit-length direction. Return entry distance and outward face normal.
    public static bool Ray(float ox,float oy,float oz,float dx,float dy,float dz,
        int x,int y,int z,float reach,out float distance,out int nx,out int ny,out int nz)
        => RayBox(ox,oy,oz,dx,dy,dz,new BlockBox(x,y,z,x+1,y+1,z+1),reach,out distance,out nx,out ny,out nz);

    public static bool RayBox(float ox,float oy,float oz,float dx,float dy,float dz,
        BlockBox box,float reach,out float distance,out int nx,out int ny,out int nz)
    {
        distance=0; nx=ny=nz=0;
        if(!Finite(ox)||!Finite(oy)||!Finite(oz)||!Finite(dx)||!Finite(dy)||!Finite(dz)||!Finite(reach)||reach<0)
            return false;
        if(!Finite(box.MinX)||!Finite(box.MinY)||!Finite(box.MinZ)||!Finite(box.MaxX)||!Finite(box.MaxY)||!Finite(box.MaxZ)||
            box.MinX>=box.MaxX||box.MinY>=box.MaxY||box.MinZ>=box.MaxZ)return false;
        float near=0,far=reach; int axis=-1,sign=0;
        for(int a=0;a<3;a++)
        {
            float origin=a==0?ox:a==1?oy:oz,direction=a==0?dx:a==1?dy:dz;
            float minimum=a==0?box.MinX:a==1?box.MinY:box.MinZ,maximum=a==0?box.MaxX:a==1?box.MaxY:box.MaxZ;
            if(Math.Abs(direction)<.000001f)
            { if(origin<minimum || origin>maximum) return false; continue; }
            float enter=(minimum-origin)/direction,exit=(maximum-origin)/direction;
            int face=-1;
            if(enter>exit){var swap=enter;enter=exit;exit=swap;face=1;}
            if(enter>near || axis<0 && enter==near){near=enter;axis=a;sign=face;}
            far=Math.Min(far,exit); if(near>far)return false;
        }
        // A ray beginning inside a block is not a valid placement target.
        if(axis<0 || near>reach)return false;
        distance=near;
        if(axis==0)nx=sign; else if(axis==1)ny=sign; else nz=sign;
        return true;
    }

    public static int Face(int nx,int ny,int nz)=>ny<0?0:ny>0?1:nz<0?2:nz>0?3:nx<0?4:5;

    public static bool CameraReach(float ox,float oy,float oz,float dx,float dy,float dz,
        float eyeX,float eyeY,float eyeZ,float reach,out float far)
    {
        far=0;
        if(!Finite(reach)||reach<0||Math.Abs(dx*dx+dy*dy+dz*dz-1)>.001f)return false;
        float x=ox-eyeX,y=oy-eyeY,z=oz-eyeZ;
        float b=x*dx+y*dy+z*dz,discriminant=b*b-(x*x+y*y+z*z-reach*reach);
        if(!Finite(discriminant)||discriminant<0)return false;
        far=-b+(float)Math.Sqrt(discriminant);
        return Finite(far)&&far>0;
    }

    public static BlockBox Translate(BlockBox box,int x,int y,int z)=>new BlockBox(
        box.MinX+x,box.MinY+y,box.MinZ+z,box.MaxX+x,box.MaxY+y,box.MaxZ+z);

    public static BlockBox NativeBox(BlockBox box)=>new BlockBox(box.MinX,box.MinZ,box.MinY,box.MaxX,box.MaxZ,box.MaxY);

    // Instance-frame transform of an unchanged native prefab resource. Unlike
    // PhysicsShape.Transform, this never modifies a shared body's geometry.
    public static float[] PrefabFrame(BlockBox mcBox,BlockBox prefabBounds)
    {
        var native=NativeBox(mcBox);
        float sx=(native.MaxX-native.MinX)/(prefabBounds.MaxX-prefabBounds.MinX);
        float sy=(native.MaxY-native.MinY)/(prefabBounds.MaxY-prefabBounds.MinY);
        float sz=(native.MaxZ-native.MinZ)/(prefabBounds.MaxZ-prefabBounds.MinZ);
        if(!Finite(sx)||!Finite(sy)||!Finite(sz)||sx<=0||sy<=0||sz<=0)throw new ArgumentException("Invalid prefab collider bounds");
        return new[]{sx,sy,sz,native.MinX-prefabBounds.MinX*sx,native.MinY-prefabBounds.MinY*sy,native.MinZ-prefabBounds.MinZ*sz};
    }

    public static bool OverlapsAgent(BlockBox box,float agentX,float agentY,float agentZ,bool mount)
    {
        float radius=mount?1.4f:.45f;
        return agentX+radius>box.MinX && agentX-radius<box.MaxX &&
            agentY+2.5f>box.MinY && agentY-.1f<box.MaxY &&
            agentZ+radius>box.MinZ && agentZ-radius<box.MaxZ;
    }
}
