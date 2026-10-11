using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mycraft.Bridge;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Demo;

// Explicit diagnostic launch only. Receipts come from a real Fabric GameTest
// explosion; the native test exercises the production receiver and RegisterBlow.
public sealed partial class DemoBridgeMissionBehavior
{
    private static string[]? _tntSmokeReceipts;
    internal static Mycraft.Building.BlockModelSnapshot? WireOffSmoke,WireOnSmoke;
    internal static void CacheTntDamageSmoke(string moduleFolder)
    {
        if(!Environment.GetCommandLineArgs().Contains("--mycraft-tnt-scene-smoke"))return;
        var path=Path.Combine(moduleFolder,"tnt-native-events.jsonl");
        if(File.Exists(path))_tntSmokeReceipts=File.ReadAllLines(path);
        string off=Path.Combine(moduleFolder,"redstone-wire-power0.bin"),on=Path.Combine(moduleFolder,"redstone-wire-power15.bin");
        if(File.Exists(off)&&File.Exists(on))
        {WireOffSmoke=Mycraft.Building.BlockModelSnapshot.Parse(File.ReadAllBytes(off));WireOnSmoke=Mycraft.Building.BlockModelSnapshot.Parse(File.ReadAllBytes(on));}
    }

    internal static void VerifyTntDamageSmoke(Mission mission)
    {
        if(!Environment.GetCommandLineArgs().Contains("--mycraft-tnt-scene-smoke"))
            throw new InvalidOperationException("TNT damage smoke requires its diagnostic launch flag");
        if(Current!=null)throw new InvalidOperationException("Cannot run damage smoke inside a live bridge session");
        if(_tntSmokeReceipts==null||_tntSmokeReceipts.Length!=2)
            throw new InvalidOperationException("Expected one actual MC explosion and one native damage receipt");
        var messages=new List<Dictionary<string,object>>();
        foreach(var line in _tntSmokeReceipts)
        {
            if(!BridgeJson.TryParse(line,out var message)||!DemoProtocol.ValidateMessage(message))
                throw new InvalidOperationException("Invalid native MC explosion fixture");
            messages.Add(message);
        }
        var blast=messages[0];var hit=messages[1];
        if(DemoProtocol.S(blast,"type")!="explosion"||DemoProtocol.S(hit,"type")!="explosion_hit"||
           DemoProtocol.S(blast,"blastId")!=DemoProtocol.S(hit,"blastId"))
            throw new InvalidOperationException("Native MC fixture blast/hit association is invalid");
        var player=mission.MainAgent;
        var target=mission.Agents.FirstOrDefault(a=>a.IsHuman&&a.IsActive()&&a.Health>0&&player!=null&&a.IsEnemyOf(player));
        if(player==null||!player.IsActive()||target==null)
            throw new InvalidOperationException("Native damage smoke needs a living player and target");
        float nativeDamage=(float)DemoProtocol.D(hit,"damage");
        int expected=CombatEventLedger.ToBannerlordDamage(nativeDamage,1);
        if(expected<=0||expected>=target.HealthLimit)
            throw new InvalidOperationException("Native smoke receipt cannot be applied nonlethally to this target");
        var demo=new DemoBridgeMissionBehavior {_state=DemoState.Active,_player=player,
            _sessionId=DemoProtocol.S(blast,"sid"),_config=new DemoConfig {McHitFactor=1}};
        int eid=(int)DemoProtocol.L(hit,"eid");
        demo._knownActors.Add(eid,target);demo._actorIds.Add(target,eid);
        float originalHealth=target.Health;
        try
        {
            target.Health=target.HealthLimit;float before=target.Health;
            Current=demo;
            demo.ReceiveExplosion(blast);
            demo.ReceiveExplosion(hit);
            float after=target.Health;
            demo.ReceiveExplosion(hit);
            float afterDuplicate=target.Health;
            if(demo._endRequested||Math.Abs(before-after-expected)>.01f||Math.Abs(after-afterDuplicate)>.01f)
                throw new InvalidOperationException("Native damage mismatch: before="+before+", after="+after+
                    ", afterDuplicate="+afterDuplicate+", expected="+expected);
            DemoLog.Diagnostic("TNT_NATIVE_DAMAGE_PASS: actual MC receipt damage="+nativeDamage+
                ", nativeHP="+before+" -> "+after+", duplicateHP="+afterDuplicate+", expectedLoss="+expected);
        }
        finally
        {
            Current=null;
            if(target.IsActive())target.Health=originalHealth;
            demo._inventoryUI.Dispose();
        }
    }
}
