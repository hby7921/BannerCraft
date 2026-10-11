using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Mycraft.Demo;
using Mycraft.Patches;
using TaleWorlds.Engine.GauntletUI;

namespace Mycraft.UI;

internal static class MinecraftHudSprites
{
    private sealed class Sheet
    {
        public byte[] Pixels = Array.Empty<byte>();
        public int Width;
        public int Height;
    }
    private static readonly List<Sheet> Sheets = new List<Sheet>();

    // Cache disk data during module startup, never from the HUD frame loop.
    public static void Cache(string moduleFolder)
    {
        Sheets.Clear();
        try
        {
            var metadata = new XmlDocument();
            metadata.Load(Path.Combine(moduleFolder, "GUI", "MycraftSpriteData.xml"));
            foreach (XmlNode node in metadata.SelectNodes("/SpriteData/SpriteCategories/SpriteCategory/SpriteSheetSize")!)
            {
                var width = int.Parse(node.Attributes!["Width"]!.Value);
                var height = int.Parse(node.Attributes["Height"]!.Value);
                var id = int.Parse(node.Attributes["ID"]!.Value);
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || id != Sheets.Count + 1 || id > 8)
                    throw new FormatException("Invalid HUD atlas dimensions/order");
                var pixels = File.ReadAllBytes(Path.Combine(moduleFolder, "GUI", "SpriteSheets", "MycraftHUD", "MycraftHUD_" + id + ".rgba"));
                if (pixels.Length != width * height * 4) throw new FormatException("HUD atlas size mismatch");
                Sheets.Add(new Sheet { Pixels = pixels, Width = width, Height = height });
            }
        }
        catch (Exception e) { Sheets.Clear(); MinecraftHeroState.LogFailure("HUD sprite cache", e); }
    }

    public static void EnsureLoaded()
    {
        var category = UIResourceManager.GetSpriteCategory("MycraftHUD");
        if (category == null || Sheets.Count != category.SpriteSheetCount)
            throw new InvalidOperationException("MycraftHUD sprite data/atlas missing");
        if (!category.IsLoaded) category.InitializePartialLoad();
        for (var i = 0; i < Sheets.Count; i++)
        {
            if (category.SpriteSheets[i] != null) continue;
            var sheet = Sheets[i];
            var texture = TaleWorlds.Engine.Texture.CreateFromByteArray(sheet.Pixels, sheet.Width, sheet.Height);
            texture.SetTextureAsAlwaysValid();
            category.SpriteSheets[i] = new TaleWorlds.TwoDimension.Texture(new EngineTexture(texture));
        }
    }

    public static string ItemSprite(string itemId)
    {
        var name = itemId.StartsWith("minecraft:", StringComparison.Ordinal) ? itemId.Substring(10) : itemId;
        if(name=="crossbow")name="crossbow_standby";
        var sprite = "mycraft_item_" + name.Replace('/', '_');
        return UIResourceManager.SpriteData.SpriteExists(sprite) ? sprite : "mycraft_item_unknown";
    }
}
