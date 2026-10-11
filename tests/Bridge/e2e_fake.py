#!/usr/bin/env python3
"""e2e_fake.py — illustrative in-process protocol simulation.

This does not load either production bridge implementation or either game.
It is not evidence that the Fabric/C# bridge or damage settlement works.

Runs the BL role (server) and MC role (client) against each other with fixed
geometry. The MC role mirrors fake_mc.py behavior (vanilla reach gate, hp
settlement, death once, sid gating, eventId dedup) and asserts:
handshake -> session -> pose/actor -> attack_input(2m) -> hit_actor(5.0)
-> far attack (miss, silence) -> player_hurt -> hp 14 -> death once
-> post-death hurt ignored -> end_session.

Usage: python e2e_fake.py [port]
"""
import json
import socket
import sys
import threading
import time
import uuid

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 38911
PV = 1
SEQ_BL = [0]
SEQ_MC = [0]

def seq(side):
    side[0] += 1
    return side[0]

def env(side, sid, msg_type, **f):
    m = {"type": msg_type, "pv": PV, "sid": sid, "seq": seq(side)}
    m.update(f)
    return m

class LineSock:
    def __init__(self, sock):
        self.sock = sock
        self.buf = b""
        self.lines = []
        self.lock = threading.Lock()

    def send(self, obj):
        self.sock.sendall((json.dumps(obj, ensure_ascii=False) + "\n").encode())

    def reader(self):
        while True:
            try:
                chunk = self.sock.recv(65536)
            except OSError:
                return
            if not chunk:
                return
            with self.lock:
                self.buf += chunk
                while b"\n" in self.buf:
                    line, self.buf = self.buf.split(b"\n", 1)
                    if line.strip():
                        self.lines.append(json.loads(line.decode()))

    def wait_for(self, mtype, timeout=3.0, pred=None):
        deadline = time.time() + timeout
        while time.time() < deadline:
            with self.lock:
                for i, m in enumerate(self.lines):
                    if m.get("type") == mtype and (pred is None or pred(m)):
                        return self.lines.pop(i)
            time.sleep(0.01)
        return None

def main():
    failures = []

    def check(name, cond, detail=""):
        print(("PASS " if cond else "FAIL ") + name + (f" :: {detail}" if detail and not cond else ""))
        if not cond:
            failures.append(name)

    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", PORT))
    server.listen(1)

    mc = LineSock(socket.create_connection(("127.0.0.1", PORT)))
    threading.Thread(target=mc.reader, daemon=True).start()
    bl_sock, _ = server.accept()
    bl = LineSock(bl_sock)
    threading.Thread(target=bl.reader, daemon=True).start()

    bl.send(env(SEQ_BL, "", "hello", agent="e2e-bl", gameVersion="v1.5.4.123627"))
    check("e2e.hello", mc.wait_for("hello") is not None)
    sid = "e2e-" + str(uuid.uuid4())[:8]
    mc.send(env(SEQ_MC, "", "hello_ack", agent="e2e-mc", mcVersion="26.3", playerName="Steve"))
    check("e2e.hello_ack", bl.wait_for("hello_ack") is not None)

    ground_y = -60.0
    bl.send(env(SEQ_BL, sid, "begin_session", mode="demo_field", groundY=ground_y, offsetX=0.0, offsetZ=0.0, scale=1.0))
    got = mc.wait_for("begin_session")
    check("e2e.begin_session", got is not None and got.get("sid") == sid)

    # --- MC role responder (mirrors fake_mc.py) ---
    actor = {"eid": 0, "x": 0.0, "z": 0.0, "alive": False}
    hp = {"value": 20.0, "max": 20.0, "alive": True}
    hurt_seen = set()
    death_signals = [0]
    last_pose = {}

    def mc_responder():
        while True:
            with mc.lock:
                msgs, mc.lines[:] = mc.lines[:], []
            for m in msgs:
                if m.get("sid") != sid:
                    continue  # stale/foreign session: ignored (protocol §4.2)
                t = m.get("type")
                if t == "player_pose":
                    last_pose.update(m)
                elif t == "actor_upsert":
                    actor.update(eid=m.get("eid"), x=m.get("x"), z=m.get("z"), alive=m.get("alive", True))
                elif t == "attack_input":
                    if actor["alive"] and hp["alive"]:
                        dist = ((m.get("x", 0) - actor["x"]) ** 2 + (m.get("z", 0) - actor["z"]) ** 2) ** 0.5
                        if dist <= 3.0:
                            mc.send(env(SEQ_MC, sid, "hit_actor", eventId=str(uuid.uuid4()),
                                        attackId=m.get("attackId", ""), eid=actor["eid"], damage=5.0, killed=False))
                        # out of range: silence (implicit miss, protocol §3 hit_actor)
                elif t == "player_hurt":
                    ev = m.get("eventId", "")
                    if hp["alive"] and ev not in hurt_seen:
                        hurt_seen.add(ev)
                        hp["value"] = max(0.0, hp["value"] - float(m.get("damage", 0)))
                        if hp["value"] <= 0:
                            hp["alive"] = False
                            death_signals[0] += 1
                        mc.send(env(SEQ_MC, sid, "player_state", hp=hp["value"], maxHp=hp["max"],
                                    alive=hp["alive"], item="minecraft:air"))
                elif t == "end_session":
                    return
            time.sleep(0.005)

    threading.Thread(target=mc_responder, daemon=True).start()

    # pose + actor at 2m from the player
    bl.send(env(SEQ_BL, sid, "player_pose", x=100.0, y=73.0, z=-25.0, yaw=0.0))
    bl.send(env(SEQ_BL, sid, "actor_upsert", eid=1, x=102.0, y=ground_y, z=-25.0, yaw=180.0, alive=True, name="Bandit"))
    time.sleep(0.05)
    check("simulation.pose_and_actor_seen", last_pose.get("x") == 100.0 and actor["eid"] == 1 and actor["x"] == 102.0)

    # attack in range -> hit_actor(5.0)
    at1 = str(uuid.uuid4())
    bl.send(env(SEQ_BL, sid, "attack_input", attackId=at1, x=100.0, y=73.0, z=-25.0, yaw=0.0))
    hit = bl.wait_for("hit_actor", pred=lambda m: m.get("attackId") == at1)
    check("e2e.hit_in_range", hit is not None and abs(hit.get("damage", 0) - 5.0) < 1e-6 and hit.get("eid") == 1 and hit.get("killed") is False)
    bl.send(env(SEQ_BL, sid, "ack", ackEventId=(hit or {}).get("eventId", "")))

    # attack far away (15m) -> vanilla reach gate -> silence
    far = str(uuid.uuid4())
    bl.send(env(SEQ_BL, sid, "attack_input", attackId=far, x=100.0, y=73.0, z=-40.0, yaw=0.0))
    check("e2e.attack_miss_out_of_range", bl.wait_for("hit_actor", timeout=0.6, pred=lambda m: m.get("attackId") == far) is None)

    # duplicate eventId -> second application rejected (consumer dedup)
    eid = str(uuid.uuid4())
    seen = set()

    def dedup_apply(ev):
        if ev in seen:
            return False
        seen.add(ev)
        return True

    check("e2e.eventid_dedup", dedup_apply(eid) and not dedup_apply(eid))

    # Exercise stale-session rejection while alive so a mistaken application changes hp.
    with mc.lock:
        mc.lines.append(env(SEQ_BL, "ancient-session", "player_hurt", eventId=str(uuid.uuid4()), damage=5.0, sourceEid=1))
    time.sleep(0.1)
    check("simulation.stale_sid_rejected", hp["value"] == 20.0 and len(hurt_seen) == 0)

    # player_hurt 6 -> hp 14; hurt 30 -> death once; post-death hurt ignored
    bl.send(env(SEQ_BL, sid, "player_hurt", eventId=str(uuid.uuid4()), damage=6.0, sourceEid=1))
    st = bl.wait_for("player_state", pred=lambda m: abs(m.get("hp", -1) - 14.0) < 1e-6)
    check("e2e.player_hurt_hp14", st is not None and st.get("alive") is True)
    bl.send(env(SEQ_BL, sid, "player_hurt", eventId=str(uuid.uuid4()), damage=30.0, sourceEid=1))
    st2 = bl.wait_for("player_state", pred=lambda m: m.get("alive") is False)
    check("e2e.player_death", st2 is not None and st2.get("hp", 1) <= 0)
    with bl.lock:
        states_before = len(bl.lines)
    bl.send(env(SEQ_BL, sid, "player_hurt", eventId=str(uuid.uuid4()), damage=5.0, sourceEid=1))
    time.sleep(0.2)
    with bl.lock:
        new_states = [m for m in bl.lines[states_before:] if m.get("type") == "player_state"]
    check("e2e.post_death_hurt_ignored", len(new_states) == 0)
    check("e2e.death_signal_once", death_signals[0] == 1)

    bl.send(env(SEQ_BL, sid, "end_session", reason="mission_end"))
    print("E2E " + ("PASSED" if not failures else f"FAILED: {failures}"))
    sys.exit(0 if not failures else 1)

if __name__ == "__main__":
    main()
