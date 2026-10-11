using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

public sealed class McInventoryState
{
    public long Revision {get;private set;}
    public bool Creative {get;private set;}
    public int SelectedSlot {get;private set;}
    public string[] Items {get;}=new string[41];
    public int[] Counts {get;}=new int[41];

    public static McInventoryState Read(Dictionary<string,object> message)
    {
        var state=new McInventoryState();
        state.Revision=ReadInt(message,"rev");
        if(!message.TryGetValue("creative",out var creative) || !(creative is bool))throw new FormatException("Boolean creative mode required");
        state.Creative=(bool)creative;
        state.SelectedSlot=(int)ReadInt(message,"selectedSlot");
        if(state.Revision<1 || state.SelectedSlot<0 || state.SelectedSlot>8)throw new FormatException("Invalid inventory revision/selection");
        for(var i=0;i<41;i++)
        {
            var prefix=i<36?"inv"+i:i<40?"armor"+(i-36):"offhand";
            var item=BridgeJson.S(message,prefix+"Item");
            var count=ReadInt(message,prefix+"Count");
            if(item.Length==0 || item.Length>256 || count<0 || count>9999 || (item=="minecraft:air")!=(count==0))
                throw new FormatException("Invalid inventory cell " + i);
            state.Items[i]=item;state.Counts[i]=(int)count;
        }
        return state;
    }
    private static long ReadInt(Dictionary<string,object> message,string key)
    {
        if(!message.TryGetValue(key,out var value) || !(value is long || value is int))throw new FormatException("Integer required: "+key);
        return BridgeJson.L(message,key);
    }
}
