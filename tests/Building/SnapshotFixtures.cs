using System.IO;

internal static class SnapshotFixtures
{
    // External wire fixture: Minecraft top half-height face, red/transparent
    // two-pixel texture, 0.5-high collision and independently serialized outline.
    internal static byte[] Slab(bool collision=true)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(0x4D43424D);writer.Write(1);writer.Write(1);
        writer.Write(2);writer.Write(1);writer.Write(new byte[]{255,0,0,255,0,255,0,0});
        writer.Write(1);writer.Write(0);writer.Write(0xff559922u);
        foreach(float value in new[]{0,.5f,0,0,0, 0,.5f,1,0,1, 1,.5f,1,1,1, 1,.5f,0,1,0})writer.Write(value);
        writer.Write(0f);writer.Write(1f);writer.Write(0f);
        writer.Write(collision?1:0);
        if(collision)Box(writer);
        writer.Write(1);Box(writer);
        return stream.ToArray();
    }
    private static void Box(BinaryWriter writer)
    {foreach(float value in new[]{0,0,0,1,.5f,1})writer.Write(value);}
    internal static byte[] ChangeInt(byte[] original,int offset,int value)
    {
        var result=(byte[])original.Clone();
        using var stream=new MemoryStream(result);using var writer=new BinaryWriter(stream);
        stream.Position=offset;writer.Write(value);return result;
    }
}
