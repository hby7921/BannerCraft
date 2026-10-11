#!/usr/bin/env python3
"""fake_bannerlord.py — Bannerlord-side stand-in for the Mycraft demo bridge (protocol/demo-v1.md).

Listens on 127.0.0.1 and plays the BL role: hello, begin_session, 20 Hz player_pose,
actor_upsert, player_hurt injection on keypress, end_session. For testing the Fabric
mod without launching Bannerlord.

Usage: python fake_bannerlord.py [port] [groundY]   (default 38765, -60)
"""
import json
import math
import socket
import sys
import threading
import time
import uuid

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 38765
GROUND_Y = float(sys.argv[2]) if len(sys.argv) > 2 else -60.0
PV = 1
SEQ = [0]
CLIENT = {"sock": None, "lock": threading.Lock(), "sid": "", "alive": True}

def next_seq():
    SEQ[0] += 1
    return SEQ[0]

def envelope(msg_type, **fields):
    m = {"type": msg_type, "pv": PV, "sid": CLIENT["sid"], "seq": next_seq()}
    m.update(fields)
    return m

def send(obj):
    with CLIENT["lock"]:
        sock = CLIENT["sock"]
        if sock is None:
            return
        try:
            sock.sendall((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
        except OSError:
            pass

def pose_loop():
    t = 0.0
    while True:
        time.sleep(0.05)  # 20 Hz
        if CLIENT["sock"] is None or not CLIENT["sid"]:
            continue
        t += 0.05
        x = 100.0 + 5.0 * math.sin(t)
        z = -25.0 + 5.0 * math.cos(t)
        yaw = (t * 57.2958) % 360.0
        send(envelope("player_pose", x=round(x, 3), y=73.0, z=round(z, 3), yaw=round(yaw, 2)))
        send(envelope("actor_upsert", eid=1, x=104.0, y=GROUND_Y, z=-25.0, yaw=180.0,
                      alive=True, name="Fake Bandit"))

def console_loop():
    print("[fake_bl] commands: h = forward player_hurt(6), e = end_session, q = quit")
    while True:
        cmd = input().strip().lower()
        if cmd == "h":
            send(envelope("player_hurt", eventId=str(uuid.uuid4()), damage=6.0, sourceEid=1))
            print("[fake_bl] player_hurt(6) sent")
        elif cmd == "e":
            send(envelope("end_session", reason="mission_end"))
            CLIENT["sid"] = ""
        elif cmd == "q":
            send(envelope("end_session", reason="disconnect"))
            time.sleep(0.2)
            with CLIENT["lock"]:
                if CLIENT["sock"]:
                    CLIENT["sock"].close()
            return

def handle(msg):
    mtype = msg.get("type")
    if mtype == "hello_ack":
        sid = str(uuid.uuid4()).split("-")[0]
        CLIENT["sid"] = sid
        send(envelope("begin_session", mode="demo_field", groundY=GROUND_Y,
                      offsetX=0.0, offsetZ=0.0, scale=1.0))
        print(f"[fake_bl] session {sid} begun")
    elif mtype == "hit_actor":
        print(f"[fake_bl] HIT eid={msg.get('eid')} damage={msg.get('damage')} eventId={msg.get('eventId')}")
        send(envelope("ack", ackEventId=msg.get("eventId", "")))
    elif mtype == "player_state":
        print(f"[fake_bl] MC player hp={msg.get('hp')}/{msg.get('maxHp')} alive={msg.get('alive')} item={msg.get('item')}")
        if msg.get("alive") is False and CLIENT["alive"]:
            CLIENT["alive"] = False
            print("[fake_bl] MC death -> BL would trigger player death once")
            send(envelope("end_session", reason="death"))
            CLIENT["sid"] = ""
    elif mtype == "ack":
        pass
    elif mtype == "heartbeat":
        pass
    elif mtype == "error":
        print(f"[fake_bl] ERROR {msg.get('code')}: {msg.get('message')}")
    else:
        print(f"[fake_bl] note: {mtype}")

def handle_client(sock):
    with CLIENT["lock"]:
        if CLIENT["sock"] is not None:
            print("[fake_bl] rejecting second client")
            sock.close()
            return
        CLIENT["sock"] = sock
    print("[fake_bl] MC client connected")
    send(envelope("hello", agent="fake-bannerlord", gameVersion="v1.5.4.123627"))
    buf = b""
    try:
        while True:
            chunk = sock.recv(65536)
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if line.strip():
                    handle(json.loads(line.decode("utf-8")))
    except (ConnectionResetError, OSError):
        pass
    print("[fake_bl] client disconnected")
    with CLIENT["lock"]:
        CLIENT["sock"] = None
        CLIENT["sid"] = ""
        CLIENT["alive"] = True

def main():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", PORT))
    server.listen(1)
    print(f"[fake_bl] listening on 127.0.0.1:{PORT} (groundY={GROUND_Y})")
    threading.Thread(target=pose_loop, daemon=True).start()
    threading.Thread(target=console_loop, daemon=True).start()
    while True:
        sock, addr = server.accept()
        threading.Thread(target=handle_client, args=(sock,), daemon=True).start()

if __name__ == "__main__":
    main()
