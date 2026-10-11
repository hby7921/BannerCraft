using System;
using Mycraft.Demo;
using System.Linq;

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL " + name);
    count++;
    Console.WriteLine("PASS " + name);
}
var ledger = new CombatEventLedger();
Check(ledger.Resolve("unknown", "e", 3, 1) == CombatEventLedger.Resolution.UnknownAttack, "unsolicited hit rejected");
Check(ledger.TryIssue("a", 1), "attack registered");
Check(ledger.Resolve("a", "e", 3, 100) == CombatEventLedger.Resolution.Accepted, "living target hit accepted");
Check(ledger.Resolve("a", "e", 3, 500) == CombatEventLedger.Resolution.Duplicate, "retry cannot double damage");
Check(ledger.Resolve("a", "e2", 3, 500) == CombatEventLedger.Resolution.UnknownAttack, "second event for one swing rejected");
ledger.TryIssue("b", 10);
Check(ledger.Resolve("b", "b1", double.NaN, 50) == CombatEventLedger.Resolution.Invalid, "NaN damage rejected");
Check(ledger.Resolve("b", "b1", double.PositiveInfinity, 50) == CombatEventLedger.Resolution.Invalid, "infinite damage rejected");
Check(ledger.Resolve("b", "b1", -1, 50) == CombatEventLedger.Resolution.Invalid, "negative damage rejected");
Check(ledger.Resolve("b", "b1", 1, 3011) == CombatEventLedger.Resolution.UnknownAttack, "late hit rejected");
Check(CombatEventLedger.ToBannerlordDamage(0.2, 1) == 1, "fractional damage rounds once");
Check(CombatEventLedger.ToBannerlordDamage(5, 2) == 10, "one conversion factor");
ledger.Clear();
Check(ledger.Resolve("a", "e", 3, 1) == CombatEventLedger.Resolution.UnknownAttack, "new session drops old hit");
var fullSessionValid = true;
for (var i = 0; i < 256; i++)
{
    fullSessionValid &= ledger.TryIssue("a" + i, i);
    fullSessionValid &= ledger.Resolve("a" + i, "e" + i, 3, i) == CombatEventLedger.Resolution.Accepted;
}
Check(fullSessionValid, "256 distinct attacks resolve once each");
ledger.TryIssue("limit", 257);
Check(ledger.Resolve("limit", "limit", 3, 258) == CombatEventLedger.Resolution.CapacityExceeded, "flood within retry window remains bounded");
Check(ledger.TryIssue("long-battle",11000),"long battle attack registered");
Check(ledger.Resolve("long-battle","new-hit",3,11001)==CombatEventLedger.Resolution.Accepted,"expired retry records allow longer battles");
Check(ledger.Resolve("a0","e0",3,11002)==CombatEventLedger.Resolution.UnknownAttack,"retired event cannot reapply consumed attack");
Check(ledger.Resolve("long-battle","new-hit",3,12501)==CombatEventLedger.Resolution.Duplicate,"latest event still acknowledges retries");
Console.WriteLine("ALL " + count + " CHECKS PASSED");
var candidates=Enumerable.Range(1,100).Select(i=>(Id:i,DistanceSquared:(double)i));
var near=ActorInterestBudget.Select(candidates.Reverse());
Check(near.Length==64 && near[0]==1 && near[63]==64,"army may exceed64; nearest64 mirrored");
Check(ActorInterestBudget.Select(new[]{(1,1601d),(2,double.NaN),(3,-1d),(4,1600d)}).SequenceEqual(new[]{4}),"radius40 and finite positions only");
Check(ActorInterestBudget.Select(new[]{(3,1d),(1,1d),(2,1d)}).SequenceEqual(new[]{1,2,3}),"distance ties keep deterministic actor order");
Check(ActorInterestBudget.Select(Array.Empty<(int,double)>()).Length==0,"empty army keeps valid empty interest set");
var crowded=Enumerable.Range(1,128).Select(i=>(Id:i,Priority:3,Score:(double)i)).ToList();
crowded.Add((1000,2,0));crowded.Add((1001,1,0));
var predicted=ActorInterestBudget.SelectPrioritized(crowded);
Check(predicted.Length==64&&predicted[0]==1001&&predicted[1]==1000,
    "distant aimed and ahead-of-flight targets survive a crowded64proxy pool");
Check(!predicted.Contains(128),"ranged interest does not grow the native proxy budget");
var army=ActorInterestBudget.SelectPrioritized(Enumerable.Range(1,200).Select(i=>(Id:i,Priority:3,Score:(double)i*100)),256);
Check(army.Length==200&&army.Distinct().Count()==200&&army.Contains(200),"negotiated army mirrors all200 soldiers beyond40m");
var sixHundred=ActorInterestBudget.SelectPrioritized(Enumerable.Range(1,600).Select(i=>(Id:i,Priority:3,Score:(double)i*100)),640);
Check(sixHundred.Length==600&&sixHundred.Distinct().Count()==600&&sixHundred.Contains(600),"expanded army mirrors every600 soldiers without256 trimming");
Check(ActorInterestBudget.SelectPrioritized(Enumerable.Range(1,700).Select(i=>(Id:i,Priority:3,Score:(double)i)),640).Length==640,
    "expanded actor selection remains bounded640");
Check(ActorInterestBudget.SelectPrioritized(Enumerable.Range(1,300).Select(i=>(Id:i,Priority:3,Score:(double)i)),256).Length==256,
    "army capacity remains bounded to256");
double corridorDistance=RangedTargeting.SegmentDistanceSquared(0,80,1,0,60,1,0,91.5,1,out var futureFraction);
Check(corridorDistance<.001&&futureFraction>0&&futureFraction<1,
    "63m-per-second flight registers80m target before old10m arrival bubble");
Check(RangedTargeting.SegmentDistanceSquared(10,80,1,0,60,1,0,91.5,1,out _)>36,
    "off-path targets do not displace ahead-of-flight proxies");
Check(ActorInterestBudget.SelectPrioritized(new[]{(1,1,double.NaN),(2,2,-1d),(3,1,0d)}).SequenceEqual(new[]{3}),
    "invalid ranged ranking values are excluded");
Console.WriteLine("TOTAL " + count + " DEMO CHECKS PASSED");
Check(ShieldBlockPrediction.CanBounce(true,true,true,50,0,1,0,2),"fresh MC-ready shield predicts current front collision");
Check(!ShieldBlockPrediction.CanBounce(true,true,false,50,0,1,0,2),"raising animation cannot bypass native shield warm-up");
Check(!ShieldBlockPrediction.CanBounce(true,false,true,50,0,1,0,2),"released button cannot keep blocking from stale MC state");
Check(!ShieldBlockPrediction.CanBounce(true,true,true,351,0,1,0,2),"stale shield state does not alter native collision");
Check(!ShieldBlockPrediction.CanBounce(false,true,true,50,0,1,0,2),"unbridged agents retain their collision reaction");
Check(!ShieldBlockPrediction.CanBounce(true,true,true,50,0,1,0,-2),"back hits do not bounce from front shield");
Check(!ShieldBlockPrediction.CanBounce(true,true,true,50,0,1,2,0),"ambiguous exact-side hit stays authoritative without predicted bounce");
Check(!ShieldBlockPrediction.CanBounce(true,true,true,50,0,1,0,0)&&!ShieldBlockPrediction.CanBounce(true,true,true,50,0,1,double.NaN,2),"missing or invalid direction cannot fabricate shield contact");
Console.WriteLine("TOTAL " + count + " DEMO CHECKS PASSED");
