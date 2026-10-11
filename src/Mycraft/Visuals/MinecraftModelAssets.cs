using System;
using System.Collections.Generic;
using System.IO;
using Mycraft.Patches;
using TaleWorlds.Engine;
using Path = System.IO.Path;

namespace Mycraft.Visuals;

internal static class MinecraftModelAssets
{
    private static byte[]? _skin;
    private static byte[]? _shield;
    private static readonly Dictionary<string, byte[]> Items = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte[]> Weapons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static readonly Dictionary<string, HeldBlockModel> Blocks = new Dictionary<string, HeldBlockModel>(StringComparer.Ordinal);

    public static void Cache(string moduleFolder)
    {
        Items.Clear();
        Weapons.Clear();
        Blocks.Clear();
        _skin = null;
        _shield = null;
        try
        {
            var directory = Path.Combine(moduleFolder, "Visuals");
            var skin = File.ReadAllBytes(Path.Combine(directory, "steve.rgba"));
            if (skin.Length != 64 * 64 * 4) throw new FormatException("Expected 64x64 RGBA skin");
            _skin = skin;
            var shield = File.ReadAllBytes(Path.Combine(directory, "shield.rgba"));
            if (shield.Length != 64 * 64 * 4) throw new FormatException("Expected 64x64 RGBA shield atlas");
            _shield = shield;
            foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "items"), "*.pixels"))
            {
                var data = File.ReadAllBytes(path);
                if (data.Length == 16 * 16 * 4) Items[Path.GetFileNameWithoutExtension(path)] = data;
            }
            var weaponDirectory=Path.Combine(directory,"weapons");
            if(Directory.Exists(weaponDirectory))foreach(var path in Directory.EnumerateFiles(weaponDirectory,"*.rgba"))
            {
                var data=File.ReadAllBytes(path);
                if(data.Length!=32*32*4)throw new FormatException("Expected original 32x32 weapon texture: "+path);
                Weapons[Path.GetFileNameWithoutExtension(path)]=data;
            }
            MinecraftHeroState.Log("model assets cached: custom skin + " + Items.Count + " item textures");
            foreach (var name in Mycraft.Building.BlockGeometry.Items)
            {
                var pixels = File.ReadAllBytes(Path.Combine(directory, "blocks", name + ".rgba"));
                if (pixels.Length != 1024) throw new FormatException("Invalid held block texture: " + name);
                Blocks[name] = HeldBlockModel.Cube(pixels);
            }
            Blocks["tnt"] = HeldBlockModel.BottomTop(File.ReadAllBytes(Path.Combine(directory, "blocks", "tnt.atlas.rgba")));
        }
        catch (Exception e) { MinecraftHeroState.LogFailure("model asset cache", e); }
    }

    public static bool Available => _skin != null;
    public static byte[] Skin => _skin ?? throw new InvalidOperationException("Model skin missing");
    public static byte[]? Shield => _shield;
    public static byte[]? GetWeapon(string name)=>Weapons.TryGetValue(name,out var pixels)?pixels:null;
    public static HeldBlockModel? GetBlock(string name) => Blocks.TryGetValue(name, out var model) ? model : null;
    public static byte[]? GetItem(string name)
    {
        if (name == "crossbow") name = "crossbow_standby";
        return Items.TryGetValue(name, out var pixels) ? pixels : null;
    }

    public static Material Material(byte[] pixels, int size, out Texture texture)
    {
        Mycraft.Demo.DemoLog.Diagnostic("MODEL stage: texture creation " + size);
        // Upscale before uploading; it keeps the authored pixels crisp with native filtering.
        const int scale = 8;
        var dimension = size * scale;
        var enlarged = new byte[dimension * dimension * 4];
        for (var y = 0; y < dimension; y++)
        for (var x = 0; x < dimension; x++)
            Buffer.BlockCopy(pixels, ((y/scale)*size+x/scale)*4, enlarged, (y*dimension+x)*4, 4);
        texture = Texture.CreateFromByteArray(enlarged, dimension, dimension);
        Mycraft.Demo.DemoLog.Diagnostic("MODEL stage: default material copy");
        texture.SetTextureAsAlwaysValid();
        var material = TaleWorlds.Engine.Material.GetDefaultMaterial().CreateCopy();
        Mycraft.Demo.DemoLog.Diagnostic("MODEL stage: disable skinning");
        material.SetEnableSkinning(false);
        material.UsingDynamicLight = true;
        Mycraft.Demo.DemoLog.Diagnostic("MODEL stage: assign texture");
        material.SetTexture(TaleWorlds.Engine.Material.MBTextureType.DiffuseMap, texture);
        return material;
    }

    public static Mesh Mesh(VoxelGeometry geometry, Material material)
    {
        var builder = new MeshBuilder();
        foreach (var vertex in geometry.Vertices)
            builder.AddFaceCorner(new TaleWorlds.Library.Vec3(vertex.X,vertex.Y,vertex.Z),
                new TaleWorlds.Library.Vec3(vertex.Nx,vertex.Ny,vertex.Nz),
                // PNG/RGBA rows start at the top; native 3D mesh V starts at the bottom.
                new TaleWorlds.Library.Vec2(vertex.U,VoxelGeometry.NativeV(vertex.V)), uint.MaxValue);
        for (var i = 0; i < geometry.Indices.Count; i += 3)
            builder.AddFace(geometry.Indices[i],geometry.Indices[i+1],geometry.Indices[i+2]);
        var mesh = builder.Finalize();
        mesh.SetMaterial(material);
        mesh.VisibilityMask = VisibilityMaskFlags.DefaultDynamic;
        mesh.UpdateBoundingBox();
        return mesh;
    }
}
