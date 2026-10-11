using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

public sealed class McHotbarState
{
    public string[] Items { get; } = new string[9];
    public int[] Counts { get; } = new int[9];
    public int SelectedSlot { get; private set; }
    public int Food { get; private set; } = 20;
    public int Armor { get; private set; }
    public string OffhandItem { get; private set; }="minecraft:air";
    public int OffhandCount { get; private set; }

    private McHotbarState()
    {
        for (var i = 0; i < Items.Length; i++) Items[i] = "minecraft:air";
    }

    public static McHotbarState Read(Dictionary<string, object> message)
    {
        var result = new McHotbarState();
        if(message.ContainsKey("offhandItem"))
        {
            result.OffhandItem=BridgeJson.S(message,"offhandItem");
            if(result.OffhandItem.Length==0 || result.OffhandItem.Length>256)throw new FormatException("Invalid offhand item");
            result.OffhandCount=Integer(message,"offhandCount",0,9999);
            if((result.OffhandItem=="minecraft:air")!=(result.OffhandCount==0))throw new FormatException("Offhand item/count disagree");
        }
        if (!message.ContainsKey("selectedSlot"))
        {
            // Older demo-v1 peers only know the held item, not the remaining slots.
            result.Items[0] = BridgeJson.S(message, "item", "minecraft:air");
            result.Counts[0] = result.Items[0] == "minecraft:air" ? 0 : 1;
            return result;
        }
        result.SelectedSlot = Integer(message, "selectedSlot", 0, 8);
        result.Food = Integer(message, "food", 0, 20);
        result.Armor = Integer(message, "armor", 0, 20);
        for (var i = 0; i < 9; i++)
        {
            var item = BridgeJson.S(message, "slot" + i + "Item");
            if (item.Length == 0 || item.Length > 256) throw new FormatException("Invalid hotbar item");
            result.Items[i] = item;
            result.Counts[i] = Integer(message, "slot" + i + "Count", 0, 9999);
            if ((item == "minecraft:air") != (result.Counts[i] == 0))
                throw new FormatException("Item/count disagree");
        }
        return result;
    }

    private static int Integer(Dictionary<string, object> message, string key, int min, int max)
    {
        if (!message.TryGetValue(key, out var value) || !(value is int || value is long))
            throw new FormatException("Integer required: " + key);
        var number = BridgeJson.L(message, key, -1);
        if (number < min || number > max) throw new FormatException("Out of range: " + key);
        return (int)number;
    }
}
