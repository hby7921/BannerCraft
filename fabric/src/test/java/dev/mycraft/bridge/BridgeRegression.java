package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.ArrayList;
import java.util.Collections;

/** No game fixture required: exercise wire boundaries and event replay protection. */
public final class BridgeRegression {
    private static int assertions;
    private static void check(boolean condition, String description) {
        assertions++;
        if (!condition) throw new AssertionError(description);
    }
    private static List<String> feed(LineFrames frames, byte[] bytes) throws IOException {
        return frames.accept(bytes, bytes.length);
    }
    private static void fails(Throwing action, String description) {
        try { action.run(); }
        catch (IOException | IllegalArgumentException expected) { assertions++; return; }
        throw new AssertionError(description);
    }
    private interface Throwing { void run() throws IOException; }

    private static JsonObject actorFrame(String sid,long sequence,int id,double x) {
        JsonObject m=Proto.msg(sid,sequence,"actor_upsert");m.addProperty("eid",id);m.addProperty("alive",true);
        m.addProperty("x",x);m.addProperty("y",128);m.addProperty("z",2);m.addProperty("hostile",false);return m;
    }
    private static JsonObject poseFrame(String sid,long sequence,double x) {
        JsonObject m=Proto.msg(sid,sequence,"player_pose");m.addProperty("x",x);m.addProperty("y",128);m.addProperty("z",2);
        m.addProperty("yaw",0);m.addProperty("pitch",0);return m;
    }
    private static void inboundQueueRegression() {
        SnapshotInboundQueue inbox=new SnapshotInboundQueue(1024,4096);inbox.reset(11);
        String sid="queue-session";long sequence=1;
        inbox.offer(11,Proto.msg("",sequence++,"hello"));
        JsonObject begin=Proto.msg(sid,sequence++,"begin_session");begin.addProperty("mode","demo_field");inbox.offer(11,begin);
        inbox.offer(11,poseFrame(sid,sequence++,1));
        for(int id=1;id<=600;id++)inbox.offer(11,actorFrame(sid,sequence++,id,0));
        // Twenty full600 snapshots arrive before the game thread can drain.
        // Every valid raw sequence participates in validation even when its pose is replaced.
        for(int round=1;round<=20;round++)for(int id=1;id<=600;id++) {
            inbox.offer(11,actorFrame(sid,sequence++,id,round));
            if(id==300)inbox.offer(11,poseFrame(sid,sequence++,round));
        }
        check(inbox.snapshotCount()==601 && inbox.seenCount()==601 && inbox.size()==1204,
                "12000 interleaved600 poses retain600 pinned births plus601 latest snapshots within fixed bounds");
        long hurtSequence=sequence;
        JsonObject hurt=Proto.msg(sid,sequence++,"mob_hurt");hurt.addProperty("sourceEid",600);hurt.addProperty("eventId","first-hurt");inbox.offer(11,hurt);
        JsonObject attack=Proto.msg(sid,sequence++,"attack_input");attack.addProperty("attackId","actual-input");inbox.offer(11,attack);
        for(int id=1;id<=600;id++)inbox.offer(11,actorFrame(sid,sequence++,id,21));
        for(int index=0;index<600;index++) {
            JsonObject ack=Proto.msg(sid,sequence++,"ack");ack.addProperty("ackEventId","ack"+index);inbox.offer(11,ack);
        }
        JsonObject remove=Proto.msg(sid,sequence++,"actor_remove");remove.addProperty("eid",42);inbox.offer(11,remove);
        inbox.offer(11,actorFrame(sid,sequence++,42,22));
        JsonObject secondHurt=Proto.msg(sid,sequence++,"mob_hurt");secondHurt.addProperty("sourceEid",42);secondHurt.addProperty("eventId","reentered-hurt");inbox.offer(11,secondHurt);
        inbox.offer(11,actorFrame(sid,sequence++,42,23));
        SessionRules consumer=new SessionRules();String consumedSid="";long last=0;boolean[] born=new boolean[601];
        int births=0,acknowledgements=0,hurtCount=0,attacks=0;boolean removed=false,reentered=false;double inputPose=0;int frameCount=0;
        SnapshotInboundQueue.Inbound event;
        while((event=inbox.poll())!=null) {
            JsonObject m=event.message();long seq=Proto.integer(m,"seq");check(seq>last,"coalesced retained frames remain strictly increasing");last=seq;frameCount++;
            check(consumer.accept(m,consumedSid)==SessionRules.Result.ACCEPT,"existing game-thread session validation accepts every retained frame");
            switch(Proto.type(m)) {
                case "begin_session" -> consumedSid=Proto.sid(m);
                case "player_pose" -> inputPose=Proto.number(m,"x");
                case "actor_upsert" -> {
                    int id=(int)Proto.integer(m,"eid");
                    if(!born[id]){born[id]=true;births++;if(id==42&&removed)reentered=true;}
                    if(Proto.number(m,"x")==21)check(seq>hurtSequence,"replaced latest actor pose is appended after original reliable hurt sequence");
                }
                case "mob_hurt" -> {check(born[(int)Proto.integer(m,"sourceEid")],"each reliable hurt sees its source's preserved preceding birth");hurtCount++;}
                case "attack_input" -> {check(inputPose==20 && born[600],"actual attack observes preceding latest player pose and registered600th soldier");attacks++;}
                case "ack" -> {check(Proto.string(m,"ackEventId").equals("ack"+acknowledgements),"all600 acknowledgements retain exact original ordering");acknowledgements++;}
                case "actor_remove" -> {born[42]=false;removed=true;}
                default -> { }
            }
        }
        check(births==601 && removed && reentered && acknowledgements==600 && hurtCount==2 && attacks==1 && frameCount<1900,
                "snapshot pressure preserves all reliable events and legitimate remove/reentry without processing12000 obsolete poses");

        // A replacement must not hide a bad version, reused raw sequence or bad static payload.
        inbox.reset(12);inbox.offer(12,actorFrame(sid,100,1,0));
        fails(()->inbox.offer(12,actorFrame(sid,99,1,1)),"replaced stale raw seq rejects before coalescing");
        JsonObject badVersion=actorFrame(sid,101,1,1);badVersion.addProperty("pv",2);
        fails(()->inbox.offer(12,badVersion),"replaced malformed protocol version rejects before coalescing");
        JsonObject badPose=actorFrame(sid,101,1,Double.NaN);
        fails(()->inbox.offer(12,badPose),"bad superseded snapshot payload cannot be silently hidden");
        check(inbox.size()==1 && Proto.number(inbox.poll().message(),"x")==0,"rejected malformed replacements do not mutate the retained original pose");
        inbox.reset(13);check(!inbox.offer(12,actorFrame(sid,1000,1,1)),"old generation raw data cannot enter reconnect");
        inbox.offer(13,actorFrame(sid,1,1,1));inbox.close(12,"stale-close");
        check(inbox.size()==1,"old connection close cannot erase the new generation");
        inbox.close(13,"actual-loss");inbox.close(13,"duplicate-loss");
        check(inbox.size()==1 && !inbox.offer(13,actorFrame(sid,2,1,2)),"transport close replaces damage with one marker and blocks later same-generation enqueue");
        check(inbox.poll().closeReason().equals("actual-loss") && inbox.poll()==null,"first close reason is bounded and preserved exactly");

        inbox.reset(14);inbox.offer(14,actorFrame(sid,1,1,1));inbox.offer(14,actorFrame(sid,2,1,2));
        inbox.offer(14,Proto.msg(sid,3,"end_session"));
        check(inbox.snapshotCount()==0 && inbox.seenCount()==0 && inbox.size()==2,"end barrier clears mutable pose caches while preserving original birth and reliable end");
        inbox.offer(14,actorFrame("next-session",4,1,3));
        check(inbox.reliableCount()==3,"same actor ID begins afresh after end without a permanent tombstone");
        inbox.reset(15);inbox.offer(15,actorFrame(sid,1,1,0));
        JsonObject dead=Proto.msg(sid,2,"actor_upsert");dead.addProperty("eid",1);dead.addProperty("alive",false);inbox.offer(15,dead);
        inbox.offer(15,actorFrame(sid,3,1,1));
        check(inbox.reliableCount()==3 && inbox.snapshotCount()==0,"alive=false upsert is an ordered retirement barrier and a later birth is preserved");

        SnapshotInboundQueue small=new SnapshotInboundQueue(2,3);small.reset(1);
        small.offer(1,actorFrame(sid,1,1,0));small.offer(1,actorFrame(sid,2,2,0));
        fails(()->small.offer(1,actorFrame(sid,3,3,0)),"new distinct snapshot keys cannot exceed their negotiated queue bound");
        small.offer(1,Proto.msg(sid,4,"ack"));
        fails(()->small.offer(1,Proto.msg(sid,5,"ack")),"reliable pressure rejects explicitly instead of dropping events or growing indefinitely");
        check(small.size()==3,"reliable overflow leaves all original reliable nodes intact until transport close");
    }
    private static void groundLeaseRegression(){
        GroundSupportLeases leases=new GroundSupportLeases(4096);
        var cell=new GroundSupportLeases.Cell(-1,128,-1);
        leases.touch(cell,0,false);
        check(leases.keep(cell,19,false,false,List.of(),java.util.Map.of()),"recent sampled native ground is retained before its20tick expiry");
        check(!leases.keep(cell,20,false,false,List.of(),java.util.Map.of()),"unused sampled ground retires at its bounded lease expiry");
        leases.touch(cell,20,true);
        check(leases.keep(cell,119,false,false,List.of(),java.util.Map.of()),"real recent interaction protects support for100ticks");
        check(!leases.keep(cell,120,false,false,List.of(),java.util.Map.of()),"interaction retention expires without permanent ground growth");
        var flying=new GroundSupportLeases.Region(-2,120,-2,2,145,2);
        check(leases.keep(cell,200,false,false,List.of(flying),java.util.Map.of()),"flying body's16cell ground corridor retains native collision support");
        var replacement=new GroundSupportLeases.Cell(-1,129,-1);
        var next=java.util.Map.of(cell.column(),replacement);
        check(!leases.keep(cell,200,false,false,List.of(flying),next),"old column layer can retire after real host terrain resampling despite proximity");
        check(leases.keep(cell,200,true,false,List.of(),next)&&leases.keep(cell,200,false,true,List.of(),next),
                "visible attachments or a genuine physical body retain old layer across sampled height changes");
        leases.forget(cell);check(leases.size()==0&&leases.columnCount()==0,"ground retirement removes all corresponding bounded metadata");
        var supports=new java.util.LinkedHashSet<GroundSupportLeases.Cell>();
        int sampled=0,peak=0;
        for(int layer=0;layer<80;layer++){
            var incoming=new java.util.HashMap<Long,GroundSupportLeases.Cell>();
            for(int x=-4;x<=4;x++)for(int z=-4;z<=4;z++){
                var p=new GroundSupportLeases.Cell(x,layer%24,z);incoming.put(p.column(),p);
            }
            for(var p:new ArrayList<>(supports))if(!incoming.containsValue(p)&&!leases.keep(p,layer,false,false,List.of(),incoming)){
                supports.remove(p);leases.forget(p);
            }
            for(var p:incoming.values()){supports.add(p);leases.touch(p,layer,false);sampled++;}
            peak=Math.max(peak,supports.size());
            check(supports.size()<=4096&&leases.size()==supports.size(),"rolling sampled layers keep metadata and physical support budget synchronized");
        }
        check(sampled>4096&&peak==81&&leases.columnCount()==81,"6480 original height samples occupy only the81 currently needed native columns");
        leases.clear();check(leases.size()==0&&leases.columnCount()==0,"session end clears all ground retention indexes");
    }

    public static void main(String[] args) throws IOException {
        inboundQueueRegression();
        groundLeaseRegression();
        LineFrames frames = new LineFrames(64);
        check(feed(frames, "{\"name\":\"".getBytes(StandardCharsets.UTF_8)).isEmpty(), "partial JSON waits");
        byte[] text = "骑砍\"}\n".getBytes(StandardCharsets.UTF_8);
        check(feed(frames, new byte[]{text[0]}).isEmpty(), "split UTF-8 waits");
        check(feed(frames, java.util.Arrays.copyOfRange(text, 1, text.length)).equals(List.of("{\"name\":\"骑砍\"}")), "UTF-8 preserved");
        check(feed(frames, "{}\n{}\r\n".getBytes(StandardCharsets.UTF_8)).equals(List.of("{}", "{}\r")), "batched CRLF frames");
        fails(() -> feed(new LineFrames(4), "12345".getBytes(StandardCharsets.UTF_8)), "oversize without newline rejected");
        check(feed(new LineFrames(4), "123\n".getBytes(StandardCharsets.UTF_8)).size() == 1, "newline counted in limit");
        fails(() -> feed(new LineFrames(4), "1234\n".getBytes(StandardCharsets.UTF_8)), "newline over limit rejected");
        fails(() -> feed(new LineFrames(64), new byte[]{(byte)0xFF, '\n'}), "invalid UTF-8 rejected");
        fails(() -> feed(new LineFrames(64), "\n".getBytes(StandardCharsets.UTF_8)), "empty frames rejected");

        long[] now = {0};
        BoundedIds ids = new BoundedIds(2, 10, () -> now[0]);
        check(ids.add("a"), "first hit accepted");
        check(!ids.add("a"), "duplicate hit rejected");
        check(!ids.add(""), "empty attack rejected");
        check(ids.add("b"), "second retained event accepted");
        fails(() -> ids.add("c"), "full cache cannot evict events inside retry window");
        check(!ids.add("b"), "recent duplicate retained");
        now[0] = 9;
        check(!ids.add("a"), "event remains deduplicated throughout retry window");
        now[0] = 10;
        check(ids.add("c"), "expired events release capacity for long battles");
        for (int i = 0; i < 1000; i++) {
            now[0] += 10;
            check(ids.add("long-battle-" + i), "battle length does not exhaust retry cache");
        }
        ids.clear();
        check(ids.add("a"), "new session forgets old ids");

        JsonObject message = Proto.msg("session", 1, "player_pose");
        check(Proto.validateEnvelope(message), "valid envelope accepted");
        message.addProperty("pv", 1.5);
        check(!Proto.validateEnvelope(message), "fractional version rejected");
        message.addProperty("pv", 1);
        message.addProperty("seq", 0);
        check(!Proto.validateEnvelope(message), "zero sequence rejected");
        message.addProperty("seq", "1");
        check(!Proto.validateEnvelope(message), "numeric strings rejected");
        message.addProperty("seq", 1);
        message.remove("sid");
        check(!Proto.validateEnvelope(message), "missing sid rejected");
        message.addProperty("damage", Double.NaN);
        fails(() -> Proto.number(message, "damage"), "nonfinite damage rejected");
        check(Proto.helloAck("实际玩家").get("playerName").getAsString().equals("实际玩家"), "hello carries actual player name");

        var hotbarItems = new ArrayList<>(Collections.nCopies(9, new HotbarState.Slot("minecraft:air", 0)));
        hotbarItems.set(2, new HotbarState.Slot("minecraft:diamond_sword", 1));
        hotbarItems.set(8, new HotbarState.Slot("minecraft:apple", 32));
        HotbarState hotbar = new HotbarState(2, 17, 8, hotbarItems);
        JsonObject playerState = Proto.playerState("session", 9, 12, 20, true, "minecraft:diamond_sword", hotbar);
        check(playerState.get("hp").getAsFloat() == 12 && playerState.get("alive").getAsBoolean(), "inventory extension preserves health state");
        check(playerState.get("item").getAsString().equals("minecraft:diamond_sword"), "inventory extension preserves main hand");
        check(playerState.get("selectedSlot").getAsInt() == 2 && playerState.get("food").getAsInt() == 17
                && playerState.get("armor").getAsInt() == 8, "real player display values serialized");
        check(playerState.get("slot2Item").getAsString().equals("minecraft:diamond_sword")
                && playerState.get("slot8Count").getAsInt() == 32, "all hotbar positions retain items and counts");
        check(playerState.get("slot0Item").getAsString().equals("minecraft:air")
                && playerState.get("slot0Count").getAsInt() == 0, "empty slots retain explicit air and zero");
        check(playerState.entrySet().stream().allMatch(entry -> entry.getValue().isJsonPrimitive()), "inventory wire fields remain flat");
        hotbarItems.set(2, new HotbarState.Slot("minecraft:air", 0));
        check(hotbar.slots().get(2).item().equals("minecraft:diamond_sword"), "inventory snapshot detached from mutable source");
        check(!hotbar.equals(new HotbarState(2, 17, 8, hotbarItems)), "item change triggers snapshot inequality");
        check(!hotbar.equals(new HotbarState(3, 17, 8, hotbar.slots())), "selection change detected");
        check(!hotbar.equals(new HotbarState(2, 16, 8, hotbar.slots())), "food change detected");
        check(!hotbar.equals(new HotbarState(2, 17, 9, hotbar.slots())), "armor change detected");
        var changedCounts = new ArrayList<>(hotbar.slots());
        changedCounts.set(8, new HotbarState.Slot("minecraft:apple", 31));
        check(!hotbar.equals(new HotbarState(2, 17, 8, changedCounts)), "stack count change detected");
        fails(() -> new HotbarState(9, 20, 0, hotbar.slots()), "invalid hotbar selection rejected");
        fails(() -> new HotbarState(0, 21, 0, hotbar.slots()), "invalid food rejected");
        fails(() -> new HotbarState(0, 20, -1, hotbar.slots()), "invalid armor rejected");
        fails(() -> new HotbarState(0, 20, 0, List.of()), "incomplete inventory rejected");
        fails(() -> new HotbarState.Slot("minecraft:air", 1), "air cannot claim an item count");
        fails(() -> new HotbarState.Slot("minecraft:apple", 0), "nonempty registry item requires count");
        fails(() -> new HotbarState.Slot("invalid item", 1), "invalid registry id rejected");
        check(!Proto.playerState("session", 9, 12, 20, true, "minecraft:diamond_sword").has("selectedSlot"),
                "legacy state builder remains available without optional fields");

        HotbarState shieldState = new HotbarState(2, 17, 8, hotbar.slots(), "minecraft:shield", 1);
        JsonObject shieldWire = Proto.playerState("session", 10, 12, 20, true, "minecraft:diamond_sword", shieldState, true, true);
        check(shieldWire.get("offhandItem").getAsString().equals("minecraft:shield")
                && shieldWire.get("offhandCount").getAsInt() == 1, "actual offhand item and count serialize");
        check(!shieldState.equals(hotbar), "offhand item change triggers state snapshot update");
        check(!shieldState.equals(new HotbarState(2, 17, 8, hotbar.slots(), "minecraft:shield", 2)),
                "offhand count change triggers state snapshot update");
        check(playerState.get("offhandItem").getAsString().equals("minecraft:air")
                && playerState.get("offhandCount").getAsInt() == 0, "legacy hotbar constructor sends empty offhand");
        fails(() -> new HotbarState(2, 17, 8, hotbar.slots(), "minecraft:air", 1), "invalid offhand snapshot rejected");

        check(shieldWire.get("usingItem").getAsBoolean() && shieldWire.get("blocking").getAsBoolean()
                && shieldWire.get("usedHand").getAsString().equals("offhand"),
                "shield fixture carries authoritative use and blocking separately");
        var warmingShield = Proto.playerState("session", 11, 12, 20, true, "minecraft:diamond_sword", shieldState, true, false);
        check(warmingShield.get("usingItem").getAsBoolean() && !warmingShield.get("blocking").getAsBoolean(),
                "vanilla warm-up may use the shield before blocking becomes active");
        var releasedShield = Proto.playerState("session", 12, 12, 20, true, "minecraft:diamond_sword", shieldState, false, false);
        check(releasedShield.get("usedHand").getAsString().equals("none"), "released shield has no active hand");
        check(Proto.playerState("session", 12, 12, 20, true, "minecraft:bow", shieldState, true, false, "mainhand")
                        .get("usedHand").getAsString().equals("mainhand"), "native main-hand use is represented without claiming an offhand shield");
        fails(() -> Proto.playerState("session", 12, 12, 20, true, "minecraft:bow", shieldState, false, false, "mainhand"),
                "released main hand cannot remain marked active");
        fails(() -> Proto.playerState("session", 12, 12, 20, true, "minecraft:diamond_sword", shieldState, false, true),
                "cannot report blocking without item use");
        JsonObject use = Proto.msg("session", 13, "use_item");
        use.addProperty("hand", "offhand");
        use.addProperty("pressed", true);
        check(Proto.useItemPressed(use), "offhand shield press parsed");
        use.addProperty("pressed", false);
        check(!Proto.useItemPressed(use), "offhand shield release parsed");
        use.addProperty("pressed", "true");
        fails(() -> Proto.useItemPressed(use), "string shield boolean rejected");
        use.addProperty("pressed", 1);
        fails(() -> Proto.useItemPressed(use), "numeric shield boolean rejected");
        use.remove("pressed");
        fails(() -> Proto.useItemPressed(use), "missing shield press rejected");
        use.addProperty("pressed", true);
        use.addProperty("hand", "mainhand");
        fails(() -> Proto.useItemPressed(use), "mainhand item use is outside current shield contract");
        use.remove("hand");
        fails(() -> Proto.useItemPressed(use), "missing shield hand rejected");
        var lease = new ItemUseLease();
        check(lease.update(true, 0) && lease.held(), "shield press starts one vanilla use");
        check(!lease.update(true, 250_000_000L), "held refresh does not restart shield warm-up");
        check(!lease.expired(999_999_999L), "renewal extends use until full timeout");
        check(lease.expired(1_000_000_000L), "missing refresh expires at 750 ms");
        lease.clear();
        check(!lease.held() && !lease.expired(2_000_000_000L), "timeout or disconnect clears input");
        check(lease.update(true, 2_000_000_000L), "fresh press starts after timeout");
        check(!lease.update(false, 2_100_000_000L) && !lease.held(), "release clears held input");
        check(lease.update(true, 3_000_000_000L), "next press begins a new native use");
        lease.cancelUntilRelease();
        check(!lease.held() && !lease.update(true, 3_250_000_000L),
                "inventory or slot mutation prevents a delayed held refresh restarting shield");
        lease.update(false, 3_300_000_000L);
        check(lease.update(true, 3_400_000_000L), "release and new press rearm shield after mutation");
        lease.clear();
        check(lease.update(true, 0), "new session resets timeout and release latch");

        var discarded = new ArrayList<String>();
        ActorRegistry<String> actors = new ActorRegistry<>(discarded::add);
        String first = actors.getOrCreate(1, () -> "first");
        String second = actors.getOrCreate(2, () -> "second");
        check(actors.values().size() == 2 && first.equals("first") && second.equals("second"), "multiple actors coexist");
        check(actors.getOrCreate(1, () -> { throw new AssertionError("duplicate spawn"); }) == first,
                "same actor update retains its original proxy without spawning");
        actors.remove(1);
        check(actors.get(1) == null && actors.get(2) == second && discarded.equals(List.of("first")),
                "removal discards exactly the requested actor");
        actors.remove(1);
        check(discarded.size() == 1, "repeat removal does not discard another actor");
        check(actors.getOrCreate(1, () -> "returned").equals("returned"), "actor can re-enter with the same eid");
        actors.clear();
        check(actors.values().isEmpty() && discarded.equals(List.of("first", "second", "returned")),
                "session cleanup discards every remaining proxy once");
        fails(() -> ActorRegistry.checkedId(0), "zero actor id rejected");
        fails(() -> ActorRegistry.checkedId(-1), "negative actor id rejected");
        fails(() -> ActorRegistry.checkedId((long) Integer.MAX_VALUE + 1), "oversized actor id rejected");
        for (int i = 1; i <= ActorRegistry.CAPACITY; i++) {
            int eid = i;
            actors.getOrCreate(eid, () -> "actor" + eid);
        }
        check(actors.values().size() == 64, "all 64 actor slots are accepted");
        fails(() -> actors.getOrCreate(65, () -> "overflow"), "capacity overflow rejected before spawning");
        check(actors.getOrCreate(1, () -> "duplicate").equals("actor1"), "existing actor still updates at capacity");
        actors.remove(20);
        check(actors.getOrCreate(65, () -> "replacement").equals("replacement"), "removed slot frees capacity");
        actors.clear();
        fails(() -> actors.getOrCreate(1, () -> null), "failed entity spawn is not registered");
        check(actors.values().isEmpty(), "spawn failure leaves registry empty");
        fails(() -> actors.setCapacity(200), "unnegotiated army capacity rejected");
        actors.setCapacity(256);
        for(int i=1;i<=256;i++) {
            int eid=i;
            actors.getOrCreate(eid,()->"army"+eid);
        }
        check(actors.values().size()==256 && actors.get(200).equals("army200"), "negotiated army preserves soldiers beyond old64 limit");
        fails(() -> actors.setCapacity(64), "cannot shrink a live army registry");
        fails(() -> actors.getOrCreate(257,()->"overflow"), "negotiated army still has a strict bound");
        actors.clear();
        actors.setCapacity(64);
        for(int i=1;i<=64;i++) {
            int eid=i;
            actors.getOrCreate(eid,()->"ordinary"+eid);
        }
        fails(() -> actors.getOrCreate(65,()->"overflow"), "next ordinary session restores default64 bound");
        actors.clear();

        int[] largeArmySpawned = { 0 };
        int beforeLargeArmyDiscard = discarded.size();
        actors.setCapacity(640);
        for(int i=1;i<=600;i++) {
            int eid=i;
            actors.getOrCreate(eid,()->{largeArmySpawned[0]++;return "largeArmy"+eid;});
        }
        check(actors.values().size()==600 && largeArmySpawned[0]==600
                        && actors.get(1).equals("largeArmy1") && actors.get(256).equals("largeArmy256")
                        && actors.get(600).equals("largeArmy600"),
                "negotiated640 registry retains all600 actual soldier identities and creates every proxy once");
        check(actors.getOrCreate(600,()->{throw new AssertionError("duplicate600th proxy spawn");}).equals("largeArmy600"),
                "600th actor refresh keeps its existing real proxy without duplicate creation");
        fails(()->actors.setCapacity(640), "cannot renegotiate the same capacity on a live600 army");
        fails(()->actors.setCapacity(256), "cannot shrink a live600 army to the previous negotiated limit");
        for(int i=601;i<=640;i++) {
            int eid=i;
            actors.getOrCreate(eid,()->{largeArmySpawned[0]++;return "largeArmy"+eid;});
        }
        check(actors.values().size()==640 && largeArmySpawned[0]==640 && actors.get(640).equals("largeArmy640"),
                "large preset retains negotiated headroom through exactly the640th real proxy");
        fails(()->actors.getOrCreate(641,()->{largeArmySpawned[0]++;return "mustNotSpawn";}),
                "641st actor rejects before invoking native spawn factory");
        check(largeArmySpawned[0]==640 && actors.get(641)==null,
                "640 capacity rejection has no hidden native spawn or partial registration");
        actors.clear();
        check(actors.values().isEmpty() && discarded.size()-beforeLargeArmyDiscard==640,
                "large session teardown discards every one of its640 original proxies exactly once");
        actors.setCapacity(64);
        for(int i=1;i<=64;i++) {
            int eid=i;
            actors.getOrCreate(eid,()->"afterLarge"+eid);
        }
        fails(()->actors.getOrCreate(65,()->"overflow"), "ordinary session following640 restores original64 strict budget");
        actors.clear();

        var near = new net.minecraft.world.phys.AABB(-0.3, 0, 1, 0.3, 2, 1.6);
        var far = new net.minecraft.world.phys.AABB(-0.3, 0, 2, 0.3, 2, 2.6);
        var offRay = new net.minecraft.world.phys.AABB(1, 0, 1, 1.6, 2, 1.6);
        var eye = new net.minecraft.world.phys.Vec3(0, 1.6, 0);
        var end = new net.minecraft.world.phys.Vec3(0, 1.6, 3);
        check(NearestRayTarget.select(List.of(far, offRay, near), eye, end, box -> box, box -> true) == near,
                "nearest ray intersection wins irrespective of insertion order");
        check(NearestRayTarget.select(List.of(near, far), eye, end, box -> box, box -> box != near) == far,
                "dead or occluded actors are skipped before selecting");
        check(NearestRayTarget.select(List.of(offRay), eye, end, box -> box, box -> true) == null,
                "off-ray target never receives damage");
        check(NearestRayTarget.select(List.of(far), eye, new net.minecraft.world.phys.Vec3(0, 1.6, 0.9),
                box -> box, box -> true) == null, "targets beyond vanilla reach are excluded");
        var enclosingEye = new net.minecraft.world.phys.AABB(-0.3, 0, -0.3, 0.3, 2, 0.3);
        check(NearestRayTarget.select(List.of(near, enclosingEye), eye, end, box -> box, box -> true) == enclosingEye,
                "eye inside proxy chooses distance zero");
        JsonObject hitActor = Proto.hitActor("session", 1, "damage-id", "attack-id", 31, 4, false);
        check(hitActor.get("eid").getAsInt() == 31, "hit carries the selected enemy id rather than fixed eid one");
        BoundedIds attackIds = new BoundedIds(256);
        int damageApplications = 0;
        for (int replay = 0; replay < 3; replay++) if (attackIds.add("attack-id")) damageApplications++;
        check(damageApplications == 1, "replayed attack permits exactly one damage application");

        SessionRules rules = new SessionRules();
        JsonObject begin = Proto.msg("session", 2, "begin_session");
        begin.addProperty("mode", "demo_field");
        fails(() -> rules.accept(begin, ""), "session before hello rejected");
        rules.reset();
        check(rules.accept(Proto.msg("", 1, "hello"), "") == SessionRules.Result.ACCEPT, "initial hello consumed before session");
        check(rules.accept(begin, "") == SessionRules.Result.ACCEPT, "hello then session accepted");
        check(rules.accept(Proto.msg("old-session", 3, "player_hurt"), "session") == SessionRules.Result.STALE_SESSION, "old damage discarded");
        check(rules.accept(Proto.msg("session", 4, "player_hurt"), "session") == SessionRules.Result.ACCEPT, "current session damage accepted");
        fails(() -> rules.accept(Proto.msg("session", 4, "player_hurt"), "session"), "same sequence replay rejected");
        fails(() -> rules.accept(Proto.msg("", 5, "hello"), "session"), "second handshake rejected");
        rules.reset();
        check(rules.accept(Proto.msg("", 1, "hello"), "") == SessionRules.Result.ACCEPT, "reconnect restarts handshake sequence");
        fails(() -> rules.accept(Proto.msg("", 2, "attack_input"), ""), "attack without session rejected");
        JsonObject selection=Proto.msg("session",10,"select_slot");
        selection.addProperty("slot",0);
        check(Proto.selectedSlot(selection)==0,"first hotbar slot accepted");
        selection.addProperty("slot",8);
        check(Proto.selectedSlot(selection)==8,"last hotbar slot accepted");
        check(Proto.selectedSlot(selection)==8,"absolute selection is idempotent");
        selection.addProperty("slot",9);
        fails(()->Proto.selectedSlot(selection),"slot above hotbar rejected");
        selection.addProperty("slot",-1);
        fails(()->Proto.selectedSlot(selection),"negative hotbar slot rejected");
        selection.addProperty("slot",1.5);
        fails(()->Proto.selectedSlot(selection),"fractional hotbar slot rejected");
        selection.addProperty("slot","2");
        fails(()->Proto.selectedSlot(selection),"string hotbar slot rejected");
        var mainInventory = new ArrayList<>(Collections.nCopies(36, new HotbarState.Slot("minecraft:air", 0)));
        var armorSlots = new ArrayList<>(Collections.nCopies(4, new HotbarState.Slot("minecraft:air", 0)));
        mainInventory.set(35, new HotbarState.Slot("minecraft:stone", 64));
        armorSlots.set(3, new HotbarState.Slot("minecraft:diamond_helmet", 1));
        var inventorySnapshot = new InventoryState(true, 8, mainInventory, armorSlots,
                new HotbarState.Slot("minecraft:shield", 1));
        var inventoryWire = inventorySnapshot.message("session", 3);
        check(inventoryWire.get("rev").getAsInt() == 3 && inventoryWire.get("creative").getAsBoolean(),
                "inventory states carry authoritative revision and actual mode");
        check(inventoryWire.get("inv35Item").getAsString().equals("minecraft:stone")
                && inventoryWire.get("inv35Count").getAsInt() == 64, "last main inventory stack preserved");
        check(inventoryWire.get("armor3Item").getAsString().equals("minecraft:diamond_helmet")
                && inventoryWire.get("offhandItem").getAsString().equals("minecraft:shield"),
                "armor and offhand are distinct real inventory fields");
        check(inventoryWire.entrySet().stream().allMatch(entry -> entry.getValue().isJsonPrimitive()),
                "full inventory remains flat on wire");
        mainInventory.set(35, new HotbarState.Slot("minecraft:air", 0));
        check(inventorySnapshot.inventory().get(35).count() == 64, "full inventory detached from mutable source");
        fails(() -> new InventoryState(false, 0, List.of(), armorSlots, hotbar.slots().get(0)),
                "partial full inventory rejected");
        fails(() -> inventorySnapshot.message("session", 0), "zero revision rejected");
        JsonObject swap = Proto.msg("session", 30, "inventory_swap");
        swap.addProperty("fromSlot", 40);
        check(BuildingRules.inventorySlot(swap, "fromSlot") == 40, "offhand slot can participate in swaps");
        swap.addProperty("fromSlot", 35);
        check(BuildingRules.inventorySlot(swap, "fromSlot") == 35, "last main slot can participate in swaps");
        swap.addProperty("fromSlot", 36);
        fails(() -> BuildingRules.inventorySlot(swap, "fromSlot"), "display-only armor slot cannot be mutated");
        swap.addProperty("fromSlot", 41);
        fails(() -> BuildingRules.inventorySlot(swap, "fromSlot"), "extra equipment indices rejected");
        JsonObject build = Proto.msg("session", 31, "build_request");
        build.addProperty("requestId", "build-1");
        build.addProperty("x", -2);
        build.addProperty("y", -60);
        build.addProperty("z", 1);
        build.addProperty("action", "place");
        var request = BuildingRules.read(build);
        check(request.x() == -2 && request.y() == -60, "negative world coordinates remain valid integer blocks");
        check(BuildingRules.inRange(request, -1.5, -59.5, 1.5), "block-center range uses all three axes");
        check(BuildingRules.inRange(request, -7.5, -59.5, 1.5), "exact six-block range accepted");
        check(!BuildingRules.inRange(request, -7.5001, -59.5, 1.5), "just beyond six-block range rejected");
        check(!BuildingRules.inRange(request, -1.5, -52.5, 1.5), "vertical distance also restricted");
        check(!BuildingRules.inRange(request, Double.NaN, 0, 0), "nonfinite player pose cannot authorize building");
        build.addProperty("x", 1.2);
        fails(() -> BuildingRules.read(build), "fractional grid position rejected");
        build.addProperty("x", 30_000_000);
        fails(() -> BuildingRules.read(build), "outside world coordinate rejected before chunk access");
        build.addProperty("x", 0);
        build.addProperty("y", 2049);
        fails(() -> BuildingRules.read(build), "height beyond shared demo protocol rejected");
        build.addProperty("y", -60);
        build.addProperty("action", "destroy_terrain");
        fails(() -> BuildingRules.read(build), "unrecognized construction action rejected");
        build.addProperty("action", "break");
        build.addProperty("requestId", " ");
        fails(() -> BuildingRules.read(build), "empty build request id rejected");
        build.addProperty("requestId", "a".repeat(129));
        fails(() -> BuildingRules.read(build), "oversized build request id rejected");
        check(BuildingRules.BLOCK_ITEMS.size() == 5 && !BuildingRules.BLOCK_ITEMS.contains("minecraft:tnt")
                && !BuildingRules.BLOCK_ITEMS.contains("minecraft:sand"), "palette excludes explosive and falling blocks");
        check(BuildingRules.CREATIVE_ITEMS.contains("minecraft:shield")
                && !BuildingRules.CREATIVE_ITEMS.contains("minecraft:command_block"), "creative give uses restricted actual item palette");
        RequestHistory<String> history = new RequestHistory<>(2);
        history.put("build-1", "placed");
        history.put("build-1", "would-place-again");
        check(history.get("build-1").equals("placed"), "duplicate request retains original authoritative outcome");
        history.put("swap-1", "swapped");
        check(!history.canAccept("build-2") && history.canAccept("build-1"), "full history rejects new mutations but accepts retries");
        fails(() -> history.put("build-2", "failed"), "full request history cannot silently evict prior outcomes");
        check(history.get("build-1").equals("placed") && history.get("swap-1").equals("swapped"),
                "capacity pressure retains earliest outcome so old swap retries cannot run twice");
        history.clear();
        check(history.get("swap-1") == null, "session reset forgets old request outcomes");
        check(history.canAccept("build-2"), "new session reopens bounded request budget");
        history.put("build-2", "failed");
        history.put("build-2", "succeeded-on-retry");
        check(history.get("build-2").equals("failed"), "failed requests also cannot mutate on retry");

        JsonObject buildSuccess = Proto.operationResult("session", "build_result", "build-2", true, "block placed");
        check(buildSuccess.get("ok").getAsBoolean()
                && buildSuccess.get("message").getAsString().equals("block placed")
                && buildSuccess.get("requestId").getAsString().equals("build-2"),
                "successful build result carries nonempty correlated notice required by C# validator");
        JsonObject swapSuccess = Proto.operationResult("session", "inventory_result", "swap-2", true, "inventory updated");
        check(swapSuccess.get("message").getAsString().equals("inventory updated"),
                "successful inventory result also carries a nonempty notice");
        JsonObject rejected = Proto.operationResult("session", "build_result", "build-3", false, "existing terrain cannot be overwritten");
        check(Proto.type(rejected).equals("build_result") && !rejected.get("ok").getAsBoolean(),
                "ordinary placement failure is a result rather than fatal error");
        fails(() -> Proto.operationResult("session", "build_result", "build-2", true, ""),
                "empty success message rejected before enqueue");
        fails(() -> Proto.operationResult("session", "build_result", "", true, "block placed"),
                "result without request correlation rejected");
        JsonObject blockWire = Proto.blockState("session", request, "minecraft:stone");
        check(blockWire.get("x").getAsInt() == -2 && blockWire.get("y").getAsInt() == -60
                && blockWire.get("item").getAsString().equals("minecraft:stone"),
                "authoritative construction preserves signed grid coordinates and palette item");
        JsonObject removedWire = Proto.blockState("session",
                new BuildingRules.Request("world-external-change", -2, -60, 1, "break"), "minecraft:air");
        check(removedWire.get("requestId").getAsString().equals("world-external-change")
                && removedWire.get("item").getAsString().equals("minecraft:air"),
                "unsolicited world removal has nonempty correlation and clears only mirrored block");
        fails(() -> Proto.blockState("session", new BuildingRules.Request("", 0, 0, 0, "break"), "minecraft:air"),
                "unsolicited state cannot carry empty request id");
        check(Proto.blockState("session", request, "minecraft:tnt").get("item").getAsString().equals("minecraft:tnt"),
                "native block state is not restricted to the old five-block palette");
        check(Proto.blockState("session", request, "minecraft:fire").get("item").getAsString().equals("minecraft:fire"),
                "block IDs preserve fire even though fire has no inventory item");
        fails(() -> Proto.blockState("session", request, "not an id"), "malformed block registry ID rejected");

        JsonObject interact = Proto.msg("session", 32, "interact");
        interact.addProperty("requestId", "native-1"); interact.addProperty("pressed", true);
        interact.addProperty("target", true); interact.addProperty("x", -2); interact.addProperty("y", -60);
        interact.addProperty("z", 1); interact.addProperty("face", 1); interact.addProperty("hitX", -1.5);
        interact.addProperty("hitY", -59); interact.addProperty("hitZ", 1.5);
        interact.addProperty("terrain", true); interact.addProperty("canPlace", true);
        var nativeRequest = NativeInteractionRules.read(interact, false);
        check(nativeRequest.terrain() && nativeRequest.canPlace() && nativeRequest.face() == 1,
                "native use preserves clicked face, exact hit, and synthetic-ground intent");
        interact.addProperty("face", 4294967296L);
        fails(() -> NativeInteractionRules.read(interact, false), "oversized face cannot wrap to a valid direction");
        interact.addProperty("face", 1); interact.addProperty("hitY", -57);
        fails(() -> NativeInteractionRules.read(interact, false), "hit outside clicked block rejected");
        interact.addProperty("hitY", -59); interact.addProperty("canPlace", "true");
        fails(() -> NativeInteractionRules.read(interact, false), "native placement clearance requires actual boolean");
        interact.addProperty("canPlace", true); interact.addProperty("pressed", false);
        fails(() -> NativeInteractionRules.read(interact, false), "release cannot silently carry a stale target");
        interact.addProperty("target", false);
        check(!NativeInteractionRules.read(interact, false).pressed(), "release parses without a world lookup");
        interact.addProperty("pressed", true); interact.addProperty("target", true);
        interact.remove("hitX"); interact.remove("hitY"); interact.remove("hitZ");
        check(NativeInteractionRules.read(interact, true).target(), "mining requires a block/face but not a use hit point");
        interact.addProperty("pitch", -90);
        check(Proto.pitch(interact) == -90, "down/up look extremes pass unchanged");
        interact.addProperty("pitch", 90.01);
        fails(() -> Proto.pitch(interact), "out-of-range pitch rejected");

        var useOrder = new ArrayList<String>();
        check(NativeInteractionRules.dispatch(true, hand -> {
            useOrder.add("on" + hand); return NativeInteractionRules.Result.SUCCESS;
        }, hand -> { throw new AssertionError("successful main-hand flint use must not raise shield"); })
                == NativeInteractionRules.Result.SUCCESS && useOrder.equals(List.of("on0")),
                "main-hand block use success terminates before offhand shield");
        useOrder.clear();
        check(NativeInteractionRules.dispatch(true, hand -> {
            useOrder.add("on" + hand); return NativeInteractionRules.Result.FAIL;
        }, hand -> { throw new AssertionError("failed useOn must stop general/offhand use"); })
                == NativeInteractionRules.Result.FAIL && useOrder.equals(List.of("on0")),
                "failed main-hand placement does not become offhand blocking");
        useOrder.clear();
        NativeInteractionRules.dispatch(true, hand -> {
            useOrder.add("on" + hand); return NativeInteractionRules.Result.PASS;
        }, hand -> {
            useOrder.add("use" + hand); return hand == 0 ? NativeInteractionRules.Result.PASS : NativeInteractionRules.Result.SUCCESS;
        });
        check(useOrder.equals(List.of("on0", "use0", "on1", "use1")), "native main-hand then offhand ordering retained");
        useOrder.clear();
        NativeInteractionRules.dispatch(false, hand -> { throw new AssertionError("air use cannot call useOn"); },
                hand -> { useOrder.add("use" + hand); return NativeInteractionRules.Result.SUCCESS; });
        check(useOrder.equals(List.of("use0")), "main-hand air use (food/bow/rocket) gets priority");
        useOrder.clear();
        check(NativeInteractionRules.dispatch(false, hand -> { throw new AssertionError("MISS cannot call useOn"); },
                hand -> { useOrder.add("use" + hand); return hand == 0 ? NativeInteractionRules.Result.FAIL : NativeInteractionRules.Result.SUCCESS; })
                == NativeInteractionRules.Result.SUCCESS && useOrder.equals(List.of("use0", "use1")),
                "vanilla main-hand air-use failure still allows successful offhand use");
        check(NativeInteractionRules.dispatch(false, hand -> { throw new AssertionError("MISS cannot call useOn"); },
                hand -> hand == 0 ? NativeInteractionRules.Result.FAIL : NativeInteractionRules.Result.PASS)
                == NativeInteractionRules.Result.FAIL,
                "native item-use rejection is retained for feedback if neither hand succeeds");
        check(blockWire.entrySet().stream().allMatch(entry -> entry.getValue().isJsonPrimitive())
                && buildSuccess.entrySet().stream().allMatch(entry -> entry.getValue().isJsonPrimitive()),
                "construction results remain flat JSON");
        SessionRules buildSession = new SessionRules();
        buildSession.accept(Proto.msg("", 1, "hello"), "");
        buildSession.accept(begin, "");
        check(buildSession.accept(Proto.msg("session", 3, "inventory_request"), "session") == SessionRules.Result.ACCEPT
                && buildSession.accept(Proto.msg("session", 4, "build_request"), "session") == SessionRules.Result.ACCEPT,
                "inventory and construction session ordering does not require any enemy actor");
        check(buildSession.accept(Proto.msg("old-session", 5, "inventory_swap"), "session") == SessionRules.Result.STALE_SESSION,
                "old-session inventory mutations rejected before reaching world runtime");

        // Use production builders, then stamp positive sequences just as BridgeClient does on enqueue.
        // C# integration tests consume these fixtures to catch cross-language schema drift.
        var fixtureLines = new ArrayList<String>();
        long fixtureSequence = 0;
        JsonObject nativeUseSuccess = Proto.operationResult("session", "interaction_result", "native-use-1", true, "native item used");
        JsonObject nativeHeld = Proto.playerState("session", 0, 12, 20, true, "minecraft:bow", shieldState, true, false, "mainhand");
        nativeHeld.addProperty("mainHandBlock", false);
        nativeHeld.addProperty("offHandBlock", false);
        for (JsonObject fixture : List.of(inventoryWire, blockWire, buildSuccess, swapSuccess,
                shieldWire, removedWire, rejected, nativeUseSuccess, nativeHeld)) {
            JsonObject wire = fixture.deepCopy();
            wire.addProperty("seq", ++fixtureSequence);
            check(Proto.validateEnvelope(wire), "cross-language fixture has valid transport envelope");
            fixtureLines.add(wire.toString());
        }
        java.nio.file.Path fixtures = java.nio.file.Path.of("build", "bridge-fixtures.jsonl");
        java.nio.file.Files.createDirectories(fixtures.getParent());
        java.nio.file.Files.write(fixtures, fixtureLines, StandardCharsets.UTF_8);
        System.out.println("Fabric bridge regression: " + assertions + " assertions passed");
    }
}
