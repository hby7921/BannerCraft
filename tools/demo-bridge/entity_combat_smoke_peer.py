#!/usr/bin/env python3
"""Synthetic entity authority for actual isolated Bannerlord AI/lifecycle tests.

No Minecraft client is opened. Only actual socket mob_hurt events lower this
fixture's health; the opposite direction is an explicit synthetic damage receipt.
"""
import argparse
import json
from pathlib import Path
import socket
import time
import uuid
import os
import struct

WOODS = ("oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "pale_oak", "poplar", "bamboo")
KINDS = [wood + ("_raft" if wood == "bamboo" else "_boat") for wood in WOODS]
KINDS += [wood + ("_chest_raft" if wood == "bamboo" else "_chest_boat") for wood in WOODS]


def read_object(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else None
    except (OSError, ValueError):
        return None


def expected_close(log_path, sid):
    deadline = time.monotonic() + 1.0
    while True:
        try:
            log = log_path.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            log = ""
        if "ENTITY_NATIVE_SMOKE_FAIL" in log:
            return False
        begins = [line.split("demo session begun: ", 1)[1].strip() for line in log.splitlines() if "demo session begun: " in line]
        if sid and begins and begins[-1] == sid:
            tail = log.rsplit("demo session begun: " + sid, 1)[1]
            if any(line.endswith("demo ended: entity_scene_smoke") for line in tail.splitlines()):
                return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.05)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=38779)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args()
    deadline = time.monotonic() + args.timeout
    control = None
    while time.monotonic() < deadline:
        control = read_object(args.control)
        if control and control.get("phase") == "connect":
            break
        time.sleep(0.1)
    else:
        raise TimeoutError("Native entity control did not become ready")
    run_id = control["runId"]
    result = dict(runId=run_id, failed=False, nativeHurtCount=0, nativeHurtSource=0,
                  nativeHurtDamage=0.0, mobHp=20.0, returnHitSends=0,
                  returnHitAck=False, boatBatch=0, boatRemoveAcks=0,
                  mobRemoveAck=False, retiredReplaySends=0)

    def publish():
        # Flat JSON is also readable by the production C# bridge codec.
        temporary = args.result.with_suffix(".tmp")
        try:
            temporary.write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
            temporary.replace(args.result)
        except PermissionError:
            # A native reader can briefly hold a Windows file handle; next loop
            # retries the same receipt rather than changing gameplay evidence.
            pass

    seq, sid, tick = 0, "", 0
    session_ready = False
    selected = 0
    actors = {}
    hurt_events = set()
    pending = {}
    remove_started = set()
    boat_ids = [str(uuid.uuid5(uuid.NAMESPACE_URL, run_id + "/boat/" + kind)) for kind in KINDS]
    displayed = []
    active_phase = ""
    phase_at = 0.0
    hit_id = str(uuid.uuid4())
    next_hit = 0.0
    next_replay = 0.0
    last_state = last_heartbeat = 0.0
    mob_id = control["mobId"]
    auxiliary = [(str(uuid.uuid5(uuid.NAMESPACE_URL, run_id + "/" + name)), name) for name in ("cow", "wither")]
    copied_snapshots = False
    publish()
    try:
        with socket.create_connection(("127.0.0.1", args.port), timeout=5) as sock:
            sock.settimeout(0.05)

            def send(msg_type, **fields):
                nonlocal seq
                seq += 1
                message = dict(type=msg_type, pv=1, sid=sid, seq=seq)
                message.update(fields)
                sock.sendall((json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8"))

            def player_state():
                values = dict(hp=20.0, maxHp=20.0, alive=True, item="minecraft:diamond_sword" if selected == 0 else "minecraft:air",
                              selectedSlot=selected, food=20, armor=0, offhandItem="minecraft:air", offhandCount=0,
                              weaponKind="melee" if selected == 0 else "none", offhandWeaponKind="none",
                              useTicks=0, crossbowCharge=0.0, crossbowLoaded=False, crossbowFirework=False,
                              offhandCrossbowLoaded=False, offhandCrossbowFirework=False, usingItem=False,
                              usingShield=False, blocking=False, usedHand="none", containerOpen=False,
                              mainHandBlock=False, offHandBlock=False, eyeHeight=1.62)
                for slot in range(9):
                    values[f"slot{slot}Item"] = "minecraft:diamond_sword" if slot == 0 else "minecraft:air"
                    values[f"slot{slot}Count"] = 1 if slot == 0 else 0
                send("player_state", **values)

            def mob_state(alive=True, death_time=0):
                send("mob_state", entityId=mob_id, kind="wither_skeleton",
                     x=control["mobX"], y=control["mobY"], z=control["mobZ"],
                     yaw=0.0, bodyYaw=0.0, headYaw=0.0, pitch=0.0, vx=0.0, vy=0.0, vz=0.0,
                     hp=result["mobHp"] if alive else 0.0, maxHp=20.0, alive=alive, nativeTick=tick,
                     targetEid=int(control["targetEid"]), mainItem="minecraft:stone_sword", aggressive=True,
                     walkPosition=tick * 0.03, walkSpeed=0.0, attackProgress=0.0,
                     hurtTime=4 if result["nativeHurtCount"] else 0, deathTime=death_time)

            def generic_states():
                nonlocal copied_snapshots
                if not copied_snapshots:
                    directory = Path(os.environ["LOCALAPPDATA"]) / "Mycraft" / "entity-models" / sid
                    directory.mkdir(parents=True, exist_ok=True)
                    fixtures = Path(__file__).resolve().parents[2] / "tests" / "GenericMobVisuals" / "Fixtures"
                    for entity_id, name in auxiliary:
                        appearance = ("1" if name == "cow" else "2") * 32
                        pose = bytearray((fixtures / (name + "_0.pose")).read_bytes())
                        pose[4:36] = appearance.encode("ascii")
                        (directory / (appearance + ".asset")).write_bytes((fixtures / (name + "_0.asset")).read_bytes())
                        (directory / (entity_id.replace("-", "") + ".pose")).write_bytes(pose)
                    copied_snapshots = True
                for entity_id, name in auxiliary:
                    show_boss = name == "wither" and control.get("phase") == "show_boss"
                    send("mob_state", entityId=entity_id, kind=name,
                         x=control["bossX"] if show_boss else control["mobX"] + 70,
                         y=control["bossY"] if show_boss else control["mobY"],
                         z=control["bossZ"] if show_boss else control["mobZ"] + (10 if name == "cow" else 20),
                         yaw=0.0, bodyYaw=0.0, headYaw=0.0, pitch=0.0, vx=0.0, vy=0.0, vz=0.0,
                         hp=10.0 if name == "cow" else 150.0, maxHp=10.0 if name == "cow" else 300.0,
                         width=.9, height=1.4 if name == "cow" else 3.5,
                         hostile=name=="wither", baby=False, boss=name=="wither", bossName="Wither" if name=="wither" else "",
                         visualKey=entity_id.replace("-", ""), alive=True, nativeTick=tick, targetEid=0,
                         mainItem="minecraft:air", aggressive=name=="wither", walkPosition=0.0,walkSpeed=0.0,attackProgress=0.0,hurtTime=0,deathTime=0)

            def boat_state(index):
                local = index % 11
                send("boat_state", entityId=boat_ids[index], kind=KINDS[index],
                     x=control["boatX"] + (local % 4 - 1.5) * 3.0,
                     y=control["boatY"], z=control["boatZ"] + (local // 4) * 3.0,
                     yaw=20.0 * local, pitch=0.0, nativeTick=tick, alive=True,
                     leftPaddle=tick * 0.1, rightPaddle=tick * 0.1 + 0.2,
                     hurtTime=0.0, damage=0.0, bubbleAngle=0.0, hurtDir=1, underWater=False)

            def start_remove(entity_id, kind):
                key = (kind, entity_id)
                if key in remove_started:
                    return
                remove_started.add(key)
                event = str(uuid.uuid4())
                pending[event] = dict(kind=kind, entityId=entity_id, due=0.0, attempts=0)

            print("ENTITY_PEER_CONNECTED: synthetic authority; actual native mob_hurt required", flush=True)
            buffer = b""
            while time.monotonic() < deadline:
                now = time.monotonic()
                updated = read_object(args.control)
                if updated and updated.get("runId") == run_id:
                    control = updated
                phase = control["phase"]
                if phase != active_phase:
                    active_phase, phase_at = phase, now
                    print("ENTITY_PEER_PHASE: " + phase, flush=True)
                    if phase == "spawn":
                        displayed = list(range(11))
                    elif phase == "return_hit":
                        next_hit = now
                    elif phase == "boats_two":
                        displayed = []
                        for index in range(11):
                            start_remove(boat_ids[index], "boat_remove")
                    elif phase == "kill_remove":
                        displayed = []
                        for entity_id, _ in auxiliary:
                            start_remove(entity_id, "mob_remove")
                        for index in range(11, 22):
                            start_remove(boat_ids[index], "boat_remove")
                        next_replay = now + 2.0
                if session_ready and now - last_heartbeat >= 0.5:
                    send("heartbeat"); player_state(); last_heartbeat = now
                if session_ready and phase not in ("connect", "") and now - last_state >= 0.05:
                    tick += 1
                    if phase != "kill_remove":
                        generic_states()
                        mob_state()
                    elif now - phase_at < 1.2:
                        mob_state(False, min(20, int((now - phase_at) * 20)))
                    for index in displayed:
                        boat_state(index)
                    if displayed:
                        result["boatBatch"] = 1 if displayed[0] == 0 else 2
                    last_state = now
                if phase == "return_hit" and result["returnHitSends"] < 3 and now >= next_hit:
                    eid = int(control["targetEid"])
                    if eid in actors and result["nativeHurtCount"] > 0:
                        send("mob_hit", eventId=hit_id, sourceId=mob_id, eid=eid, damage=7.0, kind="melee")
                        result["returnHitSends"] += 1
                        next_hit = now + 0.3
                if phase == "boats_two" and result["boatRemoveAcks"] >= 11 and not displayed:
                    displayed = list(range(11, 22))
                if phase == "kill_remove":
                    if now - phase_at >= 1.2:
                        start_remove(mob_id, "mob_remove")
                    if result["mobRemoveAck"] and result["boatRemoveAcks"] >= 22 and result["retiredReplaySends"] < 3 and now >= next_replay:
                        tick += 1
                        saved_hp = result["mobHp"]; result["mobHp"] = 20.0
                        mob_state()
                        result["mobHp"] = saved_hp
                        for index in range(22):
                            boat_state(index)
                        result["retiredReplaySends"] += 1
                        next_replay = now + 0.3
                for event, removal in list(pending.items()):
                    if now < removal["due"]:
                        continue
                    if removal["attempts"] >= 4:
                        raise TimeoutError("Native removal ACK exhausted: " + removal["entityId"])
                    send(removal["kind"], eventId=event, entityId=removal["entityId"], reason="entity_scene_smoke")
                    removal["attempts"] += 1; removal["due"] = now + 0.5
                publish()
                try:
                    chunk = sock.recv(16384)
                except socket.timeout:
                    continue
                if not chunk:
                    if expected_close(args.log, sid):
                        print("ENTITY_PEER_EXPECT_CLOSE_PASS: native exact-session entity_scene_smoke teardown", flush=True)
                        return
                    raise RuntimeError("Unexpected native bridge EOF before successful entity teardown")
                buffer += chunk
                while b"\n" in buffer:
                    line, buffer = buffer.split(b"\n", 1)
                    if len(line) > 65536:
                        raise RuntimeError("Native line exceeded protocol bound")
                    if not line.strip():
                        continue
                    message = json.loads(line)
                    kind = message.get("type")
                    if kind == "hello":
                        send("hello_ack", agent="entity-scene-smoke", mcVersion="26.3", playerName="EntityLifecycleDiagnostic")
                    elif kind == "begin_session":
                        sid = message["sid"]
                        send("staging_origin", offsetX=16384, offsetZ=32768)
                        session_ready = True; player_state()
                    elif kind == "actor_upsert":
                        actors[int(message["eid"])] = message
                    elif kind == "actor_remove":
                        actors.pop(int(message["eid"]), None)
                    elif kind == "player_hurt":
                        send("ack", ackEventId=message["eventId"]); player_state()
                    elif kind == "mob_hurt":
                        if message.get("entityId") in {identity for identity, _ in auxiliary}:
                            send("ack", ackEventId=message["eventId"])
                            continue
                        if message.get("entityId") != mob_id or int(message.get("sourceEid", 0)) <= 0 or float(message.get("damage", 0)) <= 0:
                            raise RuntimeError("Invalid actual native mob_hurt receipt")
                        event = message["eventId"]
                        if event not in hurt_events:
                            hurt_events.add(event)
                            result["nativeHurtCount"] += 1
                            if result["nativeHurtCount"] == 1:
                                result["nativeHurtSource"] = int(message["sourceEid"])
                                result["nativeHurtDamage"] = float(message["damage"])
                            # This is a bounded host-only fixture, not vanilla MC HP.
                            # Keep the proxy alive for the opposite-direction check.
                            result["mobHp"] = max(1.0, result["mobHp"] - float(message["damage"]))
                            print(f"ENTITY_PEER_NATIVE_HURT: source={message['sourceEid']} damage={message['damage']} hp={result['mobHp']}", flush=True)
                        send("ack", ackEventId=event)
                    elif kind == "ack":
                        event = message["ackEventId"]
                        if event == hit_id:
                            result["returnHitAck"] = True
                        removal = pending.pop(event, None)
                        if removal:
                            if removal["kind"] == "boat_remove":
                                result["boatRemoveAcks"] += 1
                            else:
                                result["mobRemoveAck"] = True
                    elif kind == "select_slot":
                        selected = int(message["slot"]); player_state()
                    elif kind == "end_session":
                        if message.get("reason") != "entity_scene_smoke":
                            raise RuntimeError("Unexpected native entity session end reason")
                        print("ENTITY_PEER_END_PASS: requested production entity teardown", flush=True)
                        return
                    elif kind == "error":
                        raise RuntimeError("Native protocol error: " + repr(message))
                if len(buffer) > 65536:
                    raise RuntimeError("Unterminated native line exceeded protocol bound")
            raise TimeoutError("Entity peer exceeded deadline")
    except Exception as error:
        result["failed"] = True; result["error"] = str(error)
        publish()
        raise


if __name__ == "__main__":
    main()
