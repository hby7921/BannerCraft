#!/usr/bin/env python3
"""Synthetic MC authority for the isolated, real native 201 vs 3 army test.

This is not a vanilla Minecraft combat simulation. Native Fian arrows must enter
the production HandleBlow path before this fixture lowers its HP. The reverse
direction intentionally sends synthetic receipts to test native hit/casualty
statistics, retry suppression and victory through the original troop supplier.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import time
import uuid


def read_object(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else None
    except (OSError, ValueError):
        return None


def expected_close(log_path, control):
    try:
        log = log_path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return False
    return (control.get("phase") == "finish" and "MC_ARMY_SMOKE_NATIVE_VICTORY_PASS" in log
            and "MC_ARMY_SMOKE_FAIL" not in log and "demo ended: mission_end" in log)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=38780)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args()
    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        control = read_object(args.control)
        if control and control.get("phase") == "connect":
            break
        time.sleep(0.1)
    else:
        raise TimeoutError("Original native army control did not become ready")
    run_id = control["runId"]
    fian_ids = {int(control[f"fian{i}Eid"]) for i in range(int(control["fianCount"]))}
    expected_slots = {int(control[f"slot{i}"]) for i in range(3)}
    if len(fian_ids) != 200 or len(expected_slots) != 3:
        raise RuntimeError("Native fixture is not exactly 200 distinct Fians and 3 army slots")
    result = dict(runId=run_id, failed=False, actorLimit=0, actorCount=0, spawnRequests=0,
                  nativeHurtCount=0, nativeBowHurtCount=0, nativeBowSource=0,
                  nativeBowDamage=0.0, hurtDuplicateCount=0, lowestMobHp=300.0,
                  returnHitSends=0, returnHitAck=False, soldierDeathSends=0,
                  soldierDeathAck=False, spawnedMobCount=0, deadMobCount=0,
                  completed=False, selectedSlot=0, slotSelections=0, inventoryRevision=1)

    def publish():
        temporary = args.result.with_suffix(".tmp")
        try:
            temporary.write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
            temporary.replace(args.result)
        except PermissionError:
            pass  # Native read handles can briefly overlap an atomic replacement.

    seq, sid, tick = 0, "", 0
    ready = False
    selected_slot, inventory_revision = 0, 1
    inventory_requested = False
    actors, mobs, receipts = {}, [], {}
    request_fingerprint = None
    first_hurt_event = None
    result_event, result_ack, next_result = "", False, 0.0
    active_phase = ""
    last_state = last_heartbeat = 0.0
    reverse_events = {name: str(uuid.uuid4()) for name in ("return_hit", "kill_soldier")}
    next_reverse = 0.0
    fixtures = Path(__file__).resolve().parents[2] / "tests" / "GenericMobVisuals" / "Fixtures"
    samples = []
    for sample in range(3):
        asset = (fixtures / f"wither_{sample}.asset").read_bytes()
        pose = (fixtures / f"wither_{sample}.pose").read_bytes()
        if asset[:4] != b"EMA1" or pose[:4] != b"EMP1":
            raise RuntimeError("Actual Wither native renderer fixture header differs")
        samples.append((hashlib.sha256(asset).hexdigest()[:32], asset, pose))

    def snapshot_directory():
        if len(sid) != 32 or any(char not in "0123456789abcdef" for char in sid):
            raise RuntimeError("Invalid session model directory identity")
        directory = Path(os.environ["LOCALAPPDATA"]) / "Mycraft" / "entity-models" / sid
        directory.mkdir(parents=True, exist_ok=True)
        return directory

    def write_snapshots():
        directory = snapshot_directory()
        appearance, asset, original = samples[(tick // 20) % len(samples)]
        asset_path = directory / (appearance + ".asset")
        if not asset_path.exists():
            temporary = directory / (appearance + ".asset.tmp")
            temporary.write_bytes(asset)
            temporary.replace(asset_path)
        for mob in mobs:
            pose = bytearray(original)
            pose[4:36] = appearance.encode("ascii")
            struct.pack_into("<q", pose, 36, tick + 1)
            destination = directory / (mob["visualKey"] + ".pose")
            temporary = directory / (mob["visualKey"] + ".pose.tmp")
            try:
                temporary.write_bytes(pose)
                temporary.replace(destination)
            except PermissionError:
                pass

    publish()
    try:
        with socket.create_connection(("127.0.0.1", args.port), timeout=5) as sock:
            sock.settimeout(0.03)

            def send(msg_type, **fields):
                nonlocal seq
                seq += 1
                message = dict(type=msg_type, pv=1, sid=sid, seq=seq)
                message.update(fields)
                sock.sendall((json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8"))

            def player_state():
                state = dict(hp=20.0, maxHp=20.0, alive=True, item="minecraft:air",
                             selectedSlot=selected_slot, food=20, armor=0, offhandItem="minecraft:air", offhandCount=0,
                             weaponKind="none", offhandWeaponKind="none", useTicks=0, crossbowCharge=0.0,
                             crossbowLoaded=False, crossbowFirework=False, offhandCrossbowLoaded=False,
                             offhandCrossbowFirework=False, usingItem=False, usingShield=False,
                             blocking=False, usedHand="none", containerOpen=False, mainHandBlock=False,
                             offHandBlock=False, eyeHeight=1.62)
                for index in range(9):
                    state[f"slot{index}Item"] = "minecraft:air"
                    state[f"slot{index}Count"] = 0
                send("player_state", **state)

            def inventory_state():
                state = dict(rev=inventory_revision, creative=False, selectedSlot=selected_slot)
                for index in range(41):
                    prefix = f"inv{index}" if index < 36 else f"armor{index - 36}" if index < 40 else "offhand"
                    state[prefix + "Item"] = "minecraft:air"
                    state[prefix + "Count"] = 0
                send("inventory_state", **state)

            def mob_states():
                write_snapshots()
                for mob in mobs:
                    send("mob_state", entityId=mob["entityId"], kind="wither", armySlot=mob["slot"],
                         armyPlayerSide=False, visualKey=mob["visualKey"],
                         x=mob["x"], y=mob["y"], z=mob["z"],
                         yaw=0.0, bodyYaw=0.0, headYaw=0.0, pitch=0.0, vx=0.0, vy=0.0, vz=0.0,
                         hp=mob["hp"] if mob["alive"] else 0.0, maxHp=300.0, alive=mob["alive"],
                         width=.9, height=3.5, hostile=True, baby=False, boss=True, bossName="Wither",
                         invulnerableTicks=0, nativeTick=tick, targetEid=0, mainItem="minecraft:air",
                         aggressive=True, walkPosition=0.0, walkSpeed=0.0, attackProgress=0.0,
                         hurtTime=4 if mob["hp"] < 300 and mob["alive"] else 0,
                         deathTime=0 if mob["alive"] else 20)

            print("MC_ARMY_PEER_CONNECTED: synthetic authority; actual native Fian bow receipts required", flush=True)
            buffer = b""
            while time.monotonic() < deadline:
                now = time.monotonic()
                updated = read_object(args.control)
                if updated and updated.get("runId") == run_id:
                    control = updated
                phase = control["phase"]
                if phase != active_phase:
                    active_phase = phase
                    next_reverse = now
                    print("MC_ARMY_PEER_PHASE: " + phase, flush=True)
                    if phase.startswith("kill_boss_"):
                        dead_count = int(phase.rsplit("_", 1)[1])
                        if len(mobs) != 3:
                            raise RuntimeError("Native boss death requested before exact army binding")
                        for index, mob in enumerate(mobs):
                            if index < dead_count:
                                mob["alive"], mob["hp"] = False, 0.0
                        result["deadMobCount"] = dead_count
                if ready and now - last_heartbeat >= .5:
                    send("heartbeat")
                    player_state()
                    last_heartbeat = now
                if ready and mobs and now - last_state >= .05:
                    tick += 1
                    mob_states()
                    last_state = now
                if result_event and not result_ack and now >= next_result:
                    send("mc_army_spawn_result", requestId=request_fingerprint["requestId"], eventId=result_event,
                         ok=True, message="synthetic native army fixture: exact three assigned Withers")
                    next_result = now + .5
                if phase in reverse_events and mobs and now >= next_reverse:
                    key = "returnHitSends" if phase == "return_hit" else "soldierDeathSends"
                    if result[key] < 3:
                        target = int(control["targetEid"])
                        if target not in fian_ids or target not in actors and result[key] == 0:
                            raise RuntimeError("Reverse fixture target was not registered original Fian")
                        damage = 7.0 if phase == "return_hit" else float(control["soldierDeathDamage"])
                        send("mob_hit", eventId=reverse_events[phase], sourceId=mobs[0]["entityId"],
                             eid=target, damage=damage, kind="projectile")
                        result[key] += 1
                        next_reverse = now + .3
                result["actorCount"] = len(actors)
                publish()
                try:
                    chunk = sock.recv(65536)
                except socket.timeout:
                    continue
                if not chunk:
                    if expected_close(args.log, control):
                        result["completed"] = True
                        publish()
                        print("MC_ARMY_PEER_EXPECT_CLOSE_PASS: original successful native battle and mission teardown", flush=True)
                        return
                    raise RuntimeError("Unexpected bridge EOF before native army victory/teardown")
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
                        send("hello_ack", agent="mc-army-native-smoke", mcVersion="26.3", playerName="ArmyDiagnostic")
                    elif kind == "begin_session":
                        sid = message["sid"]
                        result["actorLimit"] = int(message.get("actorLimit", 64))
                        if result["actorLimit"] != 256:
                            raise RuntimeError("Army session did not negotiate actorLimit256")
                        send("staging_origin", offsetX=16384, offsetZ=32768)
                        ready = True
                        player_state()
                    elif kind == "actor_upsert":
                        actor_id = int(message["eid"])
                        if actor_id not in fian_ids:
                            raise RuntimeError("Native mirror contains an unexpected human enemy/commander actor")
                        actors[actor_id] = message
                        if len(actors) > 256:
                            raise RuntimeError("Native actor capacity exceeded")
                    elif kind == "actor_remove":
                        actors.pop(int(message["eid"]), None)
                    elif kind == "mc_army_spawn":
                        result["spawnRequests"] += 1
                        payload = {key: value for key, value in message.items() if key not in ("seq", "pv", "sid", "type")}
                        if request_fingerprint is not None:
                            if payload != request_fingerprint:
                                raise RuntimeError("Retried native spawn batch changed payload/identity")
                            result_ack = False
                            next_result = 0.0
                            continue
                        if int(message["count"]) != 3 or message["playerSide"] is not False:
                            raise RuntimeError("Native spawn batch is not three enemy Withers")
                        actual_slots = {int(message[f"mob{i}Slot"]) for i in range(3)}
                        if actual_slots != expected_slots:
                            raise RuntimeError("Native spawn request changed original army slot identities")
                        for index in range(3):
                            if message[f"mob{index}Kind"] != "wither":
                                raise RuntimeError("Native preset requested a creature other than Wither")
                            identity = str(uuid.uuid5(uuid.NAMESPACE_URL, run_id + "/army/" + str(message[f"mob{index}Slot"])))
                            mobs.append(dict(entityId=identity, visualKey=identity.replace("-", ""),
                                             slot=int(message[f"mob{index}Slot"]),
                                             x=float(message[f"mob{index}X"]), y=float(message[f"mob{index}Y"]),
                                             z=float(message[f"mob{index}Z"]), hp=300.0, alive=True))
                        request_fingerprint = payload
                        result_event = str(uuid.uuid5(uuid.NAMESPACE_URL, run_id + "/spawn-result"))
                        result["spawnedMobCount"] = len(mobs)
                        mob_states()  # First publication carries exact slot/allegiance before any ambient proxy can be created.
                    elif kind == "mob_hurt":
                        source = int(message.get("sourceEid", 0))
                        damage = float(message.get("damage", 0))
                        mob = next((mob for mob in mobs if mob["entityId"] == message.get("entityId")), None)
                        if mob is None or source not in fian_ids or source not in actors or damage <= 0:
                            raise RuntimeError("Native creature damage is not a real registered Fian receipt")
                        event = message["eventId"]
                        fingerprint = (mob["entityId"], source, damage, message.get("sourceKind", "melee"))
                        if event in receipts:
                            if receipts[event] != fingerprint:
                                raise RuntimeError("Native damage retry changed payload")
                            result["hurtDuplicateCount"] += 1
                            send("ack", ackEventId=event)
                        else:
                            receipts[event] = fingerprint
                            result["nativeHurtCount"] += 1
                            # Keep this bounded host fixture alive until explicit staged deaths.
                            # Real vanilla HP/armor/invulnerability are covered by native MC tests.
                            if mob["alive"]:
                                mob["hp"] = max(1.0, mob["hp"] - damage)
                                result["lowestMobHp"] = min(result["lowestMobHp"], mob["hp"])
                            if fingerprint[3] == "arrow":
                                result["nativeBowHurtCount"] += 1
                                if not result["nativeBowSource"]:
                                    result["nativeBowSource"] = source
                                    result["nativeBowDamage"] = damage
                                    print(f"MC_ARMY_PEER_NATIVE_BOW: source={source} damage={damage} hp={mob['hp']}", flush=True)
                            if first_hurt_event is None:
                                first_hurt_event = event  # Withhold only this ACK; its real retry must not deduct twice.
                            else:
                                send("ack", ackEventId=event)
                    elif kind == "player_hurt":
                        send("ack", ackEventId=message["eventId"])
                        player_state()
                    elif kind == "ack":
                        event = message["ackEventId"]
                        if event == result_event:
                            result_ack = True
                        elif event == reverse_events["return_hit"]:
                            result["returnHitAck"] = True
                        elif event == reverse_events["kill_soldier"]:
                            result["soldierDeathAck"] = True
                    elif kind == "select_slot":
                        slot = message.get("slot")
                        if type(slot) is not int or not 0 <= slot <= 8:
                            raise RuntimeError("Invalid native hotbar selection request")
                        if slot != selected_slot:
                            selected_slot = slot
                            inventory_revision += 1
                        result["selectedSlot"] = selected_slot
                        result["inventoryRevision"] = inventory_revision
                        result["slotSelections"] += 1
                        # Match real BridgeRuntime: change selection, cancel use,
                        # and immediately publish its authoritative confirmation.
                        player_state()
                        if inventory_requested:
                            inventory_state()
                        print(f"MC_ARMY_PEER_SLOT_CONFIRMED: selected={selected_slot} revision={inventory_revision}", flush=True)
                    elif kind == "inventory_request":
                        inventory_requested = True
                        inventory_state()
                    elif kind in ("inventory_swap", "creative_give"):
                        send("inventory_result", requestId=message["requestId"], ok=False,
                             message="The isolated host fixture has an empty inventory; live Minecraft inventory was not changed.")
                        inventory_state()
                    elif kind in ("interact", "mine"):
                        send("interaction_result", requestId=message["requestId"], ok=False,
                             message="The isolated host fixture has no placeable items.")
                        player_state()
                    elif kind == "use_item":
                        player_state()  # Vanilla air cannot begin item use.
                    elif kind == "end_session":
                        if message.get("reason") != "mission_end" or control.get("phase") != "finish":
                            raise RuntimeError("Native army session ended before successful test: " + repr(message))
                        result["completed"] = True
                        publish()
                        print("MC_ARMY_PEER_END_PASS: original production mission_end after native victory", flush=True)
                        return
                    elif kind == "error":
                        raise RuntimeError("Native protocol error: " + repr(message))
                if len(buffer) > 65536:
                    raise RuntimeError("Unterminated native line exceeded protocol bound")
            raise TimeoutError("Army diagnostic peer exceeded deadline")
    except Exception as error:
        result["failed"], result["error"] = True, str(error)
        publish()
        raise


if __name__ == "__main__":
    main()
