using System;
using System.Collections.Generic;
using System.IO;
using BinaryReader = System.IO.BinaryReader;
using System.Runtime.CompilerServices;
using Mycraft.Building;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Mycraft.Visuals;

internal sealed class OriginalEntityTexture
{
    public readonly byte[] Pixels;
    public readonly int Width,Height;
    public OriginalEntityTexture(string path)
    {
        using(var stream=File.OpenRead(path))using(var r=new BinaryReader(stream))
        {Width=r.ReadInt32();Height=r.ReadInt32();if(Width<1||Width>256||Height<1||Height>256)throw new FormatException("Invalid entity texture size");Pixels=r.ReadBytes(Width*Height*4);if(Pixels.Length!=Width*Height*4||stream.Position!=stream.Length)throw new FormatException("Incomplete original entity texture");}
    }
}

internal sealed class EntityVisualAsset
{
    public readonly OriginalEntityModel Model;
    public readonly OriginalEntityTexture Texture;
    public readonly VoxelGeometry[] Parts;
    public EntityVisualAsset(OriginalEntityModel model,OriginalEntityTexture texture,bool alreadyCutout=false)
    {Model=model;Texture=texture;Parts=new VoxelGeometry[model.Parts.Length];for(int i=0;i<Parts.Length;i++)Parts[i]=alreadyCutout?model.Parts[i].Geometry:EntityModelCutout.Prepare(model.Parts[i].Geometry,texture.Pixels,texture.Width,texture.Height);}
}

// Mission-specific materials reuse original textures. Removed entities/meshes
// remain retained through NativeSceneResources until the Mission is collectible.
internal sealed class NativeEntityVisual : IDisposable
{
    private sealed class Materials
    {public readonly Dictionary<OriginalEntityTexture,Material> Normal=new Dictionary<OriginalEntityTexture,Material>();public readonly Dictionary<OriginalEntityTexture,Material> Hurt=new Dictionary<OriginalEntityTexture,Material>();}
    private static readonly ConditionalWeakTable<Mission,Materials> Cache=new ConditionalWeakTable<Mission,Materials>();
    private readonly List<(int Part,GameEntity Entity,Mesh Mesh)> _parts=new List<(int,GameEntity,Mesh)>();
    private readonly Material _normal,_hurt;
    private bool _disposed,_isHurt;
    public NativeEntityVisual(Mission mission,EntityVisualAsset asset,bool hurt=false)
    {
        _normal=MaterialFor(mission,asset.Texture,false);_hurt=hurt?MaterialFor(mission,asset.Texture,true):_normal;
        try {for(int i=0;i<asset.Parts.Length;i++)if(asset.Parts[i].Indices.Count>0)_parts.Add((i,Create(mission,asset.Parts[i],_normal,out var mesh),mesh));}
        catch {Dispose();throw;}
    }
    private static Material MaterialFor(Mission mission,OriginalEntityTexture data,bool hurt)
    {
        var list=hurt?Cache.GetValue(mission,_=>new Materials()).Hurt:Cache.GetValue(mission,_=>new Materials()).Normal;
        if(list.TryGetValue(data,out var saved))return saved;
        const int magnify=8;int width=data.Width*magnify,height=data.Height*magnify;
        var rgba=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int src=((y/magnify)*data.Width+x/magnify)*4,dst=(y*width+x)*4;
            for(int c=0;c<4;c++)
            {
                byte channel=data.Pixels[src+c];
                // Vanilla OverlayTexture red pixels are ARGB B2FF0000;
                // entity.fsh mixes overlay RGB with base by overlay alpha.
                if(hurt&&c<3)channel=(byte)((channel*178+(c==0?255:0)*77)/255);
                rgba[dst+c]=channel;
            }
        }
        var resources=NativeSceneResources.For(mission);resources.Check(rgba.Length+2048);
        var texture=Texture.CreateFromByteArray(rgba,width,height);texture.SetTextureAsAlwaysValid();resources.Hold(texture,rgba.Length);
        var material=Material.GetDefaultMaterial().CreateCopy();material.SetEnableSkinning(false);material.UsingDynamicLight=true;material.SetTexture(Material.MBTextureType.DiffuseMap,texture);resources.Hold(material,1024);
        list.Add(data,material);return material;
    }
    private static GameEntity Create(Mission mission,VoxelGeometry geometry,Material material,out Mesh mesh)
    {
        var resources=NativeSceneResources.For(mission);resources.Check(geometry.Vertices.Count*48L+2048);
        mesh=MinecraftModelAssets.Mesh(geometry,material);resources.Hold(mesh,geometry.Vertices.Count*48L);
        var entity=GameEntity.CreateEmpty(mission.Scene,false,false,false);resources.Hold(entity,1024);
        try {entity.SetVisibilityExcludeParents(false);entity.AddMesh(mesh);return entity;}catch {entity.Remove(0);throw;}
    }
    public void Update(EntityMatrix[] frames,Vec3 origin,float scale,bool hurt=false)
    {
        if(_disposed)return;
        foreach(var p in _parts)
        {
            if(_isHurt!=hurt)p.Mesh.SetMaterial(hurt?_hurt:_normal);
            p.Entity.SetGlobalFrame(Frame(frames[p.Part],origin,scale));p.Entity.SetVisibilityExcludeParents(true);
        }
        _isHurt=hurt;
    }
    public static MatrixFrame Frame(EntityMatrix matrix,Vec3 origin,float scale)
    {
        var m=matrix.HostValues(scale);var rotation=Mat3.Identity;
        rotation.s=new Vec3(m[0],m[4],m[8]);rotation.f=new Vec3(m[1],m[5],m[9]);rotation.u=new Vec3(m[2],m[6],m[10]);
        return new MatrixFrame(rotation,origin+new Vec3(m[3],m[7],m[11]));
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var p in _parts){p.Entity.SetVisibilityExcludeParents(false);p.Entity.Remove(0);}
        _parts.Clear();
    }
}
