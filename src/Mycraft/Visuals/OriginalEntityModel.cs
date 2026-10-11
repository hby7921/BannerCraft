using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mycraft.Visuals;

// These vertices, normals, UVs, mirror winding and poses come directly from the
// pinned client's LayerDefinition.bakeRoot, not a reconstructed humanoid mesh.
public sealed class OriginalEntityModel
{
    public sealed class Part
    {
        public string Path="";
        public int Parent=-1;
        public EntityPose Pose;
        public VoxelGeometry Geometry=new VoxelGeometry();
    }
    public Part[] Parts {get;private set;}=Array.Empty<Part>();
    public static OriginalEntityModel SinglePart(VoxelGeometry geometry)=>new OriginalEntityModel {Parts=new[]{new Part {Pose=new EntityPose(0,0,0,0,0,0),Geometry=geometry}}};
    public static OriginalEntityModel Read(Stream stream)
    {
        using(var reader=new BinaryReader(stream,Encoding.UTF8,true))
        {
            if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="EMD1")throw new FormatException("Invalid original entity model header");
            int count=reader.ReadInt32();if(count<1||count>128)throw new FormatException("Invalid original entity part count");
            var parts=new Part[count];var paths=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int i=0;i<count;i++)
            {
                int n=reader.ReadInt32();if(n<0||n>256)throw new FormatException("Invalid model part path");
                var p=new Part {Path=Encoding.UTF8.GetString(reader.ReadBytes(n))};
                p.Pose=new EntityPose(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                if(p.Path.Length>0){string parent=p.Path.Substring(0,p.Path.LastIndexOf('/'));if(!paths.TryGetValue(parent,out p.Parent))throw new FormatException("Unordered model part parent");}
                if(!p.Pose.Finite)throw new FormatException("Nonfinite original model pose");
                int faces=reader.ReadInt32();if(faces<0||faces>4096)throw new FormatException("Invalid entity polygon count");
                for(int f=0;f<faces;f++)
                {
                    int start=p.Geometry.Vertices.Count;
                    for(int v=0;v<4;v++)
                    {
                        float x=reader.ReadSingle(),y=reader.ReadSingle(),z=reader.ReadSingle(),nx=reader.ReadSingle(),ny=reader.ReadSingle(),nz=reader.ReadSingle(),u=reader.ReadSingle(),uv=reader.ReadSingle();
                        if(!EntityPose.IsFinite(x)||!EntityPose.IsFinite(y)||!EntityPose.IsFinite(z)||!EntityPose.IsFinite(nx)||!EntityPose.IsFinite(ny)||!EntityPose.IsFinite(nz)||!EntityPose.IsFinite(u)||!EntityPose.IsFinite(uv))throw new FormatException("Nonfinite entity vertex");
                        // Model pixels -> host metres; the y/z reflection needs opposite winding.
                        p.Geometry.Vertices.Add(new VoxelGeometry.Vertex(x/16,z/16,y/16,nx,nz,ny,u,uv));
                    }
                    p.Geometry.Indices.Add(start);p.Geometry.Indices.Add(start+2);p.Geometry.Indices.Add(start+1);
                    p.Geometry.Indices.Add(start);p.Geometry.Indices.Add(start+3);p.Geometry.Indices.Add(start+2);
                }
                parts[i]=p;paths.Add(p.Path,i);
            }
            if(stream.Position!=stream.Length)throw new FormatException("Trailing original model data");
            return new OriginalEntityModel {Parts=parts};
        }
    }
    public EntityPose[] RestPoses()
    {var result=new EntityPose[Parts.Length];for(int i=0;i<result.Length;i++)result[i]=Parts[i].Pose;return result;}
    public int Index(string path)
    {for(int i=0;i<Parts.Length;i++)if(Parts[i].Path==path)return i;throw new ArgumentException("Missing original entity part: "+path);}

    public EntityMatrix[] Frames(EntityPose[] poses,EntityMatrix rendererRoot)
    {
        if(poses.Length!=Parts.Length)throw new ArgumentException("Entity pose count mismatch");
        var result=new EntityMatrix[poses.Length];
        for(int i=0;i<poses.Length;i++)result[i]=(Parts[i].Parent<0?rendererRoot:result[Parts[i].Parent])*poses[i].Matrix;
        return result;
    }
}

public struct EntityPose
{
    public float X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ;
    public EntityPose(float x,float y,float z,float rx,float ry,float rz,float sx=1,float sy=1,float sz=1)
    {X=x;Y=y;Z=z;RotX=rx;RotY=ry;RotZ=rz;ScaleX=sx;ScaleY=sy;ScaleZ=sz;}
    public bool Finite=>IsFinite(X)&&IsFinite(Y)&&IsFinite(Z)&&IsFinite(RotX)&&IsFinite(RotY)&&IsFinite(RotZ)&&IsFinite(ScaleX)&&IsFinite(ScaleY)&&IsFinite(ScaleZ)&&ScaleX>0&&ScaleY>0&&ScaleZ>0;
    public static bool IsFinite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
    // ModelPart.translateAndRotate uses translation, then Z/Y/X Euler order, then scale.
    public EntityMatrix Matrix=>EntityMatrix.Translation(X/16,Y/16,Z/16)*EntityMatrix.RotateZ(RotZ)*EntityMatrix.RotateY(RotY)*EntityMatrix.RotateX(RotX)*EntityMatrix.Scale(ScaleX,ScaleY,ScaleZ);
}

// Row-major affine matrix in Minecraft Cartesian coordinates. The native
// adapter changes bases with S * matrix * S; its mesh already uses S vertices.
public sealed class EntityMatrix
{
    public readonly float[] Values;
    public EntityMatrix(float[] values){if(values.Length!=12)throw new ArgumentException("Expected affine matrix");Values=values;}
    public static EntityMatrix Identity=>new EntityMatrix(new float[]{1,0,0,0,0,1,0,0,0,0,1,0});
    public static EntityMatrix Translation(float x,float y,float z)=>new EntityMatrix(new float[]{1,0,0,x,0,1,0,y,0,0,1,z});
    public static EntityMatrix Scale(float x,float y,float z)=>new EntityMatrix(new float[]{x,0,0,0,0,y,0,0,0,0,z,0});
    public static EntityMatrix RotateX(float r){float c=(float)Math.Cos(r),s=(float)Math.Sin(r);return new EntityMatrix(new float[]{1,0,0,0,0,c,-s,0,0,s,c,0});}
    public static EntityMatrix RotateY(float r){float c=(float)Math.Cos(r),s=(float)Math.Sin(r);return new EntityMatrix(new float[]{c,0,s,0,0,1,0,0,-s,0,c,0});}
    public static EntityMatrix RotateZ(float r){float c=(float)Math.Cos(r),s=(float)Math.Sin(r);return new EntityMatrix(new float[]{c,-s,0,0,s,c,0,0,0,0,1,0});}
    public static EntityMatrix AxisAngle(float r,float x,float y,float z)
    {
        // Match Quaternionf.setAngleAxis + Matrix4f.rotate exactly. The boat
        // renderer supplies (1,0,1), deliberately without normalizing its axis.
        float sin=(float)Math.Sin(r*.5f),w=(float)Math.Cos(r*.5f);x*=sin;y*=sin;z*=sin;
        float w2=w*w,x2=x*x,y2=y*y,z2=z*z;
        return new EntityMatrix(new[]{w2+x2-y2-z2,2*(x*y-z*w),2*(x*z+y*w),0,
            2*(x*y+z*w),w2-x2+y2-z2,2*(y*z-x*w),0,
            2*(x*z-y*w),2*(y*z+x*w),w2-x2-y2+z2,0});
    }
    public static EntityMatrix operator*(EntityMatrix a,EntityMatrix b)
    {
        var v=new float[12];for(int row=0;row<3;row++)for(int col=0;col<4;col++)
        {float value=col==3?a.Values[row*4+3]:0;for(int k=0;k<3;k++)value+=a.Values[row*4+k]*b.Values[k*4+col];v[row*4+col]=value;}return new EntityMatrix(v);
    }
    public float[] Point(float x,float y,float z)=>new[]{Values[0]*x+Values[1]*y+Values[2]*z+Values[3],Values[4]*x+Values[5]*y+Values[6]*z+Values[7],Values[8]*x+Values[9]*y+Values[10]*z+Values[11]};
    public float[] HostValues(float scale)
    {
        if(!EntityPose.IsFinite(scale)||scale<=0)throw new ArgumentOutOfRangeException(nameof(scale));
        return new[]{Values[0]*scale,Values[2]*scale,Values[1]*scale,Values[3]*scale,
            Values[8]*scale,Values[10]*scale,Values[9]*scale,Values[11]*scale,
            Values[4]*scale,Values[6]*scale,Values[5]*scale,Values[7]*scale};
    }
}

public static class OriginalEntityPose
{
    public const float Degrees=(float)Math.PI/180;
    private const float Pi=(float)Math.PI;
    private static readonly float[] Sine=BuildSine();
    private static float[] BuildSine(){var values=new float[65536];for(int i=0;i<values.Length;i++)values[i]=(float)Math.Sin(i*Math.PI*2/65536);return values;}
    // Mth's 26.3 lookup table deliberately quantizes animation phase.
    private static float Sin(float value)=>Sine[(int)((long)(value*10430.378350470453)&65535)];
    private static float Cos(float value)=>Sine[(int)((long)(value*10430.378350470453+16384)&65535)];
    public static float RelativeHeadYaw(float headYaw,float bodyYaw)
    {float angle=(headYaw-bodyYaw)%360;if(angle>=180)angle-=360;if(angle< -180)angle+=360;return angle;}
    // Grounded, adult, right-handed skeleton with a normal item. Original
    // HumanoidModel setup, normal swing, then SkeletonModel melee override.
    public static EntityPose[] WitherSkeleton(OriginalEntityModel model,float headYaw,float pitch,float walkPosition,float walkSpeed,float attackProgress,bool aggressive,int nativeTick,bool heldItem=true)
    {
        var pose=model.RestPoses();int head=model.Index("/head"),body=model.Index("/body"),right=model.Index("/right_arm"),left=model.Index("/left_arm"),rl=model.Index("/right_leg"),ll=model.Index("/left_leg");
        pose[head].RotX=pitch*Degrees;pose[head].RotY=headYaw*Degrees;
        pose[right].RotX=Cos(walkPosition*.6662f+Pi)*walkSpeed;
        pose[left].RotX=Cos(walkPosition*.6662f)*walkSpeed;
        pose[rl].RotX=Cos(walkPosition*.6662f)*1.4f*walkSpeed;
        pose[ll].RotX=Cos(walkPosition*.6662f+Pi)*1.4f*walkSpeed;
        pose[rl].RotY=pose[rl].RotZ=.005f;pose[ll].RotY=pose[ll].RotZ=-.005f;
        if(heldItem)pose[right].RotX=pose[right].RotX*.5f-Pi/10;
        float swing=Math.Max(0,Math.Min(1,attackProgress));
        if(swing>0)
        {
            float twist=Sin((float)Math.Sqrt(swing)*Pi*2)*.2f;
            pose[body].RotY=twist;pose[right].X=-Cos(twist)*5;pose[right].Z=Sin(twist)*5;
            pose[left].X=Cos(twist)*5;pose[left].Z=-Sin(twist)*5;
            pose[right].RotY+=twist;pose[left].RotY+=twist;pose[left].RotX+=twist;
            float eased=1-swing;eased=1-eased*eased*eased*eased;
            pose[right].RotX-=Sin(eased*Pi)*1.2f+Sin(swing*Pi)*-(pose[head].RotX-.7f)*.75f;
            pose[right].RotY+=twist*2;pose[right].RotZ+=Sin(swing*Pi)*-.4f;
        }
        Bob(ref pose[right],nativeTick,1);Bob(ref pose[left],nativeTick,-1);
        if(aggressive)
        {
            float wave=Sin(swing*Pi),wave2=Sin((1-(1-swing)*(1-swing))*Pi);
            pose[right].RotZ=pose[left].RotZ=0;pose[right].RotY=-(.1f-wave*.6f);pose[left].RotY=.1f-wave*.6f;
            pose[right].RotX=pose[left].RotX=-Pi/2-(wave*1.2f-wave2*.4f);
            Bob(ref pose[right],nativeTick,1);Bob(ref pose[left],nativeTick,-1);
        }
        return pose;
    }
    private static void Bob(ref EntityPose p,float age,float direction)
    {p.RotZ+=direction*(Cos(age*.09f)*.05f+.05f);p.RotX+=direction*Sin(age*.067f)*.05f;}
    public static EntityMatrix WitherRoot(float bodyYaw,int deathTime)
    {
        var root=EntityMatrix.RotateY((180-bodyYaw)*Degrees);
        if(deathTime>0)root=root*EntityMatrix.RotateZ(Math.Min(1,(float)Math.Sqrt(Math.Max(0,(deathTime-1)/20f*1.6f)))*Pi/2);
        return root*EntityMatrix.Scale(-1,-1,1)*EntityMatrix.Translation(0,-1.501f,0);
    }
    public static EntityPose[] Boat(OriginalEntityModel model,float leftPaddle,float rightPaddle)
    {
        var pose=model.RestPoses();Paddle(ref pose[model.Index("/left_paddle")],leftPaddle,0);Paddle(ref pose[model.Index("/right_paddle")],rightPaddle,1);return pose;
    }
    private static void Paddle(ref EntityPose p,float phase,int side)
    {p.RotX=-Pi/3+(Pi/4)*(Sin(-phase)+1)/2;p.RotY=-Pi/4+(Pi/2)*(Sin(-phase+1)+1)/2;if(side==1)p.RotY=Pi-p.RotY;}
    public static EntityMatrix BoatRoot(float yaw,float hurtTime,float damage,float bubbleAngle,int hurtDir=1,bool underWater=false)
    {
        var root=EntityMatrix.Translation(0,.375f,0)*EntityMatrix.RotateY((180-yaw)*Degrees);
        if(hurtTime>0)root=root*EntityMatrix.RotateX(Sin(hurtTime)*hurtTime*Math.Max(0,damage)/10*(hurtDir<0?-1:1)*Degrees);
        if(!underWater&&Math.Abs(bubbleAngle)>.00001f)root=root*EntityMatrix.AxisAngle(bubbleAngle*Degrees,1,0,1);
        return root*EntityMatrix.Scale(-1,-1,1)*EntityMatrix.RotateY(Pi/2);
    }
    public static EntityMatrix SwordHand(EntityMatrix root,EntityPose arm)
    {
        // SkeletonModel.translateToHand adds one pixel to the right-arm pivot.
        arm.X+=1;
        return root*arm.Matrix*EntityMatrix.RotateX(-Pi/2)*EntityMatrix.RotateY(Pi)*EntityMatrix.Translation(1f/16,2f/16,-10f/16)*
            EntityMatrix.Translation(0,4f/16,.5f/16)*EntityMatrix.RotateY(-Pi/2)*EntityMatrix.RotateZ(55*Degrees)*EntityMatrix.Scale(.85f,.85f,.85f);
    }
}
