using System;

namespace Mycraft.Visuals;

// A held block carries its texture schema together with its geometry. Atlas
// images must never accidentally enter the 16x16 flat-item extrusion path.
internal sealed class HeldBlockModel
{
    public byte[] Pixels { get; }
    public int TextureSize { get; }
    private readonly bool _bottomTop;
    private HeldBlockModel(byte[] pixels, int textureSize, bool bottomTop)
    {
        if (pixels == null || pixels.Length != textureSize * textureSize * 4)
            throw new ArgumentException("Held block texture does not match its model schema");
        Pixels = pixels; TextureSize = textureSize; _bottomTop = bottomTop;
    }
    public static HeldBlockModel Cube(byte[] pixels) => new HeldBlockModel(pixels, 16, false);
    public static HeldBlockModel BottomTop(byte[] pixels) => new HeldBlockModel(pixels, 64, true);
    public VoxelGeometry Geometry() => _bottomTop ? VoxelGeometry.HeldBlockBottomTop() : VoxelGeometry.HeldBlock();
}
