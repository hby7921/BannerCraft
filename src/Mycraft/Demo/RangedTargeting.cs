using System;

namespace Mycraft.Demo;

// Pure geometry for predicting interest, never a replacement hit/damage rule.
public static class RangedTargeting
{
    public const double MaximumDistance=256;
    public const double LookaheadSeconds=.5;
    public static double SegmentDistanceSquared(double px,double py,double pz,double ax,double ay,double az,
        double bx,double by,double bz,out double along)
    {
        double dx=bx-ax,dy=by-ay,dz=bz-az,length=dx*dx+dy*dy+dz*dz;
        along=length>1e-12?Math.Max(0,Math.Min(1,((px-ax)*dx+(py-ay)*dy+(pz-az)*dz)/length)):0;
        dx=px-ax-dx*along;dy=py-ay-dy*along;dz=pz-az-dz*along;
        return dx*dx+dy*dy+dz*dz;
    }
    public static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
}
