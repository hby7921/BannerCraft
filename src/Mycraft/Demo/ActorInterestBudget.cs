using System;
using System.Collections.Generic;
using System.Linq;

namespace Mycraft.Demo;

public static class ActorInterestBudget
{
    public const int Capacity=64;
    public const double Radius=40;
    public static int[] Select(IEnumerable<(int Id,double DistanceSquared)> candidates)=>candidates
        .Where(c=>!double.IsNaN(c.DistanceSquared) && !double.IsInfinity(c.DistanceSquared) && c.DistanceSquared>=0 && c.DistanceSquared<=Radius*Radius)
        .OrderBy(c=>c.DistanceSquared).ThenBy(c=>c.Id).Take(Capacity).Select(c=>c.Id).ToArray();

    public static int[] SelectPrioritized(IEnumerable<(int Id,int Priority,double Score)> candidates,int capacity=Capacity)=>candidates
        .Where(c=>c.Priority>=0&&RangedTargeting.Finite(c.Score)&&c.Score>=0)
        .OrderBy(c=>c.Priority).ThenBy(c=>c.Score).ThenBy(c=>c.Id).Take(Math.Max(1,Math.Min(640,capacity))).Select(c=>c.Id).ToArray();
}
