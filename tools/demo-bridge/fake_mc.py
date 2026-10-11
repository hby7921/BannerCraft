#!/usr/bin/env python3
"""fake_mc.py — Minecraft-side stand-in for the Mycraft demo bridge (protocol/demo-v1.md).

Connects to the Bannerlord-side TCP server and plays the MC role: hello_ack,
player_state, native-range hit resolution for attack_input, player_hurt intake.
For testing the C# server without launching Minecraft.

Usage: python fake_mc.py [port]   (default 38765)
"""
import json
import socket
import sys
import threading
import time
import uuid

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 38765
PV = 1
SEQ = [0]
STATE = {
    "sid": "",
    "hp": 20.0,
    "max_hp": 20.0,
    "alive": True,
    "item": "minecraft:iron_sword",
    "actor": None,       # (eid, x, z) latest actor pose
    "active": False,
}

def next_seq():
    SEQ[0] += 1
    return SEQ[0]

def envelope(msg_type, **fields):
    m = {"type": msg_type, "pv": PV, "sid": STATE["sid"], "seq": next_seq()}
    m.update(fields)
    return m

def send(sock, obj):
    sock.sendall((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))

def player_state(sock):
    send(sock, envelope("player_state", hp=STATE["hp"], maxHp=STATE["max_hp"],
                        alive=STATE["alive"], item=STATE["item"]))

def handle(sock, msg):
    mtype = msg.get("type")
    if mtype == "hello":
        send(sock, envelope("hello_ack", agent="fake-mc", mcVersion="26.3", playerName="Steve"))
        print("[fake_mc] hello_ack sent")
    elif mtype == "begin_session":
        STATE["sid"] = msg.get("sid", "")
        STATE["active"] = True
        STATE["hp"] = STATE["max_hp"]
        STATE["alive"] = True
        print(f"[fake_mc] session {STATE['sid']} (groundY={msg.get('groundY')}, scale={msg.get('scale')})")
        player_state(sock)
    elif mtype == "end_session":
        print(f"[fake_mc] end_session reason={msg.get('reason')}")
        STATE["active"] = False
        STATE["sid"] = ""
        STATE["actor"] = None
    elif mtype == "player_pose":
        # MC applies the pose; nothing to echo.
        pass
    elif mtype == "actor_upsert":
        STATE["actor"] = (msg.get("eid"), msg.get("x"), msg.get("z"), msg.get("alive", True))
    elif mtype == "actor_remove":
        STATE["actor"] = None
    elif mtype == "attack_input":
        actor = STATE["actor"]
        if actor is None or not STATE["active"] or not STATE["alive"]:
            print("[fake_mc] attack ignored (no actor/inactive/dead)")
            return
        eid, ax, az, alive = actor
        px, pz = msg.get("x"), msg.get("z")
        dist = ((px - ax) ** 2 + (pz - az) ** 2) ** 0.5
        if dist <= 3.0 and alive:
            damage = 5.0
            send(sock, envelope("hit_actor", eventId=str(uuid.uuid4()), attackId=msg.get("attackId", ""),
                                eid=eid, damage=damage, killed=False))
            print(f"[fake_mc] HIT eid={eid} dist={dist:.2f} damage={damage}")
        else:
            print(f"[fake_mc] MISS dist={dist:.2f} (vanilla reach gate)")
    elif mtype == "player_hurt":
        if not STATE["alive"]:
            print("[fake_mc] player_hurt ignored (already dead)")
            return
        damage = float(msg.get("damage", 0))
        STATE["hp"] = max(0.0, STATE["hp"] - damage)
        print(f"[fake_mc] player hurt {damage} -> hp={STATE['hp']}")
        send(sock, envelope("ack", ackEventId=msg.get("eventId", "")))
        if STATE["hp"] <= 0:
            STATE["alive"] = False
            print("[fake_mc] MC PLAYER DIED")
        player_state(sock)
    elif mtype == "ack":
        pass
    elif mtype == "heartbeat":
        pass
    elif mtype == "error":
        print(f"[fake_mc] ERROR {msg.get('code')}: {msg.get('message')}")
    else:
        print(f"[fake_mc] unknown type {mtype}")

def heartbeat(sock):
    while True:
        time.sleep(1.0)
        if STATE["active"]:
            try:
                send(sock, envelope("heartbeat"))
                player_state(sock)
            except OSError:
                return

def main():
    sock = socket.create_connection(("127.0.0.1", PORT), timeout=5)
    sock.settimeout(None)
    print(f"[fake_mc] connected to 127.0.0.1:{PORT}")
    threading.Thread(target=heartbeat, args=(sock,), daemon=True).start()
    buf = b""
    try:
        while True:
            chunk = sock.recv(65536)
            if not chunk:
                print("[fake_mc] server closed")
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if len(line) > 65536:
                    print("[fake_mc] oversize line, exiting")
                    return
                if line.strip():
                    handle(sock, json.loads(line.decode("utf-8")))
    except ConnectionResetError:
        print("[fake_mc] connection reset")
    finally:
        sock.close()

if __name__ == "__main__":
    main()
