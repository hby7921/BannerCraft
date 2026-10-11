using System;

namespace Mycraft.Bridge;

/// <summary>
/// Planar transform: BL horizontal (x,y) maps to MC (x,z); BL vertical z is discarded.
/// Inverse returns horizontal x,y and zero vertical z. Positive BL yaw mirrors MC yaw.
/// </summary>
public sealed class DemoTransform
{
    public double Scale { get; }
    public double OffsetX { get; }
    public double OffsetZ { get; }
    public double GroundY { get; }
    public double YawOffsetDeg { get; }

    public DemoTransform(double scale, double offsetX, double offsetZ, double groundY, double yawOffsetDeg)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (!Finite(offsetX) || !Finite(offsetZ) || !Finite(groundY) || !Finite(yawOffsetDeg))
            throw new ArgumentException("Transform parameters must be finite");
        Scale = scale;
        OffsetX = offsetX;
        OffsetZ = offsetZ;
        GroundY = groundY;
        YawOffsetDeg = yawOffsetDeg;
    }

    public static DemoTransform Default(double groundY)
        => new DemoTransform(1.0, 0.0, 0.0, groundY, 0.0);

    public (double X, double Y, double Z, double Yaw) BlToMc(double x, double y, double z, double yaw)
    {
        return (x * Scale + OffsetX, GroundY, y * Scale + OffsetZ, -yaw + YawOffsetDeg);
    }

    public (double X, double Y, double Z, double Yaw) McToBl(double x, double y, double z, double yaw)
    {
        return ((x - OffsetX) / Scale, (z - OffsetZ) / Scale, 0.0, -(yaw - YawOffsetDeg));
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
