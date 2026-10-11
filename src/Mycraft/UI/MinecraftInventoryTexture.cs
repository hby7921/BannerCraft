using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.TwoDimension;

namespace Mycraft.UI;

internal sealed class MinecraftInventoryTexture : IDisposable
{
    private TaleWorlds.Engine.Texture? _texture;
    private int _index;
    public string Update(VanillaInventoryTransport.Frame frame)
    {
        TaleWorlds.Engine.Texture? next=TaleWorlds.Engine.Texture.CreateFromByteArray(frame.Pixels,frame.Width,frame.Height);
        try
        {
            next.SetTextureAsAlwaysValid();
            string name="mycraft_inventory_native_"+(_index++%2);
            var category=new SpriteCategory(name,1){SheetSizes=new[]{new Vec2i(frame.Width,frame.Height)}};
            category.InitializePartialLoad();
            category.SpriteSheets[0]=new Texture(new EngineTexture(next));
            var part=new SpritePart(name,category,frame.Width,frame.Height){SheetID=1,SheetX=0,SheetY=0};
            part.UpdateInitValues();
            UIResourceManager.SpriteData.Sprites[name]=new SpriteGeneric(name,part,in SpriteNinePatchParameters.Empty);
            var old=_texture;_texture=next;next=null;
            old?.ReleaseAfterNumberOfFrames(3);
            return name;
        }
        finally {next?.ReleaseAfterNumberOfFrames(3);}
    }
    public void Dispose()
    {
        UIResourceManager.SpriteData.Sprites.Remove("mycraft_inventory_native_0");
        UIResourceManager.SpriteData.Sprites.Remove("mycraft_inventory_native_1");
        _texture?.ReleaseAfterNumberOfFrames(3);_texture=null;
    }
}
