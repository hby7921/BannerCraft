using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Mycraft.Bridge;

namespace MycraftBridgeTests;

internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static void Check(string name, bool condition, string detail = "")
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine("PASS " + name);
            return;
        }
        _failures++;
        Console.WriteLine("FAIL " + name + (detail.Length > 0 ? " :: " + detail : ""));
    }

    private static Dictionary<string, object> RecvLine(NetworkStream stream, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var sb = new StringBuilder();
        stream.ReadTimeout = 50;
        while (Environment.TickCount64 < deadline)
        {
            var buf = new byte[1];
            int b;
            try
            {
                b = stream.ReadByte();
            }
            catch (IOException)
            {
                b = -1;
            }
            if (b < 0)
            {
                Thread.Sleep(10);
                continue;
            }
            var c = (char)b;
            if (c == '\n')
                break;
            sb.Append(c);
        }
        var line = sb.ToString();
        return BridgeJson.TryParse(line, out var obj) ? obj : null!;
    }

    private static void SendLine(NetworkStream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Dictionary<string, object> HelloAck(long seq)
    {
        var hello = DemoProtocol.Msg("hello_ack", "", seq);
        hello["agent"] = "test-mc";
        hello["mcVersion"] = "26.3";
        hello["playerName"] = "Steve";
        return hello;
    }

    private static void RetryTests()
    {
        var retries = new EventRetryTracker(1);
        var hurt = DemoProtocol.PlayerHurt("session", 1, "hurt1", 5.0, 1);
        Check("retry.track", retries.Track("hurt1", hurt, 100));
        hurt["damage"] = 99.0;
        Check("retry.capacity", !retries.Track("hurt2", hurt, 100));
        Check("retry.not_early", retries.TakeDue(599, out var exhausted).Count == 0 && !exhausted);
        var retry1 = retries.TakeDue(600, out exhausted);
        Check("retry.same_event_immutable_payload", retry1.Count == 1 && DemoProtocol.S(retry1[0], "eventId") == "hurt1"
            && DemoProtocol.D(retry1[0], "damage") == 5 && !exhausted);
        Check("retry.second", retries.TakeDue(1100, out exhausted).Count == 1 && !exhausted);
        Check("retry.third", retries.TakeDue(1600, out exhausted).Count == 1 && !exhausted);
        Check("retry.last_ack_window", retries.TakeDue(2099, out exhausted).Count == 0 && !exhausted);
        Check("retry.exhausted_fail_session", retries.TakeDue(2100, out exhausted).Count == 0 && exhausted);
        Check("retry.ack_removes", retries.Acknowledge("hurt1") && retries.Count == 0);
        Check("retry.acked_no_resend", retries.TakeDue(3000, out exhausted).Count == 0 && !exhausted);
        retries.Track("new", DemoProtocol.PlayerHurt("old", 1, "new", 1.0, 1), 100);
        retries.Clear();
        Check("retry.end_session_clears", retries.Count == 0 && retries.TakeDue(1000, out exhausted).Count == 0);
    }

    private static void RestartTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        Check("restart.start", conn.Start(port, out _));
        using (var client = new TcpClient("127.0.0.1", port))
        {
            Check("restart.first_connected", WaitConnected(conn, 2000));
            SendLine(client.GetStream(), BridgeJson.Write(HelloAck(1)));
            Check("restart.initial_inbound", TakeInbound(conn, out _));
            SendLine(client.GetStream(), BridgeJson.Write(DemoProtocol.Error("old", 2, "stale_session", "stale")));
            Check("restart.nonfatal_stale_notice", TakeInbound(conn, out var stale)
                && DemoProtocol.S(stale, "code") == "stale_session" && conn.State == BridgeConnection.BridgeState.Connected);
            SendLine(client.GetStream(), BridgeJson.Write(DemoProtocol.Msg("heartbeat", "", 3)));
            Thread.Sleep(30);
            conn.Close("test reconnect");
            Check("restart.closed_no_old_inbound", !conn.TryTakeInbound(out _));
        }
        Check("restart.reuse_instance", conn.Start(port, out var error), error);
        using var next = new TcpClient("127.0.0.1", port);
        Check("restart.second_connected", WaitConnected(conn, 2000));
        SendLine(next.GetStream(), BridgeJson.Write(HelloAck(1)));
        Check("restart.new_inbound_only", TakeInbound(conn, out var current) && DemoProtocol.S(current, "type") == "hello_ack");
        Check("restart.new_wire_seq_from_one", conn.Send(DemoProtocol.Hello("v1.5.4", conn.NextSeq()))
            && DemoProtocol.L(RecvLine(next.GetStream(), 2000), "seq") == 1);
        using var newcomer = new TcpClient("127.0.0.1", port);
        newcomer.GetStream().ReadTimeout = 1000;
        var rejected = false;
        try { rejected = newcomer.GetStream().ReadByte() == -1; }
        catch (IOException e) { rejected = e.InnerException is SocketException se && se.SocketErrorCode != SocketError.TimedOut; }
        Check("restart.single_client_rejected", rejected && conn.State == BridgeConnection.BridgeState.Connected);
        conn.Dispose();
        Check("restart.disposed_cannot_start", !conn.Start(port, out error) && error.Contains("disposed"));
    }

    private static void ConcurrentWireTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        conn.Start(port, out _);
        using var client = new TcpClient("127.0.0.1", port);
        WaitConnected(conn, 2000);
        var tasks = new Task[4];
        for (var producer = 0; producer < tasks.Length; producer++)
            tasks[producer] = Task.Run(() => { for (var i = 0; i < 20; i++) conn.Send(DemoProtocol.PlayerHurt("s", 1, Guid.NewGuid().ToString(), 1.0, 1)); });
        Task.WaitAll(tasks);
        long seq = 0;
        var valid = true;
        var ids = new HashSet<string>();
        for (var i = 0; i < 80; i++)
        {
            var msg = RecvLine(client.GetStream(), 2000);
            if (msg == null || DemoProtocol.L(msg, "seq") <= seq || !DemoProtocol.ValidateMessage(msg)) { valid = false; break; }
            seq = DemoProtocol.L(msg, "seq");
            ids.Add(DemoProtocol.S(msg, "eventId"));
        }
        Check("wire.concurrent_producers_intact_monotonic", valid && ids.Count == 80);
        Check("wire.drain_before_close", conn.WaitForOutboundDrain(500));
    }

    private static string PaddedHelloLine(int bytesIncludingNewline)
    {
        var hello = HelloAck(1);
        hello["pad"] = "";
        var baseLine = BridgeJson.Write(hello);
        hello["pad"] = new string('x', bytesIncludingNewline - 1 - Encoding.UTF8.GetByteCount(baseLine));
        return BridgeJson.Write(hello);
    }

    private static void BoundaryTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        conn.Start(port, out _);
        using var client = new TcpClient("127.0.0.1", port);
        WaitConnected(conn, 2000);
        SendLine(client.GetStream(), PaddedHelloLine(DemoProtocol.MaxLineBytes));
        Check("boundary.exact_64k_accepted", TakeInbound(conn, out var msg) && DemoProtocol.S(msg, "type") == "hello_ack");
        SendLine(client.GetStream(), PaddedHelloLine(DemoProtocol.MaxLineBytes + 1));
        Check("boundary.one_byte_excess_rejected", WaitClosed(conn, 2000));
        var err = RecvLine(client.GetStream(), 2000);
        Check("boundary.excess_error_on_wire", err != null && DemoProtocol.S(err, "code") == "overflow", err == null ? "no frame; " + conn.LastError : BridgeJson.Write(err));
    }

    private static void InvalidEnvelopeTests()
    {
        foreach (var invalid in new[] { "{\"type\":\"heartbeat\",\"pv\":1,\"sid\":\"\"}", "{\"type\":\"heartbeat\",\"pv\":1.9,\"sid\":\"\",\"seq\":1}",
            "{\"type\":\"player_hurt\",\"pv\":1,\"sid\":\"s\",\"seq\":1,\"eventId\":\"e\",\"damage\":-1,\"sourceEid\":1}" })
        {
            var port = FreePort();
            using var conn = new BridgeConnection();
            conn.Start(port, out _);
            using var client = new TcpClient("127.0.0.1", port);
            WaitConnected(conn, 2000);
            SendLine(client.GetStream(), invalid);
            var err = RecvLine(client.GetStream(), 2000);
            Check("envelope.invalid_closes", WaitClosed(conn, 2000) && err != null && DemoProtocol.S(err, "code") == "protocol");
        }
    }

    private static void InboundCapacityTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        conn.Start(port, out _);
        using var client = new TcpClient("127.0.0.1", port);
        WaitConnected(conn, 2000);
        var sb = new StringBuilder();
        for (var i = 1; i <= SessionInboundBuffer.ReliableCapacity + 1; i++) sb.Append(BridgeJson.Write(DemoProtocol.Ack("reliable-flood", i, "event-" + i))).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        client.GetStream().Write(bytes, 0, bytes.Length);
        Check("queue.inbound_overflow_closes", WaitClosed(conn, 2000) && conn.DroppedInbound == 1 && !conn.TryTakeInbound(out _));
        var queue = new BoundedQueue<int>();
        var accepted = 0;
        Parallel.For(0, 1000, i => { if (queue.TryEnqueueBounded(i, 64)) Interlocked.Increment(ref accepted); });
        Check("queue.concurrent_bound_exact", accepted == 64 && queue.Count == 64);
        queue.Clear();
        Check("queue.clear", queue.Count == 0 && !queue.TryDequeue(out _));
    }

    private static Dictionary<string,object> StreamMob(string sid,string id,long seq,long tick,bool alive=true,int slot=201)
    {
        var state=DemoProtocol.Msg("mob_state",sid,seq);
        state["entityId"]=id;state["kind"]="wither";state["mainItem"]="minecraft:air";
        foreach(var key in new[]{"x","z","yaw","bodyYaw","headYaw","pitch","vx","vy","vz","walkPosition","walkSpeed","attackProgress"})state[key]=0d;
        state["y"]=128d;state["hp"]=alive?300d:0d;state["maxHp"]=300d;state["alive"]=alive;state["aggressive"]=false;
        state["nativeTick"]=tick;state["targetEid"]=1;state["hurtTime"]=0;state["deathTime"]=alive?0:1;
        state["visualKey"]="1234567890abcdef1234567890abcdef";state["width"]=.9d;state["height"]=3.5d;
        state["armySlot"]=slot;state["armyPlayerSide"]=false;
        return state;
    }
    private static Dictionary<string,object> StreamProjectile(string sid,string id,long seq,bool alive=true)
    {
        var state=DemoProtocol.Msg("projectile_state",sid,seq);
        state["entityId"]=id;state["kind"]="arrow";state["x"]=0d;state["y"]=128d;state["z"]=0d;
        state["yaw"]=0d;state["pitch"]=0d;state["alive"]=alive;return state;
    }
    private static Dictionary<string,object> StreamHit(string sid,string source,string eventId,long seq,double damage=1)
    {
        var hit=DemoProtocol.Msg("mob_hit",sid,seq);hit["eventId"]=eventId;hit["sourceId"]=source;
        hit["eid"]=17;hit["damage"]=damage;hit["kind"]="wither";return hit;
    }
    private static List<Dictionary<string,object>> DrainBuffer(SessionInboundBuffer buffer)
    {
        var messages=new List<Dictionary<string,object>>();while(buffer.TryDequeue(out var message))messages.Add(message);return messages;
    }
    private static void InboundBufferTests()
    {
        const string id="7ad989a9-4be7-4a9a-9f41-68cae184cd57";
        var buffer=new SessionInboundBuffer(2,8);
        var first=StreamMob("s",id,1,0);var hit=StreamHit("s",id,"original",3);
        bool queued=buffer.TryEnqueue(first)&&buffer.TryEnqueue(StreamMob("s",id,2,1))&&buffer.TryEnqueue(hit)
            &&buffer.TryEnqueue(StreamMob("s",id,4,2));
        var messages=DrainBuffer(buffer);
        Check("stream.first_army_state_pinned_before_damage",queued&&messages.Count==3&&DemoProtocol.L(messages[0],"armySlot")==201&&DemoProtocol.L(messages[0],"nativeTick")==0&&ReferenceEquals(messages[1],hit));
        Check("stream.latest_replacement_moves_to_tail",messages.Count==3&&DemoProtocol.L(messages[2],"seq")==4&&DemoProtocol.L(messages[2],"nativeTick")==2);
        buffer.TryEnqueue(StreamHit("s",id,"changed",5,1));buffer.TryEnqueue(StreamHit("s",id,"changed",6,9));
        messages=DrainBuffer(buffer);
        Check("stream.changed_reliable_payload_not_deduplicated",messages.Count==2&&DemoProtocol.D(messages[0],"damage")==1&&DemoProtocol.D(messages[1],"damage")==9);
        buffer.TryEnqueue(StreamMob("s",id,7,3,false));buffer.TryEnqueue(StreamMob("s",id,8,4));
        var removal=DemoProtocol.Msg("mob_remove","s",9);removal["entityId"]=id;removal["eventId"]="remove";removal["reason"]="death";
        buffer.TryEnqueue(removal);buffer.TryEnqueue(StreamMob("s",id,10,5));messages=DrainBuffer(buffer);
        Check("stream.death_and_remove_retained_no_resurrection",messages.Count==2&&!BridgeJson.B(messages[0],"alive")&&DemoProtocol.S(messages[1],"type")=="mob_remove");
        buffer.TryEnqueue(StreamMob("other",id,11,0));messages=DrainBuffer(buffer);
        Check("stream.same_entity_other_session_has_new_first_binding",messages.Count==1&&DemoProtocol.S(messages[0],"sid")=="other");
        buffer.TryEnqueue(StreamMob("other",id,12,1));buffer.TryEnqueue(DemoProtocol.EndSession("other",13,"test"));buffer.TryEnqueue(StreamMob("other",id,14,2));messages=DrainBuffer(buffer);
        Check("stream.end_clears_pending_snapshots_and_blocks_late_state",messages.Count==1&&DemoProtocol.S(messages[0],"type")=="end_session"&&buffer.SnapshotCount==0);
        buffer.Clear();buffer.TryEnqueue(StreamMob("s",id,1,0));messages=DrainBuffer(buffer);
        Check("stream.clear_resets_binding_and_retirement_metadata",messages.Count==1&&BridgeJson.B(messages[0],"alive")&&buffer.CoalescedCount==0);
        buffer.Clear();
        Check("stream.distinct_visual_capacity_is_bounded",buffer.TryEnqueue(StreamProjectile("s","p1",1))&&buffer.TryEnqueue(StreamProjectile("s","p2",2))&&!buffer.TryEnqueue(StreamProjectile("s","p3",3))&&buffer.SnapshotCount==2);
        bool reliable=true;for(int i=0;i<8;i++)reliable&=buffer.TryEnqueue(DemoProtocol.Ack("s",4+i,"ack"+i));
        Check("stream.reliable_budget_independent_of_snapshots",reliable&&buffer.ReliableCount==8&&buffer.Count==10&&!buffer.TryEnqueue(DemoProtocol.Ack("s",20,"overflow")));
        buffer.Clear();bool beats=true;for(int i=0;i<2000;i++)beats&=buffer.TryEnqueue(DemoProtocol.Msg("heartbeat","",i+1));
        Check("stream.heartbeats_never_consume_game_queue",beats&&buffer.Count==0);
    }
    private static void InboundPausedSocketTests()
    {
        const string sid="paused-army";
        var bosses=new[]{Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),Guid.NewGuid().ToString()};
        var projectiles=new string[8];for(int i=0;i<projectiles.Length;i++)projectiles[i]=Guid.NewGuid().ToString();
        var port=FreePort();using var conn=new BridgeConnection();conn.Start(port,out _);
        using var client=new TcpClient("127.0.0.1",port);WaitConnected(conn,2000);
        var batch=new StringBuilder();long seq=0;bool valid=true;
        var expectedReliable=new List<string>();
        void Add(Dictionary<string,object> message,bool reliable=false)
        {
            message["seq"]=++seq;valid&=DemoProtocol.ValidateMessage(message);
            var line=BridgeJson.Write(message);batch.Append(line).Append('\n');if(reliable)expectedReliable.Add(line);
        }
        Add(HelloAck(0),true);
        for(int i=0;i<3;i++)Add(StreamMob(sid,bosses[i],0,0,true,201+i),true);
        string changedId=Guid.NewGuid().ToString();
        for(int tick=1;tick<=800;tick++)
        {
            for(int i=0;i<3;i++)Add(StreamMob(sid,bosses[i],0,tick,true,201+i));
            Add(StreamProjectile(sid,projectiles[tick%8],0));
            var player=DemoProtocol.Msg("player_state",sid,0);player["hp"]=20d;player["maxHp"]=20d;player["alive"]=true;player["item"]="minecraft:air";Add(player);
            var motion=DemoProtocol.Msg("player_motion",sid,0);motion["spinning"]=false;motion["nativeTick"]=tick;
            motion["vx"]=0d;motion["vy"]=0d;motion["vz"]=0d;Add(motion);
            if(tick%5==0)
            {
                Add(StreamHit(sid,bosses[tick%3],tick==5?changedId:Guid.NewGuid().ToString(),0),true);
                Add(DemoProtocol.Ack(sid,0,"native-hurt-"+tick),true);
            }
            if(tick==250)Add(StreamProjectile(sid,projectiles[0],0,false),true);
            if(tick==400)
            {
                Add(StreamMob(sid,bosses[0],0,tick,false,201),true);
                var remove=DemoProtocol.Msg("mob_remove",sid,0);remove["entityId"]=bosses[0];remove["eventId"]=Guid.NewGuid().ToString();remove["reason"]="death";Add(remove,true);
            }
        }
        Add(StreamHit(sid,bosses[1],changedId,0,9),true);
        Add(DemoProtocol.Ack(sid,0,"burst-finished"),true);
        long barrier=seq;
        var bytes=Encoding.UTF8.GetBytes(batch.ToString());client.GetStream().Write(bytes,0,bytes.Length);
        var deadline=Environment.TickCount64+5000;
        while(conn.LastInboundSequence<barrier&&conn.State==BridgeConnection.BridgeState.Connected&&Environment.TickCount64<deadline)Thread.Sleep(5);
        Check("stream.socket_4800_valid_snapshots_with_events_received",valid&&conn.LastInboundSequence==barrier&&conn.CoalescedInbound>4000&&conn.DroppedInbound==0);
        object peerGate=new object();
        using(var heartbeat=new Timer(_=>{lock(peerGate){try{SendLine(client.GetStream(),BridgeJson.Write(DemoProtocol.Msg("heartbeat","",++seq)));}catch(IOException){}catch(ObjectDisposedException){}}},null,0,200))
        {
            // Deliberately no game queue polling: model/graphic pause exceeds512 samples and heartbeat lease.
            Thread.Sleep(DemoProtocol.TimeoutMs+500);
            Check("stream.socket_game_pause_keeps_bounded_connection",conn.State==BridgeConnection.BridgeState.Connected&&conn.DroppedInbound==0);
        }
        var got=new List<Dictionary<string,object>>();
        while(conn.TryTakeInbound(out var message))got.Add(message);
        var actualReliable=new List<string>();long previous=0;bool ordered=true;
        foreach(var message in got)
        {
            long current=DemoProtocol.L(message,"seq");ordered&=current>previous;previous=current;
            string type=DemoProtocol.S(message,"type");
            bool firstMob=type=="mob_state"&&DemoProtocol.L(message,"nativeTick")==0;
            if(type=="hello_ack"||type=="mob_hit"||type=="ack"||type=="mob_remove"||firstMob||message.ContainsKey("alive")&&!BridgeJson.B(message,"alive"))actualReliable.Add(BridgeJson.Write(message));
        }
        Check("stream.socket_consumer_sequences_increase_after_coalescing",ordered&&got.Count<400);
        Check("stream.socket_all_reliable_receipts_unchanged_in_order",actualReliable.Count==expectedReliable.Count&&actualReliable.SequenceEqual(expectedReliable));
        Check("stream.socket_three_original_army_bindings_retained",bosses.All(id=>got.Any(m=>DemoProtocol.S(m,"type")=="mob_state"&&DemoProtocol.S(m,"entityId")==id&&DemoProtocol.L(m,"nativeTick")==0&&DemoProtocol.L(m,"armySlot")>=201)));
        Check("stream.socket_latest_live_boss_pose_retained",bosses.Skip(1).All(id=>got.Any(m=>DemoProtocol.S(m,"type")=="mob_state"&&DemoProtocol.S(m,"entityId")==id&&DemoProtocol.L(m,"nativeTick")==800)));
        Check("stream.socket_retired_entities_have_no_late_alive_sample",!got.Any(m=>DemoProtocol.S(m,"entityId")==bosses[0]&&BridgeJson.B(m,"alive")&&DemoProtocol.L(m,"nativeTick")>0)&&got.Count(m=>DemoProtocol.S(m,"entityId")==projectiles[0])==1);
        lock(peerGate)
        {
            SendLine(client.GetStream(),BridgeJson.Write(StreamMob(sid,bosses[1],++seq,801,true,202)));
            SendLine(client.GetStream(),BridgeJson.Write(DemoProtocol.EndSession(sid,++seq,"paused_end")));
            SendLine(client.GetStream(),BridgeJson.Write(StreamMob(sid,bosses[1],++seq,802,true,202)));
        }
        deadline=Environment.TickCount64+2000;while(conn.LastInboundSequence<seq&&Environment.TickCount64<deadline)Thread.Sleep(5);
        got.Clear();while(conn.TryTakeInbound(out var message))got.Add(message);
        Check("stream.socket_end_preserved_and_late_snapshot_retired",got.Count==1&&DemoProtocol.S(got[0],"type")=="end_session");
        conn.Close("test reconnect");conn.Start(port,out _);
        using var next=new TcpClient("127.0.0.1",port);WaitConnected(conn,2000);
        SendLine(next.GetStream(),BridgeJson.Write(StreamMob("new-session",bosses[0],1,0,true,201)));
        Check("stream.socket_reconnect_clears_old_death_binding_cache",TakeInbound(conn,out var renewed)&&BridgeJson.B(renewed,"alive")&&DemoProtocol.L(renewed,"nativeTick")==0&&DemoProtocol.S(renewed,"sid")=="new-session");
    }

    private static Dictionary<string,object> OutboundMobHurt(string sid,int source,long seq,string eventId,double damage=1)
    {
        var message=DemoProtocol.Msg("mob_hurt",sid,seq);message["sourceEid"]=source;message["eventId"]=eventId;
        message["entityId"]="7ad989a9-4be7-4a9a-9f41-68cae184cd57";message["sourceKind"]="arrow";message["damage"]=damage;return message;
    }
    private static void OutboundBufferTests()
    {
        var buffer=new SessionOutboundBuffer();const string sid="outbound-army";long seq=0;bool accepted=true;
        bool Add(Dictionary<string,object> message)
        {message["seq"]=++seq;return DemoProtocol.ValidateMessage(message)&&buffer.TryEnqueue(message,BridgeJson.Write(message),out _);}
        accepted&=Add(DemoProtocol.Hello("v1.4.8"));accepted&=Add(DemoProtocol.BeginSession(sid,0,128,0,0,1));
        accepted&=Add(DemoProtocol.PlayerPose(sid,0,0,128,0,0));
        for(int id=1;id<=600;id++)accepted&=Add(DemoProtocol.ActorUpsert(sid,0,id,0,128,0,0,true,"Fian"));
        for(int round=1;round<=20;round++)
        {
            accepted&=Add(DemoProtocol.PlayerPose(sid,0,round,128,0,0));
            for(int id=1;id<=600;id++)accepted&=Add(DemoProtocol.ActorUpsert(sid,0,id,round,128,0,0,true,"Fian"));
        }
        Check("outbound.12000_snapshots_are_bounded_and_coalesced",accepted&&buffer.Count==1204&&buffer.SnapshotCount==601&&buffer.ReliableCount==603&&buffer.CoalescedCount>11000);
        var hurt=OutboundMobHurt(sid,600,0,"retired-source-hurt");accepted&=Add(hurt);
        accepted&=Add(DemoProtocol.AttackInput(sid,0,"attack",20,128,0,0));
        for(int id=1;id<=600;id++)accepted&=Add(DemoProtocol.ActorUpsert(sid,0,id,21,128,0,0,true,"Fian"));
        for(int id=0;id<600;id++)accepted&=Add(DemoProtocol.Ack(sid,0,"ack-"+id));
        accepted&=Add(DemoProtocol.ActorRemove(sid,0,600));
        accepted&=Add(new Dictionary<string,object>(hurt)); // Native receipt remains ACKable after its source retires.
        var changed=new Dictionary<string,object>(hurt){["damage"]=9d};changed["seq"]=++seq;
        Check("outbound.retired_exact_retry_allowed_changed_missing_source_rejected",accepted&&!buffer.TryEnqueue(changed,BridgeJson.Write(changed),out var sourceError)&&sourceError.Contains("source actor"));
        accepted&=Add(DemoProtocol.ActorUpsert(sid,0,600,22,128,0,0,true,"Fian"));
        accepted&=Add(OutboundMobHurt(sid,600,0,"fresh-source-hurt"));
        var frames=new List<Dictionary<string,object>>();while(buffer.TryDequeue(out var frame)){BridgeJson.TryParse(frame.Line,out var message);frames.Add(message);}
        long last=0;double playerX=0,sourceX=0;bool order=true,dependency=true;int births=0,acks=0,hurts=0;var actors=new HashSet<long>();
        foreach(var message in frames)
        {
            order&=DemoProtocol.L(message,"seq")>last;last=DemoProtocol.L(message,"seq");string type=DemoProtocol.S(message,"type");
            if(type=="player_pose")playerX=DemoProtocol.D(message,"x");
            if(type=="actor_upsert"){long id=DemoProtocol.L(message,"eid");if(actors.Add(id))births++;if(id==600)sourceX=DemoProtocol.D(message,"x");}
            if(type=="actor_remove")actors.Remove(DemoProtocol.L(message,"eid"));
            if(type=="mob_hurt"){hurts++;if(hurts==1)dependency&=actors.Contains(600)&&sourceX==20&&playerX==20;if(hurts==3)dependency&=actors.Contains(600)&&sourceX==22;}
            if(type=="ack"){dependency&=DemoProtocol.S(message,"ackEventId")=="ack-"+acks;acks++;}
        }
        Check("outbound.source_latest_pose_and_player_pose_precede_hurt",dependency&&hurts==3);
        Check("outbound.all_reliable_frames_and_reentry_in_original_order",accepted&&order&&births==601&&acks==600&&frames.Count<1900);
        var unknown=OutboundMobHurt(sid,999,++seq,"unannounced");
        Check("outbound.unannounced_actor_cannot_start_damage",!buffer.TryEnqueue(unknown,BridgeJson.Write(unknown),out sourceError)&&sourceError.Contains("source actor"));
        accepted=Add(DemoProtocol.ActorUpsert(sid,0,1,23,128,0,0,true,"Fian"))&&Add(DemoProtocol.EndSession(sid,0,"done"));
        Check("outbound.end_clears_replaceable_and_birth_metadata",accepted&&buffer.SnapshotCount==0);
        accepted&=Add(DemoProtocol.ActorUpsert("new-session",0,1,24,128,0,0,true,"Fian"));
        Check("outbound.new_session_birth_is_retained",accepted&&buffer.ReliableCount==2);
        buffer.Terminate(BridgeJson.Write(DemoProtocol.Error("",++seq,"overflow","test")),"overflow: test");
        Check("outbound.terminal_error_replaces_pending_and_stops_admission",buffer.Count==1&&!Add(DemoProtocol.Ack(sid,0,"late"))&&buffer.TryDequeue(out var terminal)&&terminal.CloseReason=="overflow: test"&&!buffer.TryDequeue(out _));
        var small=new SessionOutboundBuffer(2,3);
        bool Small(Dictionary<string,object> m)=>small.TryEnqueue(m,BridgeJson.Write(m),out _);
        Check("outbound.independent_distinct_and_reliable_bounds",Small(DemoProtocol.ActorUpsert("s",1,1,0,128,0,0,true,"Fian"))&&Small(DemoProtocol.ActorUpsert("s",2,2,0,128,0,0,true,"Fian"))&&!Small(DemoProtocol.ActorUpsert("s",3,3,0,128,0,0,true,"Fian"))&&Small(DemoProtocol.Ack("s",4,"ok"))&&!Small(DemoProtocol.Ack("s",5,"overflow"))&&small.Count==3);
    }
    private static void OutboundPausedSocketTests()
    {
        const string sid="slow-peer";var port=FreePort();using var conn=new BridgeConnection();conn.Start(port,out _,"v1.4.8");
        using var client=new TcpClient();client.ReceiveBufferSize=4096;client.Connect("127.0.0.1",port);WaitConnected(conn,2000);
        long peerSeq=0;using var heartbeat=new Timer(_=>{try{SendLine(client.GetStream(),BridgeJson.Write(DemoProtocol.Msg("heartbeat","",Interlocked.Increment(ref peerSeq))));}catch(IOException){}catch(ObjectDisposedException){}},null,0,200);
        bool accepted=conn.Send(DemoProtocol.BeginSession(sid,0,128,0,0,1))&&conn.Send(DemoProtocol.PlayerPose(sid,0,0,128,0,0));
        for(int id=1;id<=600;id++)
        {
            var actor=DemoProtocol.ActorUpsert(sid,0,id,0,128,0,0,true,"Fian");actor["pad"]=new string('x',4096);accepted&=conn.Send(actor);
        }
        for(int round=1;round<=20;round++)
        {
            accepted&=conn.Send(DemoProtocol.PlayerPose(sid,0,round,128,0,0));
            for(int id=1;id<=600;id++)accepted&=conn.Send(DemoProtocol.ActorUpsert(sid,0,id,round,128,0,0,true,"Fian"));
        }
        accepted&=conn.Send(OutboundMobHurt(sid,600,0,"socket-hurt"));
        accepted&=conn.Send(DemoProtocol.ActorUpsert(sid,0,600,21,128,0,0,true,"Fian"));
        for(int i=0;i<600;i++)accepted&=conn.Send(DemoProtocol.Ack(sid,0,"socket-ack-"+i));
        accepted&=conn.Send(DemoProtocol.Ack(sid,0,"socket-end-barrier"));
        Check("outbound.socket_slow_peer_does_not_overflow_12000_updates",accepted&&conn.CoalescedOutbound>10000&&conn.DroppedOutbound==0&&conn.State==BridgeConnection.BridgeState.Connected);
        client.GetStream().ReadTimeout=5000;using var reader=new StreamReader(client.GetStream(),Encoding.UTF8,false,65536,true);
        long last=0;int count=0,ackCount=0;double source=0,pose=0;bool valid=true,sourceCorrect=false,ended=false;var ids=new HashSet<long>();
        while(count<2000)
        {
            var line=reader.ReadLine();if(line==null){valid=false;break;}
            if(!BridgeJson.TryParse(line,out var message)){valid=false;break;}
            valid&=DemoProtocol.ValidateMessage(message)&&DemoProtocol.L(message,"seq")>last;last=DemoProtocol.L(message,"seq");count++;
            string type=DemoProtocol.S(message,"type");
            if(type=="player_pose")pose=DemoProtocol.D(message,"x");
            if(type=="actor_upsert"){ids.Add(DemoProtocol.L(message,"eid"));if(DemoProtocol.L(message,"eid")==600)source=DemoProtocol.D(message,"x");}
            if(type=="mob_hurt")sourceCorrect=ids.Count==600&&source==20&&pose==20;
            if(type=="ack")
            {
                string id=DemoProtocol.S(message,"ackEventId");if(id=="socket-end-barrier"){ended=true;break;}
                valid&=id=="socket-ack-"+ackCount;ackCount++;
            }
        }
        Check("outbound.socket_wire_order_and_source_dependency_preserved",valid&&sourceCorrect&&ended&&ackCount==600&&ids.Count==600&&count<1900);
        Check("outbound.socket_drains_latest_instead_of_obsolete_poses",conn.WaitForOutboundDrain(1000)&&conn.DroppedOutbound==0);
    }
    private static void OutboundFailureSocketTests()
    {
        var port=FreePort();using var conn=new BridgeConnection();conn.Start(port,out _);
        using var client=new TcpClient();client.ReceiveBufferSize=4096;client.Connect("127.0.0.1",port);WaitConnected(conn,2000);
        long seq=0;using var heartbeat=new Timer(_=>{try{SendLine(client.GetStream(),BridgeJson.Write(DemoProtocol.Msg("heartbeat","",Interlocked.Increment(ref seq))));}catch(IOException){}catch(ObjectDisposedException){}},null,0,200);
        for(int id=1;id<=600;id++)
        {var actor=DemoProtocol.ActorUpsert("failure",0,id,0,128,0,0,true,"Fian");actor["pad"]=new string('x',4096);conn.Send(actor);}
        var bad=DemoProtocol.ActorUpsert("failure",0,1,double.NaN,128,0,0,true,"Fian");
        var clock=System.Diagnostics.Stopwatch.StartNew();bool rejected=!conn.Send(bad);clock.Stop();
        Check("outbound.invalid_superseded_payload_rejected_without_socket_wait",rejected&&clock.ElapsedMilliseconds<500&&conn.LastError.Contains("invalid outbound"));
        Check("outbound.terminal_prevents_later_send",!conn.Send(DemoProtocol.Ack("failure",0,"late")));
        client.GetStream().ReadTimeout=5000;using var reader=new StreamReader(client.GetStream(),Encoding.UTF8,false,65536,true);
        long last=0;bool ordered=true,sawError=false;int frames=0;
        try
        {
            string? line;while((line=reader.ReadLine())!=null&&frames++<603)
            {
                if(!BridgeJson.TryParse(line,out var message)){ordered=false;break;}
                ordered&=DemoProtocol.L(message,"seq")>last;last=DemoProtocol.L(message,"seq");
                if(DemoProtocol.S(message,"type")=="error"){sawError=true;break;}
            }
        }
        catch(IOException){ordered=false;}
        Check("outbound.error_has_no_lower_sequence_after_it",ordered&&sawError&&WaitClosed(conn,2000));
        Check("outbound.original_failure_reason_survives_close",conn.LastError.Contains("invalid outbound"));
        conn.Start(port,out _);using var next=new TcpClient("127.0.0.1",port);WaitConnected(conn,2000);
        conn.Send(DemoProtocol.ActorUpsert("new",0,1,0,128,0,0,true,"Fian"));
        var renewed=RecvLine(next.GetStream(),2000);
        Check("outbound.reconnect_resets_terminal_and_birth_metadata",renewed!=null&&DemoProtocol.L(renewed,"seq")==1&&DemoProtocol.S(renewed,"sid")=="new");
    }

    private static void ReliableSocketTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        conn.Start(port, out _);
        using var client = new TcpClient("127.0.0.1", port);
        WaitConnected(conn, 2000);
        conn.Send(DemoProtocol.Hello("v1.5.4"));
        var mcGate = new BridgeSessionValidator();
        Check("reliable.hello", mcGate.Validate(RecvLine(client.GetStream(), 2000)) == BridgeSessionValidator.Verdict.Ok);
        SendLine(client.GetStream(), BridgeJson.Write(HelloAck(1)));
        TakeInbound(conn, out _);
        const string sid = "reliable-session";
        mcGate.BeginSession(sid);
        conn.Send(DemoProtocol.BeginSession(sid, 1, -60, 0, 0, 1));
        Check("reliable.session", mcGate.Validate(RecvLine(client.GetStream(), 2000)) == BridgeSessionValidator.Verdict.Ok);
        var outbox = new EventRetryTracker();
        var hurt = DemoProtocol.PlayerHurt(sid, 1, "hurt-reliable", 5.0, 1);
        Check("reliable.initial_send", conn.Send(hurt) && outbox.Track("hurt-reliable", hurt, 0));
        var first = RecvLine(client.GetStream(), 2000);
        var applied = 0;
        if (mcGate.Validate(first) == BridgeSessionValidator.Verdict.Ok && mcGate.TryMarkEvent(DemoProtocol.S(first, "eventId"))) applied++;
        var due = outbox.TakeDue(500, out var exhausted);
        Check("reliable.lost_ack_causes_retry", !exhausted && due.Count == 1 && conn.Send(due[0]));
        var second = RecvLine(client.GetStream(), 2000);
        var accepted = mcGate.Validate(second) == BridgeSessionValidator.Verdict.Ok;
        if (accepted && mcGate.TryMarkEvent(DemoProtocol.S(second, "eventId"))) applied++;
        Check("reliable.one_application_new_sequence", accepted && applied == 1 && DemoProtocol.L(second, "seq") > DemoProtocol.L(first, "seq"));
        SendLine(client.GetStream(), BridgeJson.Write(DemoProtocol.Ack(sid, 2, DemoProtocol.S(second, "eventId"))));
        Check("reliable.ack_clears_retry", TakeInbound(conn, out var ack) && outbox.Acknowledge(DemoProtocol.S(ack, "ackEventId")) && outbox.Count == 0);
        SendLine(client.GetStream(), BridgeJson.Write(DemoProtocol.Msg("heartbeat", "", 2)));
        Check("reliable.replayed_wire_seq_closes", WaitClosed(conn, 2000) && conn.LastError.Contains("sequence"));
    }

    private static void BackgroundHeartbeatTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        conn.Start(port, out _);
        using var client = new TcpClient("127.0.0.1", port);
        WaitConnected(conn, 2000);
        conn.Send(DemoProtocol.Hello("v1.5.4"));
        RecvLine(client.GetStream(), 2000);
        SendLine(client.GetStream(), BridgeJson.Write(HelloAck(1)));
        TakeInbound(conn, out _);
        long nextSeq = 1;
        using (var peerHeartbeat = new Timer(_ =>
        {
            try { SendLine(client.GetStream(), BridgeJson.Write(DemoProtocol.Msg("heartbeat", "", Interlocked.Increment(ref nextSeq)))); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, null, 100, 500))
        {
            // No game-thread polling, sends or ticks during this pause.
            Thread.Sleep(DemoProtocol.TimeoutMs + 500);
            Check("heartbeat.game_thread_pause_stays_connected", conn.State == BridgeConnection.BridgeState.Connected);
            Check("heartbeat.background_server_frame", DemoProtocol.S(RecvLine(client.GetStream(), 1000), "type") == "heartbeat");
        }
    }

    private static void JsonTests()
    {
        var obj = new Dictionary<string, object>
        {
            { "type", "hit_actor" },
            { "pv", 1L },
            { "damage", 12.5 },
            { "alive", true },
            { "note", "引号\"反斜杠\\换行\n制表\t" }
        };
        var json = BridgeJson.Write(obj);
        Check("json.roundtrip", BridgeJson.TryParse(json, out var back)
            && BridgeJson.S(back, "type") == "hit_actor"
            && BridgeJson.L(back, "pv") == 1
            && Math.Abs(BridgeJson.D(back, "damage") - 12.5) < 1e-9
            && BridgeJson.B(back, "alive")
            && BridgeJson.S(back, "note") == "引号\"反斜杠\\换行\n制表\t", json);
        Check("json.malformed", !BridgeJson.TryParse("{\"type\": }", out _));
        Check("json.trailing", !BridgeJson.TryParse("{} {}", out _));
        Check("json.nested_rejected", !BridgeJson.TryParse("{\"a\":{\"b\":1}}", out _));
        foreach (var invalid in new[] { "{\"a\":+1}", "{\"a\":01}", "{\"a\":1.}", "{\"a\":.1}", "{\"a\":1e}", "{\"a\":1e309}", "{\"a\":1,\"a\":2}", "{\"a\":\"raw\tcontrol\"}" })
            Check("json.reject_" + invalid, !BridgeJson.TryParse(invalid, out _));
        Check("json.valid_exponent", BridgeJson.TryParse("{\"a\":-1.25e+2}", out var exponent) && BridgeJson.D(exponent, "a") == -125);
        var nonfiniteRejected = false;
        try { BridgeJson.Write(new Dictionary<string, object> { { "value", double.PositiveInfinity } }); }
        catch (ArgumentException) { nonfiniteRejected = true; }
        Check("json.reject_nonfinite_output", nonfiniteRejected);
    }

    private static void TransformTests()
    {
        var t = new DemoTransform(1.0, 10.0, -5.0, 64.0, 0.0);
        var mc = t.BlToMc(100.5, 73.0, -25.25, 91.5);
        Check("transform.bl2mc", Math.Abs(mc.X - 110.5) < 1e-9 && Math.Abs(mc.Z - 68.0) < 1e-9 && Math.Abs(mc.Y - 64.0) < 1e-9 && Math.Abs(mc.Yaw - -91.5) < 1e-9);
        var bl = t.McToBl(mc.X, mc.Y, mc.Z, mc.Yaw);
        Check("transform.roundtrip", Math.Abs(bl.X - 100.5) < 1e-6 && Math.Abs(bl.Y - 73.0) < 1e-6 && bl.Z == 0 && Math.Abs(bl.Yaw - 91.5) < 1e-6);
        var plane = DemoTransform.Default(-60);
        foreach (var yaw in new[] { 0.0, 90.0, 180.0, 270.0 })
        {
            var radians = yaw * Math.PI / 180;
            var facing = plane.BlToMc(Math.Sin(radians), Math.Cos(radians), 999, yaw);
            var mcRadians = facing.Yaw * Math.PI / 180;
            Check("transform.cardinal_" + yaw, Math.Abs(facing.X + Math.Sin(mcRadians)) < 1e-9
                && Math.Abs(facing.Z - Math.Cos(mcRadians)) < 1e-9 && facing.Y == -60);
        }
        Check("transform.vertical_ignored", plane.BlToMc(1, 2, 999, 0) == plane.BlToMc(1, 2, -999, 0));
        var badScale = false;
        try { _ = new DemoTransform(0, 0, 0, 0, 0); } catch (ArgumentOutOfRangeException) { badScale = true; }
        Check("transform.zero_scale_rejected", badScale);
    }

    private static void DedupTests()
    {
        var d = new DedupSet(3);
        Check("dedup.first", d.TryMark("a"));
        Check("dedup.duplicate", !d.TryMark("a"));
        d.TryMark("b");
        d.TryMark("c");
        Check("dedup.capacity_fails_safely", d.IsFull && !d.TryMark("d"));
        Check("dedup.applied_event_never_forgotten", d.Contains("a") && !d.TryMark("a"));
        d.Clear();
        Check("dedup.session_reset", !d.IsFull && d.TryMark("a"));
    }

    private static void ValidatorTests()
    {
        var v = new BridgeSessionValidator();
        var m = DemoProtocol.PlayerHurt("a1", 1, "e1", 5.0, 1);
        Check("validator.not_in_session", v.Validate(m) == BridgeSessionValidator.Verdict.NotInSession);
        v.BeginSession("a1");
        m["seq"] = 2L;
        Check("validator.ok", v.Validate(m) == BridgeSessionValidator.Verdict.Ok);
        Check("validator.first_event", v.TryMarkEvent("e1"));
        Check("validator.dup_event", !v.TryMarkEvent("e1"));
        m["sid"] = "other";
        m["seq"] = 3L;
        Check("validator.stale_session", v.Validate(m) == BridgeSessionValidator.Verdict.StaleSession);
        m["sid"] = "a1";
        m["seq"] = 4L;
        m["pv"] = 99L;
        Check("validator.bad_pv", v.Validate(m) == BridgeSessionValidator.Verdict.BadPv);
        var helloAck = HelloAck(5);
        Check("validator.handshake_ok", v.Validate(helloAck) == BridgeSessionValidator.Verdict.Ok);
        Check("validator.replayed_sequence_rejected", v.Validate(helloAck) == BridgeSessionValidator.Verdict.StaleSequence);
        var badEnvelope = DemoProtocol.PlayerHurt("a1", 6, "e2", 2, 1);
        badEnvelope["pv"] = 1.9;
        Check("validator.fractional_pv_rejected", v.Validate(badEnvelope) == BridgeSessionValidator.Verdict.BadPv);
        badEnvelope["pv"] = 1;
        badEnvelope.Remove("seq");
        Check("validator.missing_seq_rejected", v.Validate(badEnvelope) == BridgeSessionValidator.Verdict.Malformed);
        badEnvelope["seq"] = 6L;
        badEnvelope["damage"] = -5.0;
        Check("validator.negative_damage_rejected", v.Validate(badEnvelope) == BridgeSessionValidator.Verdict.Malformed);
        v.ResetConnection();
        Check("validator.new_connection_seq_reset", v.Validate(HelloAck(1)) == BridgeSessionValidator.Verdict.Ok);
    }

    private static bool TakeInbound(BridgeConnection conn, out Dictionary<string, object> msg, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (conn.TryTakeInbound(out msg))
                return true;
            Thread.Sleep(10);
        }
        msg = null!;
        return false;
    }

    private static void LoopbackTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        Check("conn.start", conn.Start(port, out var startError), startError);
        using var second = new BridgeConnection();
        Check("conn.port_busy", !second.Start(port, out var busyError) && busyError.StartsWith("port_busy"), busyError);

        var client = new TcpClient();
        client.NoDelay = true;
        client.Connect(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        Check("conn.connected", WaitConnected(conn, 3000));
        conn.Send(DemoProtocol.Hello("v1.5.4", conn.NextSeq()));
        Check("conn.hello_before_heartbeat", DemoProtocol.S(RecvLine(stream, 2000), "type") == "hello");

        var hello = HelloAck(1);
        SendLine(stream, BridgeJson.Write(hello));
        var hadHello = TakeInbound(conn, out var gotHello);
        Check("conn.hello_ack_received", hadHello && DemoProtocol.S(gotHello, "type") == "hello_ack",
            hadHello ? "type=" + DemoProtocol.S(gotHello, "type") : "queue empty; state=" + conn.State + "; err=" + conn.LastError);

        var begin = DemoProtocol.BeginSession("s1", conn.NextSeq(), 64.0, 0.0, 0.0, 1.0);
        Check("conn.send_begin", conn.Send(begin));
        var gotBegin = RecvLine(stream, 2000);
        Check("conn.begin_on_wire", gotBegin != null && DemoProtocol.S(gotBegin, "type") == "begin_session");

        conn.Send(DemoProtocol.PlayerPose("s1", conn.NextSeq(), 1.0, 2.0, 3.0, 4.0));
        var gotPose = RecvLine(stream, 2000);
        Check("conn.pose_on_wire", gotPose != null && DemoProtocol.S(gotPose, "type") == "player_pose" && Math.Abs(DemoProtocol.D(gotPose, "x") - 1.0) < 1e-9);

        var beat = RecvLine(stream, 3000);
        Check("conn.heartbeat_sent", beat != null && DemoProtocol.S(beat, "type") == "heartbeat");

        var hit = DemoProtocol.Msg("hit_actor", "s1", 2);
        hit["eventId"] = "ev1";
        hit["attackId"] = "at1";
        hit["eid"] = 1;
        hit["damage"] = 4.0;
        hit["killed"] = false;
        SendLine(stream, BridgeJson.Write(hit));
        hit["seq"] = 3L;
        SendLine(stream, BridgeJson.Write(hit));
        var hadHit1 = TakeInbound(conn, out var hit1);
        var hadHit2 = TakeInbound(conn, out var hit2);
        Check("conn.hit_received", hadHit1 && DemoProtocol.S(hit1, "type") == "hit_actor" && DemoProtocol.S(hit1, "eventId") == "ev1");
        Check("conn.hit_dedup_by_consumer", hadHit2 && DemoProtocol.S(hit2, "eventId") == "ev1" && hit1 != hit2);

        SendLine(stream, "{oops malformed");
        var protoErr = RecvLine(stream, 2000);
        Check("conn.protocol_error", protoErr != null && DemoProtocol.S(protoErr, "type") == "error" && DemoProtocol.S(protoErr, "code") == "protocol");
        Check("conn.closed_after_protocol_error", WaitClosed(conn, 2000));

        client.Close();
    }

    private static void OversizeTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        Check("conn.start2", conn.Start(port, out _));
        var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        WaitConnected(conn, 3000);
        var big = "{\"type\":\"hello_ack\",\"pad\":\"" + new string('x', DemoProtocol.MaxLineBytes + 16) + "\"}";
        try
        {
            SendLine(stream, big);
        }
        catch (Exception)
        {
            // server may reset mid-send; either way the connection must end closed.
        }
        Check("conn.oversize_closed", WaitClosed(conn, 3000));
        client.Close();
    }

    private static void TimeoutTests()
    {
        var port = FreePort();
        using var conn = new BridgeConnection();
        Check("conn.start3", conn.Start(port, out _));
        var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        WaitConnected(conn, 3000);
        // Stay silent: no hello_ack either. Connection must hit the 3s watchdog and close.
        Check("conn.timeout_closed", WaitClosed(conn, 6000), conn.LastError);
        client.Close();
    }

    private static bool WaitConnected(BridgeConnection conn, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (conn.State == BridgeConnection.BridgeState.Connected)
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static bool WaitClosed(BridgeConnection conn, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (conn.State == BridgeConnection.BridgeState.Closed)
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static void Main()
    {
        EntityWireTests();
        CreatureDamageConfigTests();
        ArmyWireTests();
        WeaponWireTests();
        HotbarTests();
        HotbarWheelTests();
        InventoryTests();
        ShieldWireTests();
        NativeInteractionWireTests();
        ShieldImpactWireTests();
        TntWireTests();
        FabricFixtureTests();
        JsonTests();
        TransformTests();
        DedupTests();
        RetryTests();
        ValidatorTests();
        LoopbackTests();
        RestartTests();
        ConcurrentWireTests();
        BoundaryTests();
        InvalidEnvelopeTests();
        InboundCapacityTests();
        InboundBufferTests();
        InboundPausedSocketTests();
        OutboundBufferTests();
        OutboundPausedSocketTests();
        OutboundFailureSocketTests();
        ReliableSocketTests();
        BackgroundHeartbeatTests();
        BackgroundGreetingTests();
        OversizeTests();
        TimeoutTests();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED (" + _checks + " checks)" : _failures + " TEST(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void ArmyWireTests()
    {
        var begin=DemoProtocol.BeginSession("army",1,128,0,0,1);
        begin["actorLimit"]=256;
        Check("army.negotiated256_actors",DemoProtocol.ValidateMessage(begin));
        begin["actorLimit"]=640;
        Check("army.negotiated640_for600_soldiers",DemoProtocol.ValidateMessage(begin));
        begin["actorLimit"]=200;
        Check("army.arbitrary_actor_limit_rejected",!DemoProtocol.ValidateMessage(begin));
        begin.Remove("actorLimit");
        Check("army.ordinary_session_backward_compatible",DemoProtocol.ValidateMessage(begin));
        var batch=DemoProtocol.Msg("mc_army_spawn","army",2);
        batch["requestId"]=Guid.NewGuid().ToString();batch["count"]=3;batch["playerSide"]=false;
        for(int i=0;i<3;i++) {
            string prefix="mob"+i;
            batch[prefix+"Kind"]="wither";batch[prefix+"Slot"]=i+201;
            batch[prefix+"X"]=(double)i*6;batch[prefix+"Y"]=128d;batch[prefix+"Z"]=40d;
        }
        Check("army.three_assigned_real_withers",DemoProtocol.ValidateMessage(batch));
        var invalid=new Dictionary<string,object>(batch){["mob1Slot"]=201};
        Check("army.duplicate_native_slot_rejected",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(batch){["mob2X"]=double.PositiveInfinity};
        Check("army.nonfinite_spawn_rejected",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(batch){["count"]=17};
        Check("army.oversized_spawn_rejected",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(batch);invalid.Remove("playerSide");
        Check("army.missing_allegiance_rejected",!DemoProtocol.ValidateMessage(invalid));
        var result=DemoProtocol.Msg("mc_army_spawn_result","army",3);
        result["requestId"]=batch["requestId"];result["eventId"]=Guid.NewGuid().ToString();result["ok"]=true;result["message"]="spawned 3 Withers";
        Check("army.reliable_spawn_receipt",DemoProtocol.ValidateMessage(result));
        var validator=new BridgeSessionValidator();validator.BeginSession("army");
        Check("army.spawn_receipt_registered",validator.Validate(result)==BridgeSessionValidator.Verdict.Ok);
        invalid=new Dictionary<string,object>(result){["sid"]="old",["seq"]=4L};
        Check("army.old_battle_receipt_rejected",validator.Validate(invalid)==BridgeSessionValidator.Verdict.StaleSession);
        invalid=new Dictionary<string,object>(result);invalid.Remove("eventId");
        Check("army.unreliable_spawn_receipt_rejected",!DemoProtocol.ValidateMessage(invalid));
        var port=FreePort();using var conn=new BridgeConnection();conn.Start(port,out _);
        using var client=new TcpClient("127.0.0.1",port);WaitConnected(conn,2000);
        bool sent=true;
        for(int pass=0;pass<2;pass++)for(int id=1;id<=600;id++)
            sent&=conn.Send(DemoProtocol.ActorUpsert("army",0,id,id,128,0,0,true,"Fian"));
        long previous=0;var ids=new HashSet<long>();bool intact=true;
        for(int i=0;i<1200;i++) {
            var frame=RecvLine(client.GetStream(),2000);
            if(frame==null||DemoProtocol.L(frame,"seq")<=previous){intact=false;break;}
            previous=DemoProtocol.L(frame,"seq");ids.Add(DemoProtocol.L(frame,"eid"));
        }
        Check("army.two600_pose_bursts_remain_ordered",sent&&intact&&ids.Count==600&&conn.State==BridgeConnection.BridgeState.Connected);
    }

    private static void CreatureDamageConfigTests()
    {
        string root=Path.Combine(Path.GetTempPath(),"mycraft-creature-config-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"Config"));
        string file=Path.Combine(root,"Config","demo.json");
        try
        {
            File.WriteAllText(file,"{\"enabled\":true,\"mcHitFactor\":1,\"mcCreatureDamageFactor\":5}");
            var config=Mycraft.Demo.DemoConfig.Load(root);
            Check("creature.config_maps_skull8_to40",config.Enabled&&Mycraft.Demo.CombatEventLedger.ToBannerlordDamage(8*config.McCreatureDamageFactor,config.McHitFactor)==40);
            Check("creature.config_maps_wither_tick1_to5",Mycraft.Demo.CombatEventLedger.ToBannerlordDamage(config.McCreatureDamageFactor,config.McHitFactor)==5);
            Check("creature.player_weapons_and_tnt_keep_factor1",config.McHitFactor==1&&Mycraft.Demo.CombatEventLedger.ToBannerlordDamage(8,config.McHitFactor)==8);
            File.WriteAllText(file,"{\"enabled\":true,\"mcHitFactor\":1}");
            Check("creature.older_config_inherits_requested_factor5",Mycraft.Demo.DemoConfig.Load(root).McCreatureDamageFactor==5);
            File.WriteAllText(file,"{\"enabled\":true,\"mcCreatureDamageFactor\":-1}");
            Check("creature.invalid_factor_disables_bad_config",!Mycraft.Demo.DemoConfig.Load(root).Enabled);
        }
        finally{File.Delete(file);Directory.Delete(Path.Combine(root,"Config"));Directory.Delete(root);}
    }

    private static void EntityWireTests()
    {
        const string uuid="7ad989a9-4be7-4a9a-9f41-68cae184cd57";
        var mob=DemoProtocol.Msg("mob_state","entities",1);
        mob["entityId"]=uuid;mob["kind"]="wither_skeleton";mob["mainItem"]="minecraft:stone_sword";
        foreach(var key in new[]{"x","z","yaw","bodyYaw","headYaw","pitch","vx","vy","vz","walkPosition","walkSpeed","attackProgress"})mob[key]=0d;
        mob["y"]=128d;mob["hp"]=20d;mob["maxHp"]=20d;mob["alive"]=true;mob["aggressive"]=false;
        mob["nativeTick"]=1L;mob["targetEid"]=1;mob["hurtTime"]=0;mob["deathTime"]=0;
        Check("entity.original_wither_state",DemoProtocol.ValidateMessage(mob));
        var invalid=new Dictionary<string,object>(mob){["walkSpeed"]=double.NaN};
        Check("entity.nonfinite_pose_rejected",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(mob){["entityId"]="not-an-entity"};
        Check("entity.stable_uuid_required",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(mob){["kind"]="zombie"};
        Check("entity.other_species_not_fabricated",!DemoProtocol.ValidateMessage(invalid));
        var generic=new Dictionary<string,object>(mob){["kind"]="cow",["visualKey"]="1234567890abcdef1234567890abcdef",["width"]=.9d,["height"]=1.4d,["hostile"]=false,["baby"]=false,["boss"]=false,["bossName"]=""};
        Check("entity.native_passive_species_with_original_model",DemoProtocol.ValidateMessage(generic));
        invalid=new Dictionary<string,object>(generic){["visualKey"]="../unsafe"};Check("entity.model_identity_stays_within_session",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(generic){["width"]=double.NaN};Check("entity.native_collider_requires_finite_dimensions",!DemoProtocol.ValidateMessage(invalid));
        var boss=new Dictionary<string,object>(generic){["kind"]="wither",["hp"]=100d,["maxHp"]=300d,["hostile"]=true,["boss"]=true,["bossName"]="Wither",["invulnerableTicks"]=220};
        Check("entity.real_wither_birth_health",DemoProtocol.ValidateMessage(boss));
        var assigned=new Dictionary<string,object>(boss){["armySlot"]=201,["armyPlayerSide"]=false};
        Check("army.first_state_has_native_slot",DemoProtocol.ValidateMessage(assigned));
        invalid=new Dictionary<string,object>(assigned);invalid.Remove("armyPlayerSide");
        Check("army.slot_without_allegiance_rejected",!DemoProtocol.ValidateMessage(invalid));
        var bossLife=new McBossState(boss);Check("entity.bossbar_uses_original_health_ratio",Math.Abs(bossLife.Fraction-1d/3)<1e-8 && bossLife.Hp==100 && bossLife.MaxHp==300);
        boss["hp"]=150d;Check("entity.bossbar_half_life",new McBossState(boss).Fraction==.5d);
        var summon=DemoProtocol.Msg("mob_summon","entities",6);summon["requestId"]=Guid.NewGuid().ToString();summon["kind"]="wither";
        Check("entity.host_summon_without_fabricated_entity_id",DemoProtocol.ValidateMessage(summon));
        invalid=new Dictionary<string,object>(summon){["kind"]="../wither"};Check("entity.summon_registry_kind_only",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(mob){["hp"]=0d};
        Check("entity.alive_zero_hp_rejected",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(mob){["vy"]=11d};
        Check("entity.motion_budget_preserved",!DemoProtocol.ValidateMessage(invalid));
        var hurt=DemoProtocol.Msg("mob_hurt","entities",2);
        hurt["entityId"]=uuid;hurt["eventId"]=Guid.NewGuid().ToString();hurt["sourceEid"]=17;hurt["damage"]=3.75d;
        Check("entity.native_soldier_damage_wire",DemoProtocol.ValidateMessage(hurt));
        hurt["sourceKind"]="arrow";Check("entity.native_arrow_classification",DemoProtocol.ValidateMessage(hurt));
        invalid=new Dictionary<string,object>(hurt){["sourceKind"]="unknown"};Check("entity.unknown_native_damage_classification_rejected",!DemoProtocol.ValidateMessage(invalid));
        hurt.Remove("sourceKind");
        var tracker=new EventRetryTracker(4096);string eventId=DemoProtocol.S(hurt,"eventId");
        Check("entity.damage_has_reliable_outbox",tracker.Track(eventId,hurt,0));
        hurt["damage"]=99d;
        var due=tracker.TakeDue(500,out var exhausted);
        Check("entity.retry_keeps_original_damage_and_identity",due.Count==1 && !exhausted && DemoProtocol.D(due[0],"damage")==3.75 && DemoProtocol.S(due[0],"entityId")==uuid);
        Check("entity.damage_ack_retires",tracker.Acknowledge(eventId) && tracker.Count==0);
        var hit=DemoProtocol.Msg("mob_hit","entities",3);hit["eventId"]=Guid.NewGuid().ToString();hit["sourceId"]=uuid;hit["eid"]=17;hit["damage"]=1d;hit["kind"]="wither";
        Check("entity.native_wither_effect_event",DemoProtocol.ValidateMessage(hit));
        invalid=new Dictionary<string,object>(hit){["damage"]=0d};
        Check("entity.zero_damage_does_not_invent_hit",!DemoProtocol.ValidateMessage(invalid));
        var boat=DemoProtocol.Msg("boat_state","entities",4);boat["entityId"]=uuid;boat["kind"]="poplar_chest_boat";
        foreach(var key in new[]{"x","z","yaw","pitch","leftPaddle","rightPaddle","hurtTime","damage","bubbleAngle"})boat[key]=0d;
        boat["y"]=128d;boat["nativeTick"]=2L;boat["alive"]=true;boat["hurtDir"]=-1;boat["underWater"]=false;
        Check("entity.actual_new_boat_variant",DemoProtocol.ValidateMessage(boat));
        invalid=new Dictionary<string,object>(boat){["leftPaddle"]=true};
        Check("entity.paddle_angle_not_input_boolean",!DemoProtocol.ValidateMessage(invalid));
        invalid=new Dictionary<string,object>(boat){["kind"]="generic_boat"};
        Check("entity.unknown_boat_not_substituted",!DemoProtocol.ValidateMessage(invalid));
        var ground=DemoProtocol.Msg("mob_ground","entities",5);ground["entityId"]=uuid;ground["x"]=0d;ground["y"]=128d;ground["z"]=0d;ground["groundCount"]=1;
        ground["ground0X"]=0;ground["ground0Y"]=127;ground["ground0Z"]=0;ground["ground0H"]=.5d;
        Check("entity.actual_partial_ground_wire",DemoProtocol.ValidateMessage(ground));
        invalid=new Dictionary<string,object>(ground){["groundCount"]=82};
        Check("entity.ground_patch_budget",!DemoProtocol.ValidateMessage(invalid));
        var validator=new BridgeSessionValidator();validator.BeginSession("entities");
        Check("entity.state_registered_in_session_protocol",validator.Validate(mob)==BridgeSessionValidator.Verdict.Ok);
        boat["sid"]="old";
        Check("entity.old_battle_state_rejected",validator.Validate(boat)==BridgeSessionValidator.Verdict.StaleSession);
    }

    private static void WeaponWireTests()
    {
        var shot=DemoProtocol.Msg("projectile_state","weapons",1);
        shot["entityId"]="arrow-one";shot["kind"]="arrow";shot["x"]=1d;shot["y"]=129d;shot["z"]=2d;
        shot["yaw"]=90d;shot["pitch"]=0d;shot["alive"]=true;
        Check("weapon.native_flight_wire",DemoProtocol.ValidateMessage(shot));
        shot["x"]=double.NaN;Check("weapon.invalid_flight_rejected",!DemoProtocol.ValidateMessage(shot));shot["x"]=1d;
        shot["kind"]="sword";Check("weapon.unknown_projectile_rejected",!DemoProtocol.ValidateMessage(shot));shot["kind"]="trident";
        Check("weapon.native_trident_wire",DemoProtocol.ValidateMessage(shot));
        shot["vx"]=0d;shot["vy"]=-.1d;shot["vz"]=3d;
        Check("weapon.native_velocity_for_ahead_interest",DemoProtocol.ValidateMessage(shot));
        shot.Remove("vy");Check("weapon.partial_projectile_velocity_rejected",!DemoProtocol.ValidateMessage(shot));
        shot["vy"]=-.1d;
        var hit=DemoProtocol.Msg("weapon_hit","weapons",2);
        hit["eventId"]="impact-one";hit["sourceId"]="arrow-one";hit["kind"]="arrow";hit["eid"]=7;hit["damage"]=9d;
        var gate=new BridgeSessionValidator();gate.BeginSession("weapons");
        Check("weapon.async_damage_session_bound",gate.Validate(hit)==BridgeSessionValidator.Verdict.Ok);
        hit["sid"]="old";hit["seq"]=3L;Check("weapon.old_session_damage_rejected",gate.Validate(hit)==BridgeSessionValidator.Verdict.StaleSession);
        hit["damage"]=-1d;Check("weapon.invalid_native_damage_rejected",!DemoProtocol.ValidateMessage(hit));
        var ledger=new WeaponHitLedger();
        Check("weapon.async_damage_accepted",ledger.Register("one",9,0)==WeaponHitLedger.Result.Accepted);
        Check("weapon.retry_does_not_damage_twice",ledger.Register("one",9,1500)==WeaponHitLedger.Result.Duplicate);
        Check("weapon.piercing_second_target_distinct_event",ledger.Register("two",9,1500)==WeaponHitLedger.Result.Accepted);
        Check("weapon.nonfinite_damage_rejected",ledger.Register("bad",double.NaN,1500)==WeaponHitLedger.Result.Invalid);
        var pose=DemoProtocol.PlayerPose("weapons",4,0,128,0,0);pose["vx"]=.4d;pose["vy"]=0d;pose["vz"]=0d;
        Check("weapon.kinetic_motion_per_native_tick",DemoProtocol.ValidateMessage(pose));
        pose.Remove("vz");Check("weapon.partial_motion_rejected",!DemoProtocol.ValidateMessage(pose));
        var use=DemoProtocol.Msg("interact","weapons",5);use["requestId"]="button";use["pressed"]=true;use["target"]=false;use["cancel"]=false;
        Check("weapon.genuine_press_remains_valid",DemoProtocol.ValidateMessage(use));
        use["cancel"]=true;Check("weapon.cancellation_cannot_press",!DemoProtocol.ValidateMessage(use));
        use["pressed"]=false;Check("weapon.cancel_without_launch",DemoProtocol.ValidateMessage(use));
        var spin=DemoProtocol.Msg("player_motion","weapons",6);spin["spinning"]=true;spin["nativeTick"]=40L;
        spin["vx"]=0d;spin["vy"]=.5d;spin["vz"]=3d;
        Check("weapon.native_riptide_motion",DemoProtocol.ValidateMessage(spin));
        spin["vz"]=11d;Check("weapon.invalid_spin_velocity",!DemoProtocol.ValidateMessage(spin));
        var bolt=DemoProtocol.Msg("lightning_state","weapons",7);bolt["entityId"]="native-bolt";
        bolt["x"]=1d;bolt["y"]=128d;bolt["z"]=2d;bolt["seed"]="ffffffff87654321";bolt["visible"]=true;bolt["alive"]=true;
        Check("weapon.native_lightning_seed_preserved",DemoProtocol.ValidateMessage(bolt)&&BridgeJson.TryParse(BridgeJson.Write(bolt),out var boltCopy)&&DemoProtocol.S(boltCopy,"seed")=="ffffffff87654321");
        bolt["seed"]="seed";Check("weapon.invalid_lightning_seed",!DemoProtocol.ValidateMessage(bolt));
    }

    private static void HotbarTests()
    {
        var legacy = DemoProtocol.Msg("player_state", "test", 1);
        legacy["item"] = "minecraft:diamond_sword";
        Check("hotbar.legacy_held_item", McHotbarState.Read(legacy).Items[0] == "minecraft:diamond_sword");
        legacy["selectedSlot"] = 3L;
        legacy["food"] = 17L;
        legacy["armor"] = 8L;
        for (var i = 0; i < 9; i++)
        {
            legacy["slot" + i + "Item"] = i == 3 ? "minecraft:diamond_sword" : "minecraft:air";
            legacy["slot" + i + "Count"] = i == 3 ? 1L : 0L;
        }
        var inventory = McHotbarState.Read(legacy);
        Check("hotbar.real_nine_slots", inventory.Items.Length == 9 && inventory.Items[3] == "minecraft:diamond_sword"
            && inventory.SelectedSlot == 3 && inventory.Food == 17 && inventory.Armor == 8 && inventory.Counts[3] == 1);
        legacy["selectedSlot"] = 9L;
        var rejected = false;
        try { McHotbarState.Read(legacy); } catch (FormatException) { rejected = true; }
        Check("hotbar.invalid_selection_rejected", rejected);
        legacy["selectedSlot"] = 3L;
        legacy["slot0Count"] = 2L;
        rejected = false;
        try { McHotbarState.Read(legacy); } catch (FormatException) { rejected = true; }
        Check("hotbar.empty_item_count_rejected", rejected);
    }

    private static void HotbarWheelTests()
    {
        var wheel=new HotbarWheel();
        Check("wheel.up_wraps_to_ninth",wheel.Apply(0,120)==8);
        Check("wheel.down_wraps_to_first",wheel.Apply(8,-120)==0);
        Check("wheel.multi_notch",wheel.Apply(2,-360)==5);
        Check("wheel.partial_waits",wheel.Apply(2,60)==null);
        Check("wheel.partial_accumulates",wheel.Apply(2,60)==1);
        wheel.Apply(2,60);
        wheel.Clear();
        Check("wheel.menu_reset_discards_partial",wheel.Apply(2,60)==null);
        wheel.Clear();
        Check("wheel.no_input_no_change",wheel.Apply(3,0)==null);
        var select=DemoProtocol.SelectSlot("test",1,8);
        Check("wheel.selection_wire_valid",DemoProtocol.ValidateMessage(select) && DemoProtocol.L(select,"slot")==8);
        var validator=new BridgeSessionValidator();
        validator.BeginSession("test");
        Check("wheel.selection_session_bound",validator.Validate(select)==BridgeSessionValidator.Verdict.Ok);
        select["seq"]=2L; select["sid"]="old";
        Check("wheel.old_session_rejected",validator.Validate(select)==BridgeSessionValidator.Verdict.StaleSession);
        select["slot"]=9L;
        Check("wheel.invalid_slot_rejected",!DemoProtocol.ValidateMessage(select));
    }

    private static void InventoryTests()
    {
        var snapshot=DemoProtocol.Msg("inventory_state","test",1);
        snapshot["rev"]=1L;snapshot["selectedSlot"]=2L;snapshot["creative"]=true;
        for(var i=0;i<41;i++)
        {
            var prefix=i<36?"inv"+i:i<40?"armor"+(i-36):"offhand";
            snapshot[prefix+"Item"]=i==40?"minecraft:shield":"minecraft:air";
            snapshot[prefix+"Count"]=i==40?1L:0L;
        }
        var inventory=McInventoryState.Read(snapshot);
        Check("inventory.real41cells",inventory.Items.Length==41 && inventory.Items[40]=="minecraft:shield" && inventory.Creative);
        Check("inventory.snapshot_wire",DemoProtocol.ValidateMessage(snapshot));
        var hotbar=DemoProtocol.Msg("player_state","test",2);hotbar["item"]="minecraft:air";hotbar["offhandItem"]="minecraft:shield";hotbar["offhandCount"]=1L;
        Check("inventory.offhand_state",McHotbarState.Read(hotbar).OffhandItem=="minecraft:shield");
        snapshot["inv0Count"]=1.5;
        var rejected=false;try{McInventoryState.Read(snapshot);}catch(FormatException){rejected=true;}
        Check("inventory.fractional_stack_rejected",rejected);
        var swap=DemoProtocol.Msg("inventory_swap","test",2);swap["requestId"]="r";swap["rev"]=1L;swap["fromSlot"]=3L;swap["toSlot"]=40L;
        Check("inventory.swap_offhand_valid",DemoProtocol.ValidateMessage(swap));
        swap["toSlot"]=39L;Check("inventory.armor_write_rejected",!DemoProtocol.ValidateMessage(swap));
        var build=DemoProtocol.Msg("build_request","test",3);build["requestId"]="b";build["x"]=-2L;build["y"]=-60L;build["z"]=3L;build["action"]="place";
        Check("build.request_valid",DemoProtocol.ValidateMessage(build));
        build["y"]=-3000L;Check("build.out_of_bounds_rejected",!DemoProtocol.ValidateMessage(build));
    }

    private static void ShieldWireTests()
    {
        var press=DemoProtocol.UseOffhand("shield-test",1,true);
        Check("shield.press_wire",DemoProtocol.ValidateMessage(press));
        var release=DemoProtocol.UseOffhand("shield-test",2,false);
        Check("shield.release_wire",DemoProtocol.ValidateMessage(release));
        var validator=new BridgeSessionValidator();validator.BeginSession("shield-test");
        Check("shield.valid_session",validator.Validate(press)==BridgeSessionValidator.Verdict.Ok);
        release["sid"]="old";
        Check("shield.old_session_rejected",validator.Validate(release)==BridgeSessionValidator.Verdict.StaleSession);
        press["pressed"]="true";
        Check("shield.string_boolean_rejected",!DemoProtocol.ValidateMessage(press));
        press["pressed"]=true;press["hand"]="mainhand";
        Check("shield.unsupported_hand_rejected",!DemoProtocol.ValidateMessage(press));
    }

    private static void TntWireTests()
    {
        var origin=DemoProtocol.Msg("staging_origin","tnt",1);origin["offsetX"]=16384;origin["offsetZ"]=32768;
        Check("tnt.fresh_integer_staging_origin",DemoProtocol.ValidateMessage(origin));
        origin["offsetX"]=16384.5;Check("tnt.fractional_staging_rejected",!DemoProtocol.ValidateMessage(origin));
        var state=DemoProtocol.Msg("tnt_state","tnt",2);state["entityId"]="native-tnt";state["x"]=17465.5;state["y"]=129d;state["z"]=33558.5;state["fuse"]=80;state["alive"]=true;
        Check("tnt.native_fuse_position_valid",DemoProtocol.ValidateMessage(state));
        state["fuse"]=-1;Check("tnt.invalid_fuse_rejected",!DemoProtocol.ValidateMessage(state));state["fuse"]=0;state["alive"]=false;
        Check("tnt.native_removal_valid",DemoProtocol.ValidateMessage(state));
        var blast=DemoProtocol.Msg("explosion","tnt",3);blast["eventId"]="e";blast["blastId"]="b";blast["x"]=17465.5;blast["y"]=129d;blast["z"]=33558.5;blast["radius"]=4d;
        Check("tnt.actual_explosion_wire",DemoProtocol.ValidateMessage(blast));
        var hit=DemoProtocol.Msg("explosion_hit","tnt",4);hit["eventId"]="h";hit["blastId"]="b";hit["eid"]=2;hit["damage"]=20d;
        hit["knockbackX"]=.2;hit["knockbackY"]=.4;hit["knockbackZ"]=.1;
        Check("tnt.native_area_hit_wire",DemoProtocol.ValidateMessage(hit));
        hit["knockbackY"]=double.NaN;Check("tnt.invalid_impulse_rejected",!DemoProtocol.ValidateMessage(hit));
        var ledger=new ExplosionLedger();
        Check("tnt.no_hit_before_owned_blast",ledger.Register("h","b",false,0)==ExplosionLedger.Result.UnknownBlast);
        Check("tnt.visual_once",ledger.Register("e","b",true,0)==ExplosionLedger.Result.Accepted);
        Check("tnt.visual_retry_not_replayed",ledger.Register("e","b",true,100)==ExplosionLedger.Result.Duplicate);
        Check("tnt.area_hit_once",ledger.Register("h","b",false,200)==ExplosionLedger.Result.Accepted);
        Check("tnt.lost_ack_cannot_double_damage",ledger.Register("h","b",false,1000)==ExplosionLedger.Result.Duplicate);
        Check("tnt.expired_blast_cannot_authorize_new_hit",ledger.Register("late","b",false,11000)==ExplosionLedger.Result.UnknownBlast);
        ledger.Clear();Check("tnt.previous_session_event_not_authorized",ledger.Register("h","b",false,0)==ExplosionLedger.Result.UnknownBlast);
    }

    private static void ShieldImpactWireTests()
    {
        var hit=DemoProtocol.PlayerHurt("shield-hit",1,"damage-one",7,80);
        Check("shield_impact.legacy_message_remains_valid",DemoProtocol.ValidateMessage(hit));
        hit["x"]=10d;hit["y"]=128d;hit["z"]=20d;hit["yaw"]=-90d;hit["pitch"]=15d;
        hit["hasSourcePosition"]=true;hit["sourceX"]=13d;hit["sourceY"]=128d;hit["sourceZ"]=20d;
        Check("shield_impact.source_outside_proxy_budget_has_direction",DemoProtocol.ValidateMessage(hit));
        var roundtrip=BridgeJson.TryParse(BridgeJson.Write(hit),out var copy);
        Check("shield_impact.position_and_facing_survive_wire",roundtrip && DemoProtocol.ValidateMessage(copy) && DemoProtocol.D(copy,"sourceX")==13 && DemoProtocol.D(copy,"yaw")==-90);
        hit.Remove("sourceZ");Check("shield_impact.partial_source_rejected",!DemoProtocol.ValidateMessage(hit));hit["sourceZ"]=20d;
        hit["sourceX"]=double.NaN;Check("shield_impact.nan_source_rejected",!DemoProtocol.ValidateMessage(hit));hit["sourceX"]=13d;
        hit["hasSourcePosition"]=false;Check("shield_impact.unknown_source_cannot_have_fabricated_coordinates",!DemoProtocol.ValidateMessage(hit));hit["hasSourcePosition"]=true;
        hit.Remove("yaw");Check("shield_impact.partial_victim_pose_rejected",!DemoProtocol.ValidateMessage(hit));hit["yaw"]=-90d;
        hit["yaw"]=double.MaxValue;Check("shield_impact.out_of_float_range_yaw_rejected",!DemoProtocol.ValidateMessage(hit));hit["yaw"]=-90d;
        var result=DemoProtocol.Msg("player_hurt_result","shield-hit",2);
        result["eventId"]="damage-one";result["hpBefore"]=20d;result["hpAfter"]=20d;
        result["expectedBlocked"]=7d;result["actualBlocked"]=7d;result["nativeBlockChecked"]=true;
        result["sourceKnown"]=true;result["headYaw"]=-90d;
        Check("shield_impact.actual_block_diagnostic_wire",DemoProtocol.ValidateMessage(result));
        result["actualBlocked"]=-1d;Check("shield_impact.invalid_actual_block_rejected",!DemoProtocol.ValidateMessage(result));
    }

    private static void NativeInteractionWireTests()
    {
        var request=DemoProtocol.Msg("interact","native",1);
        request["requestId"]="press";request["pressed"]=true;request["target"]=true;
        request["x"]=-3;request["y"]=127;request["z"]=5;request["face"]=1;
        request["terrain"]=true;request["canPlace"]=true;
        request["hitX"]=-2.5;request["hitY"]=128.0;request["hitZ"]=5.5;
        Check("native.use_on_valid_hit",DemoProtocol.ValidateMessage(request));
        request["surfaceH"]=.625;request["hitY"]=127.625;
        request["groundCount"]=1;request["ground0X"]=-3;request["ground0Y"]=127;request["ground0Z"]=5;request["ground0H"]=.625;
        Check("tnt.fractional_ground_survives_json",BridgeJson.TryParse(BridgeJson.Write(request),out var groundCopy) &&
            DemoProtocol.ValidateMessage(groundCopy) && DemoProtocol.D(groundCopy,"surfaceH")==.625 && DemoProtocol.D(groundCopy,"ground0H")==.625);
        request["surfaceH"]=double.NaN;Check("tnt.invalid_surface_rejected",!DemoProtocol.ValidateMessage(request));request["surfaceH"]=.625;
        request["ground0H"]=1.001;Check("tnt.ground_above_cell_rejected",!DemoProtocol.ValidateMessage(request));
        request["ground0H"]=-.001;Check("tnt.ground_below_cell_rejected",!DemoProtocol.ValidateMessage(request));
        request.Remove("ground0H");request.Remove("surfaceH");
        Check("tnt.legacy_full_ground_remains_valid",DemoProtocol.ValidateMessage(request));
        request.Remove("ground0Z");Check("tnt.incomplete_ground_position_rejected",!DemoProtocol.ValidateMessage(request));
        request["groundCount"]=82;Check("tnt.terrain_patch_budget_enforced",!DemoProtocol.ValidateMessage(request));
        request.Remove("groundCount");request.Remove("ground0X");request.Remove("ground0Y");request["hitY"]=128.0;
        var validator=new BridgeSessionValidator();validator.BeginSession("native");
        Check("native.use_session_bound",validator.Validate(request)==BridgeSessionValidator.Verdict.Ok);
        request["face"]=6;Check("native.invalid_face",!DemoProtocol.ValidateMessage(request));request["face"]=1;
        request["hitY"]=129.0;Check("native.hit_outside_block",!DemoProtocol.ValidateMessage(request));request["hitY"]=128.0;
        request["hitX"]=double.NaN;Check("native.nonfinite_hit",!DemoProtocol.ValidateMessage(request));request["hitX"]=-2.5;
        request["pressed"]=false;Check("native.release_cannot_modify_target",!DemoProtocol.ValidateMessage(request));
        request["target"]=false;Check("native.release_without_target",DemoProtocol.ValidateMessage(request));
        request["type"]="mine";Check("native.stop_mining_without_target",DemoProtocol.ValidateMessage(request));
        request["pressed"]=true;request["target"]=true;Check("native.mine_face",DemoProtocol.ValidateMessage(request));
        request["seq"]=2L;request["sid"]="previous";
        Check("native.stale_mining_session",validator.Validate(request)==BridgeSessionValidator.Verdict.StaleSession);
        var pose=DemoProtocol.PlayerPose("native",3,0,128,0,45);pose["pitch"]=-60.0;
        Check("native.pitch_preserved",DemoProtocol.ValidateMessage(pose));
        pose["pitch"]=91.0;Check("native.invalid_pitch",!DemoProtocol.ValidateMessage(pose));
        var outcome=DemoProtocol.Msg("interaction_result","native",4);
        outcome["requestId"]="press";outcome["ok"]=false;outcome["message"]="cannot ignite this target";
        Check("native.use_failure_is_nonfatal",DemoProtocol.ValidateMessage(outcome));
    }

    private static void FabricFixtureTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "build-mycraft.ps1"))) root = root.Parent;
        var path = root == null ? "" : Path.Combine(root.FullName, "fabric", "build", "bridge-fixtures.jsonl");
        Check("fabric.fixtures_exist_run_gradle_first", File.Exists(path));
        if (!File.Exists(path)) return;
        var validator = new BridgeSessionValidator();
        validator.BeginSession("session");
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            count++;
            var parsed = BridgeJson.TryParse(line, out var message);
            Check("fabric.fixture_" + count + "_wire", parsed && DemoProtocol.ValidateMessage(message));
            if (!parsed) continue;
            Check("fabric.fixture_" + count + "_session", validator.Validate(message) == BridgeSessionValidator.Verdict.Ok);
            switch (DemoProtocol.S(message, "type"))
            {
                case "inventory_state":
                    var inventory = McInventoryState.Read(message);
                    Check("fabric.inventory_real_slots", inventory.Revision == 3 && inventory.Creative &&
                        inventory.Items[35] == "minecraft:stone" && inventory.Counts[35] == 64 &&
                        inventory.Items[39] == "minecraft:diamond_helmet" && inventory.Items[40] == "minecraft:shield");
                    break;
                case "player_state":
                    var hotbar = McHotbarState.Read(message);
                    Check("fabric.hotbar_offhand", hotbar.SelectedSlot == 2 && hotbar.Items[2] == "minecraft:diamond_sword" &&
                        hotbar.OffhandItem == "minecraft:shield" && hotbar.Counts[8] == 32);
                    if(DemoProtocol.S(message,"usedHand")=="offhand")
                        Check("fabric.shield_use_state", BridgeJson.B(message,"usingItem") && BridgeJson.B(message,"blocking"));
                    else
                        Check("fabric.mainhand_use_state",BridgeJson.B(message,"usingItem") && !BridgeJson.B(message,"blocking") &&
                            DemoProtocol.S(message,"usedHand")=="mainhand" && DemoProtocol.S(message,"item")=="minecraft:bow" &&
                            !BridgeJson.B(message,"mainHandBlock") && !BridgeJson.B(message,"offHandBlock"));
                    break;
            }
        }
        Check("fabric.all_fixtures_present", count == 9);
    }

    private static void BackgroundGreetingTests()
    {
        var port=FreePort();
        using var connection=new BridgeConnection();
        Check("greeting.start_with_background_hello",connection.Start(port,out _,"v1.5.4"));
        using var client=new TcpClient("127.0.0.1",port);
        var stream=client.GetStream();
        var hello=RecvLine(stream,2000);
        Check("greeting.arrives_without_game_tick",hello!=null && DemoProtocol.S(hello,"type")=="hello" && connection.GreetingSent);
        SendLine(stream,BridgeJson.Write(HelloAck(1)));
        // Simulate Bannerlord paused while the user is entering /mycraft connect.
        var sawBeat=false;
        for(var i=0;i<4;i++)
        {
            SendLine(stream,BridgeJson.Write(DemoProtocol.Msg("heartbeat","",i+2)));
            var frame=RecvLine(stream,1400);
            sawBeat|=frame!=null && DemoProtocol.S(frame,"type")=="heartbeat";
        }
        Check("greeting.paused_game_stays_connected",sawBeat && connection.State==BridgeConnection.BridgeState.Connected);
        Check("greeting.ack_waits_for_game_thread",TakeInbound(connection,out var inbound) && DemoProtocol.S(inbound,"type")=="hello_ack");
    }
}
