using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mycraft.Bridge;

namespace Mycraft.UI;

/// <summary>Bounded local transport; all disk work runs on one background worker.</summary>
internal sealed class VanillaInventoryTransport : IDisposable
{
    internal const int HeaderSize=72, MaxWidth=1280, MaxHeight=720, MaxEvents=256;
    internal const int Magic=0x3149434d;
    internal sealed class Frame
    {
        internal int Width,Height;
        internal long Time,Acknowledged,Number;
        internal bool Open;
        internal byte[] Pixels=Array.Empty<byte>();
    }
    private static readonly object StartGate=new object();
    private static Task _previous=Task.CompletedTask;
    private readonly string _session,_token=Guid.NewGuid().ToString("N");
    private readonly object _gate=new object();
    private readonly List<Dictionary<string,object>> _events=new List<Dictionary<string,object>>();
    private readonly Task _worker;
    private volatile bool _stopping;
    private Frame? _latest;
    private long _sequence,_lastRead,_displayedFrame;
    private volatile bool _failed,_remoteClosed;
    private long _lastFrameTime;
    public bool Failed=>_failed;
    public bool RemoteClosed=>_remoteClosed;
    public long LastFrameTime=>Interlocked.Read(ref _lastFrameTime);

    internal static bool ValidId(string value)
    {
        if(value==null || value.Length!=32)return false;
        foreach(char c in value)if(!(c>='0' && c<='9') && !(c>='a' && c<='f'))return false;
        return true;
    }
    public VanillaInventoryTransport(string session)
    {
        if(!ValidId(session))throw new ArgumentException("Invalid inventory session",nameof(session));
        _session=session;
        lock(StartGate)
        {
            var previous=_previous;
            _worker=Task.Run(async()=>{try{await previous.ConfigureAwait(false);}catch{}await Run().ConfigureAwait(false);});
            _previous=_worker;
        }
    }
    public void Enqueue(Dictionary<string,object> input)
    {
        lock(_gate)
        {
            if(_stopping || Failed)return;
            if(_events.Count>=MaxEvents){_failed=true;_stopping=true;return;}
            input["seq"]=++_sequence;
            input["frame"]=Interlocked.Read(ref _displayedFrame);
            _events.Add(input);
        }
    }
    public Frame? TakeFrame()=>Interlocked.Exchange(ref _latest,null);
    public void MarkDisplayed(long number)=>Interlocked.Exchange(ref _displayedFrame,number);
    private string Envelope(bool open)
    {
        var root=BridgeJson.Write(new Dictionary<string,object>{{"sid",_session},{"token",_token},{"open",open},{"time",Now()}});
        var result=new StringBuilder(root.Substring(0,root.Length-1)).Append(",\"events\":[");
        lock(_gate)
            for(int i=0;i<_events.Count;i++){if(i>0)result.Append(',');result.Append(BridgeJson.Write(_events[i]));}
        return result.Append("]}").ToString();
    }
    private async Task Run()
    {
        string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Mycraft","vanilla-ui",_session);
        try
        {
            Directory.CreateDirectory(dir);
            while(!_stopping)
            {
                try
                {
                Publish(Path.Combine(dir,"input.json"),Encoding.UTF8.GetBytes(Envelope(true)));
                var path=Path.Combine(dir,"frame.bin");
                if(File.Exists(path))
                {
                    Frame? frame;
                    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                        frame=ReadFrame(stream,_token,Now(),_lastRead);
                    if(frame!=null && frame.Number>_lastRead)
                    {
                        _lastRead=frame.Number;
                        lock(_gate)_events.RemoveAll(e=>(long)e["seq"]<=frame.Acknowledged);
                        Interlocked.Exchange(ref _lastFrameTime,frame.Time);
                        if(!frame.Open){_remoteClosed=true;break;}
                        Interlocked.Exchange(ref _latest,frame);
                    }
                }
                }
                catch(IOException){ /* Atomic file replacement can briefly contend on Windows; expiry is handled by the UI. */ }
                await Task.Delay(33).ConfigureAwait(false);
            }
        }
        catch(Exception){_failed=true;}
        finally
        {
            try{Publish(Path.Combine(dir,"input.json"),Encoding.UTF8.GetBytes(Envelope(false)));}catch{}
            Interlocked.Exchange(ref _latest,null);
            try{File.Delete(Path.Combine(dir,"input.json.tmp"));}catch{}
        }
    }
    internal static Frame? ReadFrame(Stream stream,string token,long now,long minimumNumber=0)
    {
        if(stream.Length<HeaderSize || stream.Length>HeaderSize+(long)MaxWidth*MaxHeight*4)return null;
        using(var reader=new BinaryReader(stream,Encoding.ASCII,true))
        {
            if(reader.ReadInt32()!=Magic)return null;
            int width=reader.ReadInt32(),height=reader.ReadInt32();
            long time=reader.ReadInt64(),ack=reader.ReadInt64(),number=reader.ReadInt64();
            string id=Encoding.ASCII.GetString(reader.ReadBytes(32));
            int flags=reader.ReadInt32();
            if(id!=token || time<now-3000 || time>now+1000 || ack<0 || number<=minimumNumber || flags<0 || flags>1)return null;
            if(flags==0)return width==0 && height==0 && stream.Length==HeaderSize?new Frame{Time=time,Acknowledged=ack,Number=number}:null;
            if(width<1 || height<1 || width>MaxWidth || height>MaxHeight || stream.Length!=HeaderSize+(long)width*height*4)return null;
            byte[] pixels=reader.ReadBytes(width*height*4);
            if(pixels.Length!=width*height*4)return null;
            return new Frame{Width=width,Height=height,Time=time,Acknowledged=ack,Number=number,Open=true,Pixels=pixels};
        }
    }
    private static void Publish(string path,byte[] bytes)
    {
        if(bytes.Length>131072)throw new IOException("Inventory input limit");
        string temp=path+".tmp";
        File.WriteAllBytes(temp,bytes);
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    internal static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public void Dispose(){_stopping=true;lock(_gate)_events.Clear();}
}
