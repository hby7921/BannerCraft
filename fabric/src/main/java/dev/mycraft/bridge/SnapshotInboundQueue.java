package dev.mycraft.bridge;

import com.google.gson.JsonObject;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

/** Wire-thread snapshots coalesce; dependency births and every action remain ordered and bounded. */
final class SnapshotInboundQueue {
    record Inbound(long generation, JsonObject message, String closeReason) { }
    private record Key(String sid, String type, int actor) { }
    private static final class Node {
        final Inbound value;
        Node previous, next;
        Key replacementKey;
        boolean reliable;
        Node(Inbound value, Key replacementKey, boolean reliable) {
            this.value=value;this.replacementKey=replacementKey;this.reliable=reliable;
        }
    }
    private final int snapshotLimit, reliableLimit;
    private final Map<Key,Node> latest = new HashMap<>();
    private final Set<Key> firstSeen = new HashSet<>();
    private Node head, tail;
    private int size, reliableCount;
    private long generation, lastRawSequence;
    private boolean closed;

    SnapshotInboundQueue(int snapshotLimit, int reliableLimit) {
        if(snapshotLimit<1||reliableLimit<1)throw new IllegalArgumentException("positive inbound budgets required");
        this.snapshotLimit=snapshotLimit;this.reliableLimit=reliableLimit;
    }
    synchronized void reset(long nextGeneration) {
        clearPending();generation=nextGeneration;lastRawSequence=0;closed=false;
    }
    synchronized boolean offer(long incomingGeneration, JsonObject message) {
        if(incomingGeneration!=generation||closed)return false;
        // Validate every original raw frame before anything can replace it.
        if(!Proto.validateEnvelope(message))throw new IllegalArgumentException("invalid inbound envelope");
        long sequence=Proto.integer(message,"seq");
        if(sequence<=lastRawSequence)throw new IllegalArgumentException("inbound raw sequence must increase");
        lastRawSequence=sequence;
        String type=Proto.type(message), sid=Proto.sid(message);
        Key key=null;
        boolean retiring=false;
        if(type.equals("actor_upsert")) {
            int actor=ActorRegistry.checkedId(Proto.integer(message,"eid"));
            retiring=!Proto.bool(message,"alive");
            if(!retiring){validateSnapshot(message,false);key=new Key(sid,type,actor);}
        } else if(type.equals("player_pose")) {
            validateSnapshot(message,true);key=new Key(sid,type,0);
        } else if(type.equals("actor_remove")) {
            ActorRegistry.checkedId(Proto.integer(message,"eid"));retiring=true;
        }
        if(key!=null) {
            if(!firstSeen.contains(key)) {
                if(firstSeen.size()>=snapshotLimit)throw new IllegalArgumentException("inbound distinct snapshot overflow");
                requireReliable(1);
                firstSeen.add(key);append(new Node(new Inbound(generation,message,null),null,true));
            } else {
                Node previous=latest.get(key);
                if(previous!=null)unlink(previous);
                else if(latest.size()>=snapshotLimit)throw new IllegalArgumentException("inbound snapshot overflow");
                Node next=new Node(new Inbound(generation,message,null),key,false);
                latest.put(key,next);append(next);
            }
            return true;
        }
        // Native item/attack input must see the preceding queued player position.
        // Pin just that pose; an ordinary ACK does not freeze all600 actor snapshots.
        Key playerKey=new Key(sid,"player_pose",0);
        Node pose=latest.get(playerKey);
        requireReliable(1+(pose==null?0:1));
        if(pose!=null){latest.remove(playerKey);pose.replacementKey=null;pose.reliable=true;reliableCount++;}
        if(retiring)forget(new Key(sid,"actor_upsert",ActorRegistry.checkedId(Proto.integer(message,"eid"))));
        if(type.equals("end_session"))clearSnapshotCaches();
        append(new Node(new Inbound(generation,message,null),null,true));
        return true;
    }
    private static void validateSnapshot(JsonObject message,boolean player) {
        double x=Proto.number(message,"x"),y=Proto.number(message,"y"),z=Proto.number(message,"z");
        if(Math.abs(x)>29_999_984||Math.abs(z)>29_999_984||y< -2048||y>2048)
            throw new IllegalArgumentException("inbound snapshot pose outside demo bounds");
        if(player){Proto.number(message,"yaw");Proto.pitch(message);}
        if(message.has("hostile"))Proto.bool(message,"hostile");
        if(message.has("vx")||message.has("vy")||message.has("vz"))
            for(String component:new String[]{"vx","vy","vz"})if(Math.abs(Proto.number(message,component))>10)
                throw new IllegalArgumentException("inbound snapshot motion outside bounds");
        if(message.has("hp")||message.has("maxHp")) {
            double hp=Proto.number(message,"hp"),maximum=Proto.number(message,"maxHp");
            if(hp<0||maximum<=0||hp>maximum||maximum>10000)throw new IllegalArgumentException("invalid inbound snapshot health");
        }
    }
    private void requireReliable(int additional) {
        if(reliableCount+additional>reliableLimit)throw new IllegalArgumentException("inbound reliable overflow");
    }
    private void forget(Key key) {
        Node old=latest.remove(key);if(old!=null)unlink(old);firstSeen.remove(key);
    }
    private void clearSnapshotCaches() {
        for(Node node:latest.values())unlink(node);
        latest.clear();firstSeen.clear();
    }
    synchronized Inbound poll() {
        if(head==null)return null;
        Node node=head;
        if(node.replacementKey!=null)latest.remove(node.replacementKey,node);
        unlink(node);return node.value;
    }
    synchronized int size() {return size;}
    synchronized int reliableCount() {return reliableCount;}
    synchronized int snapshotCount() {return latest.size();}
    synchronized int seenCount() {return firstSeen.size();}
    synchronized void close(long incomingGeneration,String reason) {
        if(incomingGeneration!=generation||closed)return;
        clearPending();closed=true;
        append(new Node(new Inbound(generation,null,reason==null?"closed":reason),null,true));
    }
    synchronized void clearPending() {
        head=tail=null;size=reliableCount=0;latest.clear();firstSeen.clear();
    }
    private void append(Node node) {
        node.previous=tail;if(tail!=null)tail.next=node;else head=node;tail=node;size++;if(node.reliable)reliableCount++;
    }
    private void unlink(Node node) {
        if(node.previous==null)head=node.next;else node.previous.next=node.next;
        if(node.next==null)tail=node.previous;else node.next.previous=node.previous;
        size--;if(node.reliable)reliableCount--;node.previous=node.next=null;
    }
}
