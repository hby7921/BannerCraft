#!/usr/bin/env python3
"""Bounded loopback authority for native command-only ownership diagnostics.

This deliberately synthetic peer proves host lifecycle, not Minecraft gameplay.
It waits for the native preconnection assertions before opening the TCP socket.
"""
import argparse
import json
from pathlib import Path
import socket
import time


def expected_native_close(log_path, session_id, timeout=1.0):
    """Allow EOF only when this exact session has the requested native teardown.

    Bridge Dispose may close the socket before its queued end_session is written.
    Read the fresh run's native receipt for at most one second; never treat an
    arbitrary disconnect, stale session receipt, or failed smoke as success.
    """
    if not session_id:
        return False
    deadline = time.monotonic() + min(1.0, max(0.0, timeout))
    while True:
        try:
            log = log_path.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            log = ""
        if "COMMAND_ONLY_SMOKE_FAIL" in log:
            return False
        sessions = [line.split("demo session begun: ", 1)[1].strip()
                    for line in log.splitlines() if "demo session begun: " in line]
        if sessions and sessions[-1] == session_id:
            tail = log.rsplit("demo session begun: " + session_id, 1)[1]
            if any(line.strip().endswith("demo ended: command_only_smoke")
                   for line in tail.splitlines()):
                return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=38769)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args()
    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        if args.log.exists():
            log = args.log.read_text(encoding="utf-8-sig", errors="replace")
            if "COMMAND_ONLY_SMOKE_FAIL" in log:
                raise RuntimeError("Native diagnostic failed before connection")
            if "COMMAND_ONLY_PRECONNECT_PASS" in log:
                break
        time.sleep(0.1)
    else:
        raise TimeoutError("Native PRECONNECT_PASS not observed within deadline")

    seq = 0
    sid = ""
    state_ready = False
    selected = 0

    with socket.create_connection(("127.0.0.1", args.port), timeout=5) as sock:
        sock.settimeout(0.2)

        def send(kind, **fields):
            nonlocal seq
            seq += 1
            message = dict(type=kind, pv=1, sid=sid, seq=seq)
            message.update(fields)
            sock.sendall((json.dumps(message, separators=(",", ":")) + "\n").encode("utf-8"))

        def state():
            message = dict(hp=20.0, maxHp=20.0, alive=True,
                           item="minecraft:diamond_sword" if selected == 0 else "minecraft:air",
                           selectedSlot=selected, food=20, armor=0,
                           offhandItem="minecraft:air", offhandCount=0,
                           weaponKind="melee" if selected == 0 else "none",
                           offhandWeaponKind="none", useTicks=0,
                           crossbowCharge=0.0, crossbowLoaded=False, crossbowFirework=False,
                           offhandCrossbowLoaded=False, offhandCrossbowFirework=False,
                           usingItem=False, usingShield=False, blocking=False,
                           usedHand="none", containerOpen=False,
                           mainHandBlock=False, offHandBlock=False, eyeHeight=1.62)
            for slot in range(9):
                message[f"slot{slot}Item"] = "minecraft:diamond_sword" if slot == 0 else "minecraft:air"
                message[f"slot{slot}Count"] = 1 if slot == 0 else 0
            send("player_state", **message)

        print(f"COMMAND_ONLY_PEER_CONNECTED: 127.0.0.1:{args.port}; synthetic authority", flush=True)
        buffer = b""
        next_heartbeat = time.monotonic()
        while time.monotonic() < deadline:
            if state_ready and time.monotonic() >= next_heartbeat:
                send("heartbeat")
                state()
                next_heartbeat = time.monotonic() + 0.5
            try:
                chunk = sock.recv(16384)
            except socket.timeout:
                continue
            if not chunk:
                if expected_native_close(args.log, sid):
                    print("COMMAND_ONLY_PEER_EXPECT_CLOSE_PASS: expected EOF; fresh native receipt confirms exact command_only_smoke teardown", flush=True)
                    return
                raise RuntimeError("Connection closed without end_session")
            buffer += chunk
            if len(buffer) > 65536:
                raise RuntimeError("Native peer line buffer exceeded protocol bound")
            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                if not line.strip():
                    continue
                message = json.loads(line)
                kind = message.get("type")
                if kind == "hello":
                    send("hello_ack", agent="command-only-smoke", mcVersion="26.3", playerName="LifecycleDiagnostic")
                elif kind == "begin_session":
                    sid = message["sid"]
                    send("staging_origin", offsetX=16384, offsetZ=32768)
                    state_ready = True
                    state()
                    print("COMMAND_ONLY_PEER_READY: valid staging origin and nine-slot melee player state", flush=True)
                elif kind == "player_hurt":
                    # Keep this isolated lifecycle fixture alive; never invent a hit.
                    send("ack", ackEventId=message["eventId"])
                    state()
                elif kind == "select_slot":
                    selected = int(message["slot"])
                    state()
                elif kind == "end_session":
                    if message.get("reason") != "command_only_smoke":
                        raise RuntimeError("Unexpected native teardown reason: " + str(message.get("reason")))
                    print("COMMAND_ONLY_PEER_END_PASS: requested production session teardown", flush=True)
                    return
                elif kind == "error":
                    raise RuntimeError("Native protocol error: " + repr(message))
        raise TimeoutError("Native lifecycle session exceeded deadline")


if __name__ == "__main__":
    main()
