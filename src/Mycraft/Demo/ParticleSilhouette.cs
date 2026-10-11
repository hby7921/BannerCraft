using System;
using System.Collections.Generic;

namespace Mycraft.Demo;

// Original TNT/smoke sprites use binary alpha. Omit transparent pixels from
// geometry so opaque rendering does not depend on native alpha permutations.
public static class ParticleSilhouette
{
    public readonly struct Span
    {
        public readonly int Start,End,Row;
        public Span(int start,int end,int row){Start=start;End=end;Row=row;}
    }
    public static List<Span> Read(byte[] rgba,int size)
    {
        if(size<1||size>32||rgba==null||rgba.Length!=size*size*4)throw new ArgumentException("Invalid particle sprite");
        var spans=new List<Span>();
        for(int y=0;y<size;y++)for(int x=0;x<size;)
        {
            if(rgba[(y*size+x)*4+3]<26){x++;continue;}
            int start=x++;
            while(x<size&&rgba[(y*size+x)*4+3]>=26)x++;
            spans.Add(new Span(start,x,y));
        }
        return spans;
    }
}
