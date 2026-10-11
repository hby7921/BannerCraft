using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

/// <summary>
/// demo-v1 protocol constants, message builders and envelope helpers.
/// Authoritative spec: protocol/demo-v1.md.
/// </summary>
public static class DemoProtocol
{
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 38765;
    public const int PoseHz = 20;
    public const int HeartbeatMs = 1000;
    public const int TimeoutMs = 3000;
    public const int MaxLineBytes = 65536;
    public const int DedupCapacity = 256;
    public const int RetryCount = 3;
    public const int RetryIntervalMs = 500;
    public const int PendingHitTtlMs = 1500;
    public const int InQueueCapacity = 512;
    // Full six-hundred-actor pose and volley bursts remain bounded without dropping control messages.
    public const int OutQueueCapacity = 4096;

    public static Dictionary<string, object> Msg(string type, string sid, long seq)
    {
        return new Dictionary<string, object>
        {
            { "type", type },
            { "pv", ProtocolVersion },
            { "sid", sid },
            { "seq", seq }
        };
    }

    public static Dictionary<string, object> Hello(string gameVersion, long seq = 1)
    {
        var m = Msg("hello", "", seq);
        m["agent"] = "mycraft-bannerlord";
        m["gameVersion"] = gameVersion;
        return m;
    }

    public static Dictionary<string, object> BeginSession(string sid, long seq, double groundY, double offsetX, double offsetZ, double scale)
    {
        var m = Msg("begin_session", sid, seq);
        m["mode"] = "demo_field";
        m["groundY"] = groundY;
        m["offsetX"] = offsetX;
        m["offsetZ"] = offsetZ;
        m["scale"] = scale;
        return m;
    }

    public static Dictionary<string, object> EndSession(string sid, long seq, string reason)
    {
        var m = Msg("end_session", sid, seq);
        m["reason"] = reason;
        return m;
    }

    public static Dictionary<string, object> PlayerPose(string sid, long seq, double x, double y, double z, double yaw)
    {
        var m = Msg("player_pose", sid, seq);
        m["x"] = x;
        m["y"] = y;
        m["z"] = z;
        m["yaw"] = yaw;
        return m;
    }

    public static Dictionary<string, object> ActorUpsert(string sid, long seq, int eid, double x, double y, double z, double yaw, bool alive, string name)
    {
        var m = Msg("actor_upsert", sid, seq);
        m["eid"] = eid;
        m["x"] = x;
        m["y"] = y;
        m["z"] = z;
        m["yaw"] = yaw;
        m["alive"] = alive;
        m["name"] = name;
        return m;
    }

    public static Dictionary<string, object> ActorRemove(string sid, long seq, int eid)
    {
        var m = Msg("actor_remove", sid, seq);
        m["eid"] = eid;
        return m;
    }

    public static Dictionary<string, object> AttackInput(string sid, long seq, string attackId, double x, double y, double z, double yaw)
    {
        var m = Msg("attack_input", sid, seq);
        m["attackId"] = attackId;
        m["x"] = x;
        m["y"] = y;
        m["z"] = z;
        m["yaw"] = yaw;
        return m;
    }

    public static Dictionary<string, object> PlayerHurt(string sid, long seq, string eventId, double damage, int sourceEid)
    {
        var m = Msg("player_hurt", sid, seq);
        m["eventId"] = eventId;
        m["damage"] = damage;
        m["sourceEid"] = sourceEid;
        return m;
    }

    public static Dictionary<string,object> SelectSlot(string sid,long seq,int slot)
    {
        if(slot<0 || slot>8)throw new ArgumentOutOfRangeException(nameof(slot));
        var message=Msg("select_slot",sid,seq);
        message["slot"]=slot;
        return message;
    }

    public static Dictionary<string,object> UseOffhand(string sid,long seq,bool pressed)
    {
        var message=Msg("use_item",sid,seq);
        message["hand"]="offhand";message["pressed"]=pressed;
        return message;
    }

    public static Dictionary<string, object> Ack(string sid, long seq, string ackEventId)
    {
        var m = Msg("ack", sid, seq);
        m["ackEventId"] = ackEventId;
        return m;
    }

    public static Dictionary<string, object> Error(string sid, long seq, string code, string message)
    {
        var m = Msg("error", sid, seq);
        m["code"] = code;
        m["message"] = message;
        return m;
    }

    public static bool ValidatePv(Dictionary<string, object> msg)
        => Integer(msg, "pv") && DemoProtocol.L(msg, "pv") == ProtocolVersion;

    public static bool ValidateEnvelope(Dictionary<string, object> msg)
        => msg != null && Text(msg, "type", 64) && ValidatePv(msg)
            && msg.TryGetValue("sid", out var sid) && sid is string s && s.Length <= 128
            && Integer(msg, "seq") && L(msg, "seq") > 0;

    public static bool ValidateMessage(Dictionary<string, object> msg)
    {
        if (!ValidateEnvelope(msg)) return false;
        switch (S(msg, "type"))
        {
            case "mob_state": case "mob_hit": case "mob_hurt": case "mob_remove": case "mob_ground": case "mob_summon": case "mc_army_spawn": case "mc_army_spawn_result":
            case "boat_state": case "boat_remove": return NativeEntityProtocol.Validate(msg);
            case "hello": return Text(msg, "agent") && Text(msg, "gameVersion");
            case "hello_ack": return Text(msg, "agent") && Text(msg, "mcVersion") && Text(msg, "playerName");
            case "heartbeat": return true;
            case "error": return Text(msg, "code") && Text(msg, "message", 1024);
            case "begin_session": return S(msg, "mode") == "demo_field" && Number(msg, "groundY")
                    && Number(msg, "offsetX") && Number(msg, "offsetZ") && Number(msg, "scale") && D(msg, "scale") > 0
                    && (!msg.ContainsKey("yawOffsetDeg") || Number(msg, "yawOffsetDeg"))
                    && (!msg.ContainsKey("actorLimit") || Integer(msg,"actorLimit") && (L(msg,"actorLimit")==64 || L(msg,"actorLimit")==256 || L(msg,"actorLimit")==640));
            case "end_session": return Text(msg, "reason");
            case "staging_origin":return Integer(msg,"offsetX")&&Integer(msg,"offsetZ")&&
                Math.Abs(D(msg,"offsetX"))<=131072&&Math.Abs(D(msg,"offsetZ"))<=131072;
            case "tnt_state":return Text(msg,"entityId")&&WorldPosition(msg)&&Integer(msg,"fuse")&&L(msg,"fuse")>=0&&L(msg,"fuse")<=32767&&Boolean(msg,"alive");
            case "projectile_state":return Text(msg,"entityId")&&(ProjectileKind(S(msg,"kind")) || NativeEntityProtocol.MobKind(S(msg,"kind")) && (msg.ContainsKey("visualKey") || !BridgeJson.B(msg,"alive")))&&WorldPosition(msg)&&Number(msg,"yaw")&&Number(msg,"pitch")&&Boolean(msg,"alive")&&
                Motion(msg)&&
                (!msg.ContainsKey("visualKey") || NativeEntityProtocol.VisualKey(S(msg,"visualKey")))&&
                (!msg.ContainsKey("color")||Integer(msg,"color")&&L(msg,"color")>=-1&&L(msg,"color")<=16777215);
            case "player_motion":return Boolean(msg,"spinning")&&Integer(msg,"nativeTick")&&L(msg,"nativeTick")>=0&&Motion(msg)&&msg.ContainsKey("vx");
            case "lightning_state":return Text(msg,"entityId")&&WorldPosition(msg)&&Boolean(msg,"visible")&&Boolean(msg,"alive")&&Seed(S(msg,"seed"));
            case "stop_spin":return Text(msg,"requestId");
            case "weapon_hit":return Text(msg,"eventId")&&Text(msg,"sourceId")&&(ProjectileKind(S(msg,"kind"))||S(msg,"kind")=="spear")&&ActorId(msg,"eid")&&Number(msg,"damage")&&D(msg,"damage")>0&&D(msg,"damage")<=10000;
            case "explosion":return Text(msg,"eventId")&&Text(msg,"blastId")&&WorldPosition(msg)&&Number(msg,"radius")&&D(msg,"radius")>0&&D(msg,"radius")<=64;
            case "explosion_hit":return Text(msg,"eventId")&&Text(msg,"blastId")&&ActorId(msg,"eid")&&Number(msg,"damage")&&D(msg,"damage")>0&&D(msg,"damage")<=10000&&
                Number(msg,"knockbackX")&&Number(msg,"knockbackY")&&Number(msg,"knockbackZ");
            case "player_pose": return Pose(msg);
            case "actor_upsert": return ActorId(msg, "eid") && Pose(msg) && Boolean(msg, "alive") && Text(msg, "name", 256)&&
                (!(msg.ContainsKey("hp")||msg.ContainsKey("maxHp")) || Number(msg,"hp")&&Number(msg,"maxHp")&&D(msg,"hp")>=0&&D(msg,"maxHp")>0&&D(msg,"hp")<=D(msg,"maxHp")&&D(msg,"maxHp")<=10000);
            case "actor_remove": return ActorId(msg, "eid");
            case "attack_input": return Text(msg, "attackId") && Pose(msg);
            case "select_slot": return Integer(msg,"slot") && L(msg,"slot")>=0 && L(msg,"slot")<=8;
            case "use_item":return S(msg,"hand")=="offhand" && Boolean(msg,"pressed");
            case "interact":return Text(msg,"requestId") && Boolean(msg,"pressed") && Boolean(msg,"target") &&
                (!msg.ContainsKey("cancel")||Boolean(msg,"cancel")&&(!BridgeJson.B(msg,"cancel")||!BridgeJson.B(msg,"pressed"))) &&
                (!BridgeJson.B(msg,"target") || BridgeJson.B(msg,"pressed") && BlockCoordinates(msg) && Face(msg) && Boolean(msg,"terrain") && Boolean(msg,"canPlace") &&
                 HitCoordinate(msg,"x","hitX") && HitCoordinate(msg,"y","hitY") && HitCoordinate(msg,"z","hitZ")) && GroundPatch(msg);
            case "mine":return Text(msg,"requestId") && Boolean(msg,"pressed") && Boolean(msg,"target") &&
                (!BridgeJson.B(msg,"target") || BridgeJson.B(msg,"pressed") && BlockCoordinates(msg) && Face(msg));
            case "interaction_result":return Text(msg,"requestId") && Boolean(msg,"ok") && Text(msg,"message",1024);
            case "inventory_request":return true;
            case "inventory_state":return Integer(msg,"rev") && L(msg,"rev")>0 && Boolean(msg,"creative") && Integer(msg,"selectedSlot");
            case "inventory_swap":return Text(msg,"requestId") && Integer(msg,"rev") && L(msg,"rev")>0 &&
                InventorySlot(msg,"fromSlot") && InventorySlot(msg,"toSlot");
            case "creative_give":return Text(msg,"requestId") && Integer(msg,"rev") && L(msg,"rev")>0 && Text(msg,"item",256) && Integer(msg,"slot") && L(msg,"slot")>=0 && L(msg,"slot")<=8;
            case "build_request":return Text(msg,"requestId") && BlockCoordinates(msg) && (S(msg,"action")=="place" || S(msg,"action")=="break");
            case "block_state":return Text(msg,"requestId") && BlockCoordinates(msg) && Text(msg,"item",256);
            case "build_result":return Text(msg,"requestId") && Boolean(msg,"ok") && Text(msg,"message",1024);
            case "inventory_result":return Text(msg,"requestId") && Boolean(msg,"ok") && Text(msg,"message",1024);
            case "hit_actor": return Text(msg, "eventId") && Text(msg, "attackId") && ActorId(msg, "eid")
                    && Number(msg, "damage") && D(msg, "damage") > 0 && Boolean(msg, "killed");
            case "player_hurt": return Text(msg, "eventId") && Number(msg, "damage") && D(msg, "damage") > 0
                    && ActorId(msg, "sourceEid") && OptionalHurtPose(msg) && OptionalDamageOrigin(msg);
            case "player_hurt_result":return Text(msg,"eventId") && Number(msg,"hpBefore") && Number(msg,"hpAfter") &&
                    Number(msg,"expectedBlocked") && D(msg,"expectedBlocked")>=0 && Boolean(msg,"sourceKnown") && Number(msg,"headYaw") &&
                    Number(msg,"actualBlocked") && D(msg,"actualBlocked")>=0 && Boolean(msg,"nativeBlockChecked");
            case "player_state": return Number(msg, "hp") && Number(msg, "maxHp") && Boolean(msg, "alive") && Text(msg, "item", 256)
                    && D(msg, "maxHp") > 0 && D(msg, "hp") >= 0 && D(msg, "hp") <= D(msg, "maxHp")
                    && BridgeJson.B(msg, "alive") == (D(msg, "hp") > 0) && WeaponState(msg);
            case "ack": return Text(msg, "ackEventId");
            default: return false;
        }
    }

    private static bool Pose(Dictionary<string, object> msg)
        => Number(msg, "x") && Number(msg, "y") && Number(msg, "z") && Number(msg, "yaw") &&
           Motion(msg) &&
           (!msg.ContainsKey("pitch") || Number(msg,"pitch") && Math.Abs(D(msg,"pitch"))<=90);
    private static bool ProjectileKind(string kind)=>kind=="arrow"||kind=="spectral_arrow"||kind=="tipped_arrow"||kind=="trident"||kind=="firework";
    private static bool Seed(string seed) {
        if(seed.Length!=16)return false;
        foreach(char c in seed)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;
        return true;
    }
    private static bool WeaponKind(string kind)=>kind=="none"||kind=="melee"||kind=="bow"||kind=="crossbow"||kind=="spear"||kind=="trident";
    private static bool Motion(Dictionary<string,object> msg)=>
        !(msg.ContainsKey("vx")||msg.ContainsKey("vy")||msg.ContainsKey("vz")) ||
        Number(msg,"vx")&&Number(msg,"vy")&&Number(msg,"vz")&&Math.Abs(D(msg,"vx"))<=10&&Math.Abs(D(msg,"vy"))<=10&&Math.Abs(D(msg,"vz"))<=10;
    private static bool WeaponState(Dictionary<string,object> msg)=>!msg.ContainsKey("weaponKind") ||
        (!msg.ContainsKey("eyeHeight")||Number(msg,"eyeHeight")&&D(msg,"eyeHeight")>0&&D(msg,"eyeHeight")<=4)&&
        WeaponKind(S(msg,"weaponKind"))&&WeaponKind(S(msg,"offhandWeaponKind"))&&Integer(msg,"useTicks")&&L(msg,"useTicks")>=0&&L(msg,"useTicks")<=72000&&
        Number(msg,"crossbowCharge")&&D(msg,"crossbowCharge")>=0&&D(msg,"crossbowCharge")<=1&&Boolean(msg,"crossbowLoaded")&&Boolean(msg,"crossbowFirework")&&
        Boolean(msg,"offhandCrossbowLoaded")&&Boolean(msg,"offhandCrossbowFirework");
    private static bool Face(Dictionary<string,object> msg)=>Integer(msg,"face") && L(msg,"face")>=0 && L(msg,"face")<=5;
    private static bool WorldPosition(Dictionary<string,object> msg)=>Number(msg,"x")&&Number(msg,"y")&&Number(msg,"z")&&
        Math.Abs(D(msg,"x"))<=29999984&&Math.Abs(D(msg,"z"))<=29999984&&D(msg,"y")>=-2048&&D(msg,"y")<=2048;
    private static bool GroundPatch(Dictionary<string,object> msg)
    {
        if(msg.ContainsKey("surfaceH")&&(!Number(msg,"surfaceH")||D(msg,"surfaceH")<0||D(msg,"surfaceH")>1))return false;
        if(!msg.ContainsKey("groundCount"))return true;
        if(!Integer(msg,"groundCount")||L(msg,"groundCount")<0||L(msg,"groundCount")>81)return false;
        for(var i=0;i<L(msg,"groundCount");i++)
        {
            if(!Integer(msg,"ground"+i+"X")||!Integer(msg,"ground"+i+"Y")||!Integer(msg,"ground"+i+"Z")||
               Math.Abs(D(msg,"ground"+i+"X"))>=30000000||Math.Abs(D(msg,"ground"+i+"Z"))>=30000000||
               D(msg,"ground"+i+"Y")< -2048||D(msg,"ground"+i+"Y")>2048)return false;
            if(msg.ContainsKey("ground"+i+"H")&&(!Number(msg,"ground"+i+"H")||D(msg,"ground"+i+"H")<0||D(msg,"ground"+i+"H")>1))return false;
        }
        return true;
    }
    private static bool OptionalHurtPose(Dictionary<string,object> msg)=>
        !(msg.ContainsKey("x")||msg.ContainsKey("y")||msg.ContainsKey("z")||msg.ContainsKey("yaw")||msg.ContainsKey("pitch")) ||
        Pose(msg) && Number(msg,"pitch") && Math.Abs(D(msg,"yaw"))<=float.MaxValue &&
        Math.Abs(D(msg,"x"))<=29999984 && Math.Abs(D(msg,"z"))<=29999984 && D(msg,"y")>=-2048 && D(msg,"y")<=2048;
    private static bool OptionalDamageOrigin(Dictionary<string,object> msg)
    {
        bool any=msg.ContainsKey("sourceX")||msg.ContainsKey("sourceY")||msg.ContainsKey("sourceZ");
        if(!msg.ContainsKey("hasSourcePosition"))return !any;
        if(!Boolean(msg,"hasSourcePosition"))return false;
        if(!BridgeJson.B(msg,"hasSourcePosition"))return !any;
        return Number(msg,"sourceX")&&Number(msg,"sourceY")&&Number(msg,"sourceZ") &&
            Math.Abs(D(msg,"sourceX"))<=29999984&&Math.Abs(D(msg,"sourceZ"))<=29999984&&D(msg,"sourceY")>=-2048&&D(msg,"sourceY")<=2048;
    }
    private static bool HitCoordinate(Dictionary<string,object> msg,string cell,string hit)=>Number(msg,hit) &&
        D(msg,hit)>=D(msg,cell)-.001 && D(msg,hit)<=D(msg,cell)+1.001;
    private static bool InventorySlot(Dictionary<string,object> message,string key)=>Integer(message,key) && (L(message,key)>=0 && L(message,key)<=35 || L(message,key)==40);
    private static bool BlockCoordinates(Dictionary<string,object> message)=>Integer(message,"x") && Integer(message,"y") && Integer(message,"z") &&
        Math.Abs(D(message,"x"))<30000000 && Math.Abs(D(message,"z"))<30000000 && D(message,"y")>=-2048 && D(message,"y")<=2048;
    private static bool Text(Dictionary<string, object> msg, string key, int max = 128)
        => msg.TryGetValue(key, out var value) && value is string text && text.Length > 0 && text.Length <= max;
    private static bool Integer(Dictionary<string, object> msg, string key)
        => msg.TryGetValue(key, out var value) && (value is long || value is int);
    private static bool Number(Dictionary<string, object> msg, string key)
        => msg.TryGetValue(key, out var value) && (value is int || value is long
                || value is double number && !double.IsNaN(number) && !double.IsInfinity(number));
    private static bool Boolean(Dictionary<string, object> msg, string key)
        => msg.TryGetValue(key, out var value) && value is bool;
    private static bool ActorId(Dictionary<string, object> msg, string key)
        => Integer(msg, key) && L(msg, key) >= 0 && L(msg, key) <= int.MaxValue;

    public static long L(Dictionary<string, object> o, string key, long defaultValue = 0)
        => BridgeJson.L(o, key, defaultValue);

    public static double D(Dictionary<string, object> o, string key, double defaultValue = 0.0)
        => BridgeJson.D(o, key, defaultValue);

    public static string S(Dictionary<string, object> o, string key, string defaultValue = "")
        => BridgeJson.S(o, key, defaultValue);
}
