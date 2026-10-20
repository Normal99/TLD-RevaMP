#!/usr/bin/env python3
"""Replay local numeric repro JSON through two already running, isolated windowed player copies. Never launches a game.

  python tools/replay_repro.py recording.json --actor-port 27060 --observer-port 27061
  python tools/replay_repro.py recording.json --validate-only
Optional --peer-recording replays the other participant's actions; --net-map supplies explicit old:new net ids.
"""
import argparse
import json
import math
import pathlib
import socket
import sys
import time

COLS = "t st kind net other item slot x y z a b c d e f g h".split()
KINDS = "bind pose seat_enter seat_leave pickup drop throw inventory_switch inventory_swap inventory_store inventory_unstore right_grip right_release usable ignition attach detach physlock physunlock hitch unhitch command mark entity_pose poi_usable network drive".split()


def integer(value, low, high):
    return type(value) in (int, float) and low <= value <= high and int(value) == value


def number(value):
    return type(value) in (int, float) and -1e100 <= value <= 1e100


def load_recording(path):
    if pathlib.Path(path).stat().st_size > 32 * 1024 * 1024:
        raise ValueError("recording exceeds 32 MiB")
    data = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    if set(data) - {"skipped"} != {"format", "protocol", "sampleHz", "complete", "dropped", "cols", "rows"}:
        raise ValueError("unexpected recording fields")
    if any(type(data[k]) is not int for k in ("format", "protocol", "sampleHz", "dropped")) or data["format"] != 1 or data["sampleHz"] != 10 or data["cols"] != COLS:
        raise ValueError("unsupported recording format")
    if data["complete"] is not True or data["dropped"] != 0:
        raise ValueError("incomplete recording")
    if not integer(data.get("skipped", 0), 0, 4294967295):
        raise ValueError("invalid skipped count")
    if data.get("skipped", 0):
        print(json.dumps({"warning": "unresolvable recording rows skipped", "skipped": data["skipped"]}), file=sys.stderr)
    rows = data["rows"]
    if not isinstance(rows, list) or not 1 <= len(rows) <= 60000:
        raise ValueError("invalid recording length")
    previous = 0
    bound = set()
    for row in rows:
        if not isinstance(row, list) or len(row) != len(COLS) or any(not number(n) for n in row):
            raise ValueError("rows must contain exactly 18 finite numbers")
        if row[0] < previous or row[0] > 86400 or not integer(row[2], 0, len(KINDS) - 1):
            raise ValueError("invalid action/time")
        previous = row[0]
        if any(not integer(row[i], 0, 4294967295) for i in (3, 4)) or any(not integer(row[i], -1, 65535) for i in (5, 6)):
            raise ValueError("invalid object address")
        kind = KINDS[int(row[2])]
        if kind in ("bind", "seat_enter", "seat_leave", "pickup", "drop", "throw", "usable", "ignition", "attach", "detach", "physlock", "physunlock", "hitch", "unhitch", "entity_pose", "drive") and row[3] == 0:
            raise ValueError("action requires a net id")
        if kind in ("hitch", "unhitch", "physlock") and row[4] == 0:
            raise ValueError("action requires a parent net id")
        if kind == "bind":
            if row[3] == 0 or row[3] in bound or not integer(row[10], 0, 65535):
                raise ValueError("missing or duplicate object binding")
            bound.add(row[3])
        elif any(net and net not in bound for net in row[3:5]):
            raise ValueError("action precedes its object binding")
        if kind == "command" and not integer(row[10], 1, 4):
            raise ValueError("command arguments were omitted for privacy; this command needs a manual repro")
        if kind == "network" and (not integer(row[7], 0, 2000) or not integer(row[8], 0, 2000) or any(not 0 <= row[i] <= 100 for i in (10, 11, 12)) or row[13] not in (0, 1)):
            raise ValueError("invalid network conditions")
    return rows


class Bridge:
    def __init__(self, port):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=35)
        self.stream = self.sock.makefile("rwb")

    def __call__(self, command):
        self.stream.write((command + "\n").encode("utf-8"))
        self.stream.flush()
        line = self.stream.readline(4 * 1024 * 1024)
        if not line:
            raise RuntimeError("debug bridge disconnected")
        reply = json.loads(line)
        if not isinstance(reply, dict) or "error" in reply:
            # Replies can contain exception paths or game text. Only the fixed operation name is reported.
            raise RuntimeError("bridge refused " + " ".join(command.split()[:2]))
        return reply

    def close(self):
        self.stream.close()
        self.sock.close()


def close_enough(a, b, tolerance):
    if type(a) is bool or type(b) is bool:
        return type(a) is type(b) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return math.isfinite(a) and math.isfinite(b) and abs(a - b) <= tolerance
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(close_enough(x, y, tolerance) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(close_enough(a[k], b[k], tolerance) for k in a)
    return a == b


def convergence(actor, observer, nets, timeout, tolerance, pois=()):
    deadline = time.monotonic() + timeout
    while True:
        players_a, players_b = actor("repro players"), observer("repro players")
        matched = all(player_matches(local, remote, tolerance) for local, remote in (
            (players_a["local"], players_b["remote"]), (players_b["local"], players_a["remote"])))
        for net in nets:
            a, b = actor(f"repro check {net}"), observer(f"repro check {net}")
            # A held item uses the holder's pose; the two render clocks intentionally differ while moving.
            if a["stored"] or a.get("held"):
                a.pop("pos", None)
                b.pop("pos", None)
            positions = a.pop("pos", None), b.pop("pos", None)
            if not close_enough(a, b, 0.05) or (positions[0] is not None and math.dist(*positions) > tolerance):
                matched = False
                break
        for command in pois:
            if not close_enough(actor(command), observer(command), 0.05):
                matched = False
                break
        if matched:
            return
        if time.monotonic() >= deadline:
            raise RuntimeError("two-copy state did not converge")
        time.sleep(0.1)


def player_matches(local, remote, tolerance):
    return (isinstance(local, dict) and isinstance(remote, dict) and local["net"] == remote["net"] and
            local["seat"] == remote["seat"] and (local["net"] != 0 or math.dist(local["pos"], remote["pos"]) <= tolerance))


def replay(actor, observer, rows, peer_rows=None, net_map=None, timeout=8, tolerance=0.5, speed=1, peer_offset=0):
    bridges = (actor, observer)
    maps = [dict(net_map or {}), {}]
    skipped = 0
    touched = set()
    pois = set()
    armed, old_network, errors = [], [], []
    try:
        states = []
        for bridge in bridges:
            state = bridge("repro status")
            if not state.get("isolated") or state.get("dedicated") is not False or not state.get("bridge") or not state.get("localPlayer") or state.get("playback") or state.get("recorder", {}).get("recording"):
                raise RuntimeError("both bridges must be isolated player copies with bridge enabled and recording off")
            states.append(state)
        if not integer(states[0].get("protocol"), 1, 65535) or states[0]["protocol"] != states[1].get("protocol"):
            raise RuntimeError("the copies must use the same wire protocol")
        if not states[0].get("session") or states[0]["session"] != states[1].get("session"):
            raise RuntimeError("the copies must be in the same multiplayer session")
        for bridge in bridges:
            old_network.append(bridge("net conditions status")["conditions"])
            errors.append(bridge("errors"))
            bridge("repro arm")
            armed.append(bridge)
            if bridge("repro peer").get("peers") != 1:
                raise RuntimeError("each copy must have exactly one connected peer")
        events = [(r[0], 0, i, r) for i, r in enumerate(rows)]
        events += [(r[0] + peer_offset, 1, i, r) for i, r in enumerate(peer_rows or [])]
        events.sort(key=lambda event: event[:3])
        start = time.monotonic()
        for at, side, index, row in events:
            wait = start + at / speed - time.monotonic()
            if wait > 0:
                time.sleep(wait)
            bridge = bridges[side]
            kind = KINDS[int(row[2])]
            if kind == "bind":
                old = int(row[3])
                if old not in maps[side]:
                    maps[side][old] = bridge("repro resolve " + " ".join(str(n) for n in (int(row[10]), *row[7:10])))["net"]
                bridge(f"repro check {maps[side][old]}")
                continue
            action = list(row[2:])
            if kind == "poi_usable":
                pois.add("repro poicheck " + " ".join(format(n, ".17g") for n in (row[5], row[14], row[15], row[16], row[6])))
            for column in (1, 2):
                old = action[column]
                if old:
                    if old not in maps[side]:
                        raise RuntimeError("unresolved object binding")
                    action[column] = maps[side][old]
                    touched.add(action[column])
            reply = bridge("repro act " + " ".join(format(n, ".17g") for n in action))
            if "error" in reply or reply.get("ok") is False:
                raise RuntimeError("repro action refused at row " + str(index))
            skipped += int(reply.get("skipped") is True)
        convergence(actor, observer, sorted(touched), timeout, tolerance, sorted(pois))
        for bridge, baseline in zip(bridges, errors):
            now = bridge("errors")
            if any(now.get(key, 0) > baseline.get(key, 0) for key in ("errors", "exceptions")):
                raise RuntimeError("game logged new errors/exceptions during replay")
        return {"ok": True, "actions": len(events), "objectsChecked": len(touched), "skipped": skipped}
    finally:
        cleanup_failed = False
        for bridge, conditions in zip(bridges, old_network):
            try:
                bridge("net conditions set " + " ".join(str(conditions[k]) for k in ("latencyMs", "jitterMs", "lossPct", "duplicationPct", "reorderPct")) + " " + str(int(conditions.get("sendOnly", False))))
            except (OSError, ValueError, RuntimeError):
                cleanup_failed = True
        for bridge in armed:
            try:
                bridge("repro end")
            except (OSError, ValueError, RuntimeError):
                cleanup_failed = True
        if cleanup_failed:
            raise RuntimeError("replay cleanup failed; check both copies' network conditions and repro state")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("recording")
    parser.add_argument("--peer-recording")
    parser.add_argument("--peer-offset", type=float, default=0)
    parser.add_argument("--actor-port", type=int, default=27060)
    parser.add_argument("--observer-port", type=int, default=27061)
    parser.add_argument("--net-map", help="JSON object of recorded net id to current net id")
    parser.add_argument("--speed", type=float, default=1)
    parser.add_argument("--settle", type=float, default=8)
    parser.add_argument("--tolerance", type=float, default=0.5)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    connections = []
    try:
        if any(not math.isfinite(n) for n in (args.speed, args.settle, args.tolerance, args.peer_offset)) or not 0.1 <= args.speed <= 4 or args.settle <= 0 or args.tolerance <= 0:
            raise ValueError("invalid speed, timeout, tolerance or peer offset")
        rows = load_recording(args.recording)
        peer = load_recording(args.peer_recording) if args.peer_recording else []
        mapping = json.loads(pathlib.Path(args.net_map).read_text(encoding="utf-8")) if args.net_map else {}
        mapping = {int(k): v for k, v in mapping.items()}
        if any(not integer(k, 1, 4294967295) or not integer(v, 1, 4294967295) for k, v in mapping.items()) or len(set(mapping.values())) != len(mapping):
            raise ValueError("invalid or nonunique net map")
        if args.validate_only:
            print(json.dumps({"ok": True, "rows": len(rows), "peerRows": len(peer)}))
            return 0
        if args.actor_port == args.observer_port:
            raise ValueError("use two different bridge ports")
        for port in (args.actor_port, args.observer_port):
            connections.append(Bridge(port))
        print(json.dumps(replay(*connections, rows, peer, mapping, args.settle, args.tolerance, args.speed, args.peer_offset)))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError):
        print(json.dumps({"ok": False, "error": "repro validation, bridge action or convergence failed"}))
        return 1
    finally:
        for bridge in connections:
            bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
