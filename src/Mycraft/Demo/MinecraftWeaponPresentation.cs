using System;

namespace Mycraft.Demo;

// Minecraft owns item use and flight. These helpers select only their visible
// presentation; elapsed host time never charges a weapon or launches a shot.
public static class MinecraftWeaponPresentation
{
    public enum UsePose { None, Bow, CrossbowCharge, CrossbowAim, Spear, Trident }

    public static string Kind(string item)
    {
        if(item=="minecraft:bow")return "bow";
        if(item=="minecraft:crossbow")return "crossbow";
        if(item=="minecraft:trident")return "trident";
        if(item.StartsWith("minecraft:",StringComparison.Ordinal)&&item.EndsWith("_spear",StringComparison.Ordinal))return "spear";
        return "none";
    }

    public static string Sprite(string item,bool usingItem,int useTicks,float pull,bool loaded,bool firework)
    {
        string kind=Kind(item);
        // GUI spear icons use the original16px inventory model. Only held
        // meshes select the separate32px in_hand texture through Asset().
        if(kind!="bow"&&kind!="crossbow")return item.StartsWith("minecraft:",StringComparison.Ordinal)?item.Substring(10):"unknown";
        return Asset(item,kind,usingItem,useTicks,pull,loaded,firework);
    }

    public static string Asset(string item,string kind,bool usingItem,int useTicks,float crossbowCharge,bool loaded,bool firework)
    {
        string name=item.StartsWith("minecraft:",StringComparison.Ordinal)?item.Substring(10):"unknown";
        if(kind=="bow")
        {
            // 26.3 items/bow.json: use_duration * .05, thresholds .65/.9.
            if(!usingItem)return "bow";
            return useTicks>=18?"bow_pulling_2":useTicks>=13?"bow_pulling_1":"bow_pulling_0";
        }
        if(kind=="crossbow")
        {
            // Loaded ammunition takes precedence over the charging predicate.
            if(loaded)return firework?"crossbow_firework":"crossbow_arrow";
            if(!usingItem)return "crossbow_standby";
            float charge=Finite(crossbowCharge)?Math.Max(0,Math.Min(1,crossbowCharge)):0;
            return charge>=1?"crossbow_pulling_2":charge>=.58f?"crossbow_pulling_1":"crossbow_pulling_0";
        }
        return kind=="spear"?name+"_in_hand":name;
    }

    public static UsePose Pose(string kind,bool usingItem,bool loaded)
    {
        if(kind=="crossbow")return loaded?UsePose.CrossbowAim:usingItem?UsePose.CrossbowCharge:UsePose.None;
        if(!usingItem)return UsePose.None;
        switch(kind)
        {
            case "bow":return UsePose.Bow;
            case "spear":return UsePose.Spear;
            case "trident":return UsePose.Trident;
            default:return UsePose.None;
        }
    }

    public static float BowDraw(int useTicks)=>Math.Max(0,Math.Min(1,useTicks/20f));

    public static bool ProjectileKind(string kind)=>kind=="arrow"||kind=="spectral_arrow"||kind=="tipped_arrow"||kind=="trident"||kind=="firework"||kind=="firework_rocket";
    public static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    public static bool ValidProjectile(string id,string kind,float x,float y,float z,float yaw,float pitch)=>
        !string.IsNullOrEmpty(id)&&id.Length<=128&&ProjectileKind(kind)&&Finite(x)&&Finite(y)&&Finite(z)&&
        Math.Abs(x)<=30000000&&Math.Abs(y)<=30000000&&Math.Abs(z)<=30000000&&Finite(yaw)&&Finite(pitch)&&Math.Abs(yaw)<=1000000&&Math.Abs(pitch)<=1000000;

    // AbstractArrow/ThrownTrident native yaw is atan2(vx,vz), and native pitch
    // is atan2(vy,horizontal speed). Their convention differs from player look.
    public static (float X,float Y,float Z) ProjectileDirection(float yaw,float pitch)
    {
        if(!Finite(yaw)||!Finite(pitch))throw new ArgumentException("Invalid projectile orientation");
        double y=yaw*Math.PI/180,p=pitch*Math.PI/180,c=Math.Cos(p);
        return ((float)(Math.Sin(y)*c),(float)(Math.Cos(y)*c),(float)Math.Sin(p));
    }
}

public sealed class MinecraftProjectileMotion
{
    public const float InterpolationSeconds=.05f;
    public const float StaleSeconds=2;
    private float _fromX,_fromY,_fromZ,_x,_y,_z,_fromYaw,_fromPitch,_yaw,_pitch;
    private bool _received;
    public float Age {get;private set;}

    public void Reset(){_received=false;Age=0;_fromX=_fromY=_fromZ=_x=_y=_z=_fromYaw=_fromPitch=_yaw=_pitch=0;}

    public void Apply(float x,float y,float z,float yaw,float pitch)
    {
        if(!MinecraftWeaponPresentation.Finite(x)||!MinecraftWeaponPresentation.Finite(y)||!MinecraftWeaponPresentation.Finite(z)||
            !MinecraftWeaponPresentation.Finite(yaw)||!MinecraftWeaponPresentation.Finite(pitch))throw new ArgumentException("Invalid projectile transform");
        Sample(out _fromX,out _fromY,out _fromZ,out _fromYaw,out _fromPitch);
        if(!_received){_fromX=x;_fromY=y;_fromZ=z;_fromYaw=yaw;_fromPitch=pitch;}
        _x=x;_y=y;_z=z;_yaw=_fromYaw+Wrap(yaw-_fromYaw);_pitch=_fromPitch+Wrap(pitch-_fromPitch);
        Age=0;_received=true;
    }
    public void Advance(float dt)
    {
        if(MinecraftWeaponPresentation.Finite(dt)&&dt>0)Age+=Math.Min(dt,.25f);
    }
    public void Sample(out float x,out float y,out float z,out float yaw,out float pitch)
    {
        float t=Math.Min(1,Age/InterpolationSeconds);
        x=_fromX+(_x-_fromX)*t;y=_fromY+(_y-_fromY)*t;z=_fromZ+(_z-_fromZ)*t;
        yaw=_fromYaw+(_yaw-_fromYaw)*t;pitch=_fromPitch+(_pitch-_fromPitch)*t;
    }
    private static float Wrap(float angle)
    {
        angle%=360;if(angle>180)angle-=360;else if(angle< -180)angle+=360;return angle;
    }
}
