using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

public static class NativeEntityProtocol
{
    public static bool Validate(Dictionary<string,object> message)
    {
        string type=DemoProtocol.S(message,"type");
        if(!Uuid(message,"entityId") && type!="mob_hit" && type!="mob_summon" && type!="mc_army_spawn" && type!="mc_army_spawn_result")return false;
        switch(DemoProtocol.S(message,"type"))
        {
            case "mob_state":
                return MobKind(DemoProtocol.S(message,"kind")) && MobPresentation(message) && Position(message) && Motion(message) && Tick(message) &&
                    Numbers(message,"yaw","bodyYaw","headYaw","pitch","walkPosition","walkSpeed","attackProgress","hp","maxHp") &&
                    Boolean(message,"alive") && Boolean(message,"aggressive") && Text(message,"mainItem",256) &&
                    Integer(message,"targetEid",0,int.MaxValue) && Integer(message,"hurtTime",0,200) && Integer(message,"deathTime",0,200) &&
                    DemoProtocol.D(message,"walkSpeed")>=0 && DemoProtocol.D(message,"walkSpeed")<=16 &&
                    DemoProtocol.D(message,"attackProgress")>=0 && DemoProtocol.D(message,"attackProgress")<=1 &&
                    DemoProtocol.D(message,"maxHp")>0 && DemoProtocol.D(message,"maxHp")<=1024 &&
                    DemoProtocol.D(message,"hp")>=0 && DemoProtocol.D(message,"hp")<=DemoProtocol.D(message,"maxHp") &&
                    (!BridgeJson.B(message,"alive") || DemoProtocol.D(message,"hp")>0);
            case "boat_state":
                return BoatKind(DemoProtocol.S(message,"kind")) && Position(message) && Tick(message) && Boolean(message,"alive") &&
                    Numbers(message,"yaw","pitch","leftPaddle","rightPaddle","hurtTime","damage","bubbleAngle") &&
                    DemoProtocol.D(message,"damage")>=0 && DemoProtocol.D(message,"damage")<=100000 &&
                    DemoProtocol.D(message,"hurtTime")>=0 && DemoProtocol.D(message,"hurtTime")<=200 &&
                    (!message.ContainsKey("hurtDir") || Integer(message,"hurtDir",-1,1) && DemoProtocol.L(message,"hurtDir")!=0) &&
                    (!message.ContainsKey("underWater") || Boolean(message,"underWater"));
            case "mob_hit":
                return Uuid(message,"sourceId") && Text(message,"eventId",128) && Integer(message,"eid",1,int.MaxValue) &&
                    Number(message,"damage") && DemoProtocol.D(message,"damage")>0 && DemoProtocol.D(message,"damage")<=10000 &&
                    (DemoProtocol.S(message,"kind")=="melee" || DemoProtocol.S(message,"kind")=="wither" ||
                     DemoProtocol.S(message,"kind")=="projectile" || DemoProtocol.S(message,"kind")=="explosion");
            case "mob_summon":return Text(message,"requestId",128) && MobKind(DemoProtocol.S(message,"kind"));
            case "mc_army_spawn":
                if(!Uuid(message,"requestId") || !Integer(message,"count",1,16) || !Boolean(message,"playerSide"))return false;
                var slots=new HashSet<long>();
                for(int i=0;i<DemoProtocol.L(message,"count");i++)
                {
                    string p="mob"+i;
                    if(!MobKind(DemoProtocol.S(message,p+"Kind")) || !Integer(message,p+"Slot",1,int.MaxValue) || !slots.Add(DemoProtocol.L(message,p+"Slot")))return false;
                    foreach(string axis in new[]{"X","Y","Z"})if(!Number(message,p+axis) || Math.Abs(DemoProtocol.D(message,p+axis))>(axis=="Y"?2048:131072))return false;
                }
                return true;
            case "mc_army_spawn_result":return Uuid(message,"requestId") && Uuid(message,"eventId") && Boolean(message,"ok") && Text(message,"message",1024);
            case "mob_hurt":
                return Text(message,"eventId",128) && Integer(message,"sourceEid",1,int.MaxValue) && Number(message,"damage") &&
                    (!message.ContainsKey("sourceKind") || DemoProtocol.S(message,"sourceKind")=="melee" || DemoProtocol.S(message,"sourceKind")=="arrow" || DemoProtocol.S(message,"sourceKind")=="projectile") &&
                    DemoProtocol.D(message,"damage")>0 && DemoProtocol.D(message,"damage")<=10000;
            case "mob_remove":
            case "boat_remove": return Text(message,"eventId",128) && Text(message,"reason",128);
            case "mob_ground":
                if(!Position(message) || !Integer(message,"groundCount",0,81))return false;
                for(int i=0;i<DemoProtocol.L(message,"groundCount");i++)
                    if(!Integer(message,"ground"+i+"X",-131072,131072) || !Integer(message,"ground"+i+"Y",-2048,2048) ||
                        !Integer(message,"ground"+i+"Z",-131072,131072) || !Number(message,"ground"+i+"H") ||
                        DemoProtocol.D(message,"ground"+i+"H")<=0 || DemoProtocol.D(message,"ground"+i+"H")>1)return false;
                return true;
            default:return false;
        }
    }
    public static bool MobKind(string kind)
    {
        if(string.IsNullOrEmpty(kind) || kind.Length>96)return false;
        foreach(char c in kind)if(!(c>='a'&&c<='z') && !(c>='0'&&c<='9') && c!='_')return false;
        return true;
    }
    public static bool VisualKey(string key)
    {
        if(key==null || key.Length!=32)return false;
        foreach(char c in key)if(!(c>='a'&&c<='f') && !(c>='0'&&c<='9'))return false;
        return true;
    }
    private static bool MobPresentation(Dictionary<string,object> m)
    {
        if(m.ContainsKey("visualKey") && !VisualKey(DemoProtocol.S(m,"visualKey")))return false;
        if(DemoProtocol.S(m,"kind")!="wither_skeleton" &&
            (!m.ContainsKey("visualKey") || !Number(m,"width") || !Number(m,"height")))return false;
        foreach(string key in new[]{"width","height"})
            if(m.ContainsKey(key) && (!Number(m,key) || DemoProtocol.D(m,key)<=0 || DemoProtocol.D(m,key)>64))return false;
        foreach(string key in new[]{"hostile","baby","boss"})if(m.ContainsKey(key) && !Boolean(m,key))return false;
        if(m.ContainsKey("armySlot") && (!Integer(m,"armySlot",1,int.MaxValue) || !Boolean(m,"armyPlayerSide")))return false;
        if(m.ContainsKey("armyPlayerSide") && !m.ContainsKey("armySlot"))return false;
        if(m.TryGetValue("bossName",out var bossName) && (!(bossName is string name) || name.Length>256))return false;
        if(m.ContainsKey("invulnerableTicks") && !Integer(m,"invulnerableTicks",0,100000))return false;
        return true;
    }
    public static bool BoatKind(string kind)
    {
        if(kind=="bamboo_raft" || kind=="bamboo_chest_raft")return true;
        foreach(var wood in new[]{"oak","spruce","birch","jungle","acacia","dark_oak","mangrove","cherry","pale_oak","poplar"})
            if(kind==wood+"_boat" || kind==wood+"_chest_boat")return true;
        return false;
    }
    private static bool Uuid(Dictionary<string,object> m,string key)=>Text(m,key,128) && Guid.TryParse(DemoProtocol.S(m,key),out _);
    private static bool Text(Dictionary<string,object> m,string key,int max)=>m.TryGetValue(key,out var v) && v is string s && s.Length>0 && s.Length<=max;
    private static bool Boolean(Dictionary<string,object> m,string key)=>m.TryGetValue(key,out var v) && v is bool;
    private static bool Number(Dictionary<string,object> m,string key)
    {
        if(!m.TryGetValue(key,out var v) || !(v is int || v is long || v is double || v is float || v is decimal))return false;
        var n=DemoProtocol.D(m,key,double.NaN);return !double.IsNaN(n) && !double.IsInfinity(n);
    }
    private static bool Numbers(Dictionary<string,object> m,params string[] keys){foreach(var key in keys)if(!Number(m,key))return false;return true;}
    private static bool Integer(Dictionary<string,object> m,string key,long min,long max)=>m.TryGetValue(key,out var v) && (v is int || v is long) && DemoProtocol.L(m,key)<=(double)max && DemoProtocol.L(m,key)>=(double)min;
    private static bool Position(Dictionary<string,object> m)=>Numbers(m,"x","y","z") && Math.Abs(DemoProtocol.D(m,"x"))<=131072 && Math.Abs(DemoProtocol.D(m,"z"))<=131072 && Math.Abs(DemoProtocol.D(m,"y"))<=2048;
    private static bool Motion(Dictionary<string,object> m)=>Numbers(m,"vx","vy","vz") && Math.Abs(DemoProtocol.D(m,"vx"))<=10 && Math.Abs(DemoProtocol.D(m,"vy"))<=10 && Math.Abs(DemoProtocol.D(m,"vz"))<=10;
    private static bool Tick(Dictionary<string,object> m)=>Integer(m,"nativeTick",0,long.MaxValue);
}
