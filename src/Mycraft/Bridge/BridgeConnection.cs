using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Mycraft.Bridge;

/// <summary>Loopback-only transport. Each start owns isolated queues and workers.</summary>
public sealed class BridgeConnection : IDisposable
{
    public enum BridgeState { Idle, Listening, Connected, Closed }

    private sealed class Run
    {
        public readonly TcpListener Listener;
        public readonly string? GreetingVersion;
        public readonly SessionInboundBuffer Inbound = new SessionInboundBuffer();
        public readonly SessionOutboundBuffer Outbound = new SessionOutboundBuffer();
        public readonly object SendLock = new object();
        public readonly object WriteLock = new object();
        public readonly object CloseLock = new object();
        public TcpClient Client = null!;
        public NetworkStream Stream = null!;
        public Thread AcceptThread = null!, ReaderThread = null!, WriterThread = null!;
        public Timer Timer = null!;
        public long Seq, WireSeq, LastInboundSeq, LastRx = Stopwatch.GetTimestamp();
        public int Closed, Writing, Failing;
        public string FailureReason = "";
        public volatile bool HelloSent;
        public volatile BridgeState State = BridgeState.Listening;
        public Run(TcpListener listener,string? greetingVersion) { Listener = listener; GreetingVersion=greetingVersion; }
    }

    private readonly object _lifecycle = new object();
    private volatile Run _run = null!;
    private bool _disposed;
    private string _lastError = "";
    private long _droppedInbound, _droppedOutbound;

    public BridgeState State => _run == null ? BridgeState.Idle : _run.State;
    public string LastError => _lastError;
    public long DroppedInbound => Interlocked.Read(ref _droppedInbound);
    public long DroppedOutbound => Interlocked.Read(ref _droppedOutbound);
    public long CoalescedInbound => _run == null ? 0 : _run.Inbound.CoalescedCount;
    public long CoalescedOutbound => _run == null ? 0 : _run.Outbound.CoalescedCount;
    internal long LastInboundSequence => _run == null ? 0 : Interlocked.Read(ref _run.LastInboundSeq);
    public long NextSeq() => _run == null ? 1 : Interlocked.Increment(ref _run.Seq);
    public bool GreetingSent=>_run?.HelloSent==true;

    public bool Start(int port, out string error,string? greetingVersion=null)
    {
        lock (_lifecycle)
        {
            error = "";
            if (_disposed) { error = "connection disposed"; return false; }
            if (_run != null && _run.State != BridgeState.Closed)
            { error = "connection already started"; return false; }
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); }
            catch (SocketException e)
            {
                error = "port_busy: " + port + " (" + e.SocketErrorCode + ")";
                _lastError = error;
                return false;
            }
            var run = new Run(listener,greetingVersion);
            _run = run;
            _lastError = "";
            run.AcceptThread = new Thread(() => AcceptLoop(run)) { IsBackground = true, Name = "MycraftBridge.Accept" };
            run.WriterThread = new Thread(() => WriterLoop(run)) { IsBackground = true, Name = "MycraftBridge.Writer" };
            run.AcceptThread.Start();
            run.WriterThread.Start();
            run.Timer = new Timer(_ => TimerTick(run), null, DemoProtocol.HeartbeatMs, DemoProtocol.HeartbeatMs);
            if (Volatile.Read(ref run.Closed) != 0) run.Timer.Dispose();
            return true;
        }
    }

    public bool TryTakeInbound(out Dictionary<string, object> msg)
    {
        var run = _run;
        msg = null!;
        return run != null && run.State == BridgeState.Connected && run.Inbound.TryDequeue(out msg);
    }

    public bool Send(Dictionary<string, object> msg)
    {
        var run = _run;
        return run != null && Send(run, msg);
    }

    private bool Send(Run run, Dictionary<string, object> msg)
    {
        if (msg == null || run.State != BridgeState.Connected || Volatile.Read(ref run.Closed) != 0 || Volatile.Read(ref run.Failing)!=0) return false;
        string code="",failure="";
        lock (run.SendLock)
        {
            if (run.State != BridgeState.Connected || Volatile.Read(ref run.Closed) != 0 || Volatile.Read(ref run.Failing)!=0) return false;
            // Allocate sequence numbers at the enqueue point, preserving wire order.
            var copy = new Dictionary<string, object>(msg);
            copy["seq"] = Interlocked.Increment(ref run.WireSeq);
            if (!DemoProtocol.ValidateMessage(copy)) { code="protocol";failure="invalid outbound message"; }
            else
            {
                var line = BridgeJson.Write(copy);
                if (Encoding.UTF8.GetByteCount(line) + 1 > DemoProtocol.MaxLineBytes)
                {code="overflow";failure="outbound line exceeds 64KiB";}
                else if(run.Outbound.TryEnqueue(copy,line,out failure))
                {
                    if (DemoProtocol.S(copy, "type") == "hello") run.HelloSent = true;
                    return true;
                }
                else
                {
                    code=failure.Contains("source actor")?"protocol":"overflow";
                    Interlocked.Increment(ref _droppedOutbound);
                }
            }
        }
        // Failure never waits for WriteLock/socket on a gameplay producer.
        Fail(run,code,failure);return false;
    }

    public bool WaitForOutboundDrain(int timeoutMs)
    {
        var run = _run;
        if (run == null) return false;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (run.State != BridgeState.Connected) return false;
            if (run.Outbound.Count == 0 && Volatile.Read(ref run.Writing) == 0) return true;
            Thread.Sleep(2);
        }
        return false;
    }

    public void Close(string reason)
    {
        var run = _run;
        if (run != null) CloseRun(run, reason);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            Close("disposed");
        }
    }

    private void CloseRun(Run run, string reason)
    {
        lock (run.CloseLock)
        {
            if (Interlocked.Exchange(ref run.Closed, 1) != 0) return;
            if (ReferenceEquals(_run, run)) _lastError = run.FailureReason.Length>0?run.FailureReason:reason ?? "";
            try { run.Timer?.Dispose(); } catch { }
            try { run.Listener.Stop(); } catch { }
            try { run.Client?.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { run.Client?.Close(); } catch { }
            run.Inbound.Clear();
            run.Outbound.Clear();
            // Publish Closed only after releasing the listen port and discarding old queues.
            run.State = BridgeState.Closed;
        }
    }

    private void AcceptLoop(Run run)
    {
        try
        {
            while (Volatile.Read(ref run.Closed) == 0)
            {
                var pending = run.Listener.AcceptTcpClient();
                lock (run.WriteLock)
                {
                    if (run.Client != null || Volatile.Read(ref run.Closed) != 0)
                    { pending.Close(); continue; }
                    pending.NoDelay = true;
                    pending.SendTimeout = DemoProtocol.TimeoutMs;
                    run.Client = pending;
                    run.Stream = pending.GetStream();
                    Interlocked.Exchange(ref run.LastRx, Stopwatch.GetTimestamp());
                    run.State = BridgeState.Connected;
                }
                run.ReaderThread = new Thread(() => ReaderLoop(run)) { IsBackground = true, Name = "MycraftBridge.Rx" };
                run.ReaderThread.Start();
                if(run.GreetingVersion!=null)Send(run,DemoProtocol.Hello(run.GreetingVersion));
            }
        }
        catch (Exception e)
        {
            if (Volatile.Read(ref run.Closed) == 0) CloseRun(run, "accept io error: " + e.Message);
        }
    }

    private void ReaderLoop(Run run)
    {
        var line = new byte[DemoProtocol.MaxLineBytes];
        var chunk = new byte[4096];
        var length = 0;
        try
        {
            while (Volatile.Read(ref run.Closed) == 0)
            {
                var read = run.Stream.Read(chunk, 0, chunk.Length);
                if (read == 0) { CloseRun(run, "remote closed"); return; }
                for (var i = 0; i < read && Volatile.Read(ref run.Closed) == 0; i++)
                {
                    if (length >= DemoProtocol.MaxLineBytes - 1 && chunk[i] != (byte)'\n')
                    { DrainAvailable(run); Fail(run, "overflow", "line exceeds 64KiB"); return; }
                    if (chunk[i] != (byte)'\n') { line[length++] = chunk[i]; continue; }
                    var text = new UTF8Encoding(false, true).GetString(line, 0, length);
                    length = 0;
                    if (!BridgeJson.TryParse(text, out var msg) || !DemoProtocol.ValidateMessage(msg))
                    { Fail(run, "protocol", "malformed json or envelope"); return; }
                    var seq = DemoProtocol.L(msg, "seq");
                    if (seq <= run.LastInboundSeq)
                    { Fail(run, "protocol", "sequence must increase; retries need a fresh seq"); return; }
                    Interlocked.Exchange(ref run.LastInboundSeq, seq);
                    Interlocked.Exchange(ref run.LastRx, Stopwatch.GetTimestamp());
                    if (BridgeJson.S(msg, "type") == "error" && BridgeJson.S(msg, "code") != "stale_session")
                    {
                        CloseRun(run, "peer error: " + BridgeJson.S(msg, "code") + " " + BridgeJson.S(msg, "message"));
                        return;
                    }
                    if (!run.Inbound.TryEnqueue(msg))
                    {
                        Interlocked.Increment(ref _droppedInbound);
                        Fail(run, "overflow", "inbound stream full: reliable="+run.Inbound.ReliableCount+", snapshots="+run.Inbound.SnapshotCount);
                        return;
                    }
                }
            }
        }
        catch (DecoderFallbackException) { Fail(run, "protocol", "invalid UTF-8"); }
        catch (Exception e)
        { if (Volatile.Read(ref run.Closed) == 0) CloseRun(run, "rx io error: " + e.Message); }
    }

    private void WriterLoop(Run run)
    {
        while (Volatile.Read(ref run.Closed) == 0)
        {
            Interlocked.Exchange(ref run.Writing, 1);
            if (run.Outbound.TryDequeue(out var frame))
            {
                WriteNow(run, frame.Line);
                if(frame.CloseReason.Length>0)CloseRun(run,frame.CloseReason);
            }
            else
            {
                Interlocked.Exchange(ref run.Writing, 0);
                Thread.Sleep(2);
            }
            Interlocked.Exchange(ref run.Writing, 0);
        }
    }

    private void WriteNow(Run run, string line)
    {
        lock (run.WriteLock)
        {
            if (Volatile.Read(ref run.Closed) != 0 || run.Stream == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                run.Stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e) { CloseRun(run, "tx io error: " + e.Message); }
        }
    }

    private void Fail(Run run, string code, string message)
    {
        lock(run.SendLock)
        {
            if(Volatile.Read(ref run.Closed)!=0 || Volatile.Read(ref run.Failing)!=0)return;
            run.FailureReason=code+": "+message;
            if(ReferenceEquals(_run,run))_lastError=run.FailureReason;
            Volatile.Write(ref run.Failing,1);
            var line=BridgeJson.Write(DemoProtocol.Error("",Interlocked.Increment(ref run.WireSeq),code,message));
            // The only writer finishes its one in-flight lower sequence, then
            // writes this terminal highest sequence and closes. No late old
            // frame can overtake ERROR; blocked writes obey the socket timeout.
            run.Outbound.Terminate(line,run.FailureReason);
        }
    }

    private static void DrainAvailable(Run run)
    {
        // Discard a bounded amount of already-buffered excess input before the error FIN.
        // Closing a socket with unread bytes can reset it and hide the diagnostic frame.
        var discarded = 0;
        var buffer = new byte[4096];
        while (discarded < DemoProtocol.MaxLineBytes && run.Stream.DataAvailable)
            discarded += run.Stream.Read(buffer, 0, buffer.Length);
    }

    private void TimerTick(Run run)
    {
        if (run.State != BridgeState.Connected) return;
        var ageMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref run.LastRx)) * 1000.0 / Stopwatch.Frequency;
        if (ageMs > DemoProtocol.TimeoutMs) { CloseRun(run, "heartbeat timeout"); return; }
        if (run.HelloSent) Send(run, DemoProtocol.Msg("heartbeat", "", 1));
    }
}

internal sealed class BoundedQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
    private readonly object _gate = new object();
    public int Count { get { lock (_gate) return _queue.Count; } }
    public bool TryEnqueueBounded(T item, int capacity)
    {
        lock (_gate)
        {
            if (_queue.Count >= capacity) return false;
            _queue.Enqueue(item);
            return true;
        }
    }
    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            if (_queue.TryDequeue(out var value)) { item = value!; return true; }
            item = default!;
            return false;
        }
    }
    public void Clear() { lock (_gate) { while (_queue.TryDequeue(out _)) { } } }
}

public static class MycraftBridgeLog
{
    public static Action<string> InfoSink = message => Trace.TraceInformation("[Mycraft.Bridge] {0}", message);
    public static Action<string> FailureSink = message => Trace.TraceError("[Mycraft.Bridge] {0}", message);
    public static void Info(string message) => InfoSink(message);
    public static void Failure(string where, Exception e) => FailureSink(where + " failed: " + e.Message);
}
