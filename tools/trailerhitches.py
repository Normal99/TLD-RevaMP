#!/usr/bin/env python3
"""Measure receivers and hitch the stock trailer in an ALREADY RUNNING disposable test copy.

Never launches, deploys, builds or controls TLD Copies. Start a disposable world with the bridge and the mod installed.
  tools/trailerhitches.py --port 27050 [--photos] [--out bench/hitches.json]
  tools/trailerhitches.py --port 27050 --keep-save-pair bench/hitch-save.json
Then save manually, restart/reload with the mod, and run --verify-save. Restart/reload that same save after removing
only TrailerHitches (keep the updated RevaMP core), and run --verify-save ... --removed.
"""
import argparse
import json
import math
import pathlib
import socket
import time

TRAILER_ID = 53
EXPECTED = {243, 244, 371, 372, 388, 390, 391, 396, 397}
VERDICTS = []


class BridgeUnavailable(RuntimeError):
    """The game stopped servicing commands; abort the sweep instead of queuing more spawns."""


def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok)))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)


class Bridge:
    def __init__(self, port):
        self.port = port

    def __call__(self, command):
        try:
            with socket.create_connection(("127.0.0.1", self.port), timeout=60) as sock:
                sock.sendall((command + "\n").encode("utf-8"))
                with sock.makefile("r", encoding="utf-8") as reader:
                    result = json.loads(reader.readline())
        except (OSError, ValueError) as e:
            raise BridgeUnavailable(f"{command}: {e}") from e
        if "error" in result:
            if "timeout" in result["error"].lower():
                raise BridgeUnavailable(result["error"])
            raise RuntimeError(result["error"])
        return result


def probe(h, method, *args):
    result = json.loads(h("call TrailerHitches.Probe." + method + " " + " ".join(map(str, args)))["value"])
    if "error" in result:
        raise RuntimeError(result["error"])
    return result


def roots(h):
    return {e["net"]: e for e in h("mp entities")["entities"] if e["part"] < 0}


def wait_probe(h, method, *args, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = probe(h, method, *args)
        if state.get("ready"):
            return state
        time.sleep(0.2)
    raise RuntimeError(f"{method} {args} not ready after {timeout}s")


def spawn(h, item_id, x, z, yaw, owned):
    before = set(roots(h))
    ticket = probe(h, "Spawn", item_id, f"{x:.3f}", f"{z:.3f}", f"{yaw:.3f}")["ticket"]
    owned.append(ticket)  # Retain cleanup ownership even when loading/sharing fails.
    state = wait_probe(h, "SpawnState", ticket)
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        for net, entity in roots(h).items():
            if net in before or entity["dormant"] or entity["stored"] or entity.get("shown") is False or entity["proxy"]:
                continue
            members = h(f"mp entitems {net}")["items"]
            if any(m["i"] == 0 and m["id"] == state["save"] and m["loaded"] for m in members):
                return {**state, "ticket": ticket, "net": net}
        time.sleep(0.2)
    raise RuntimeError(f"item {item_id}, save {state['save']}: no loaded, visible shared root after 20s")


def host_local(h):
    # Release the reservation before the game's transport binds the same UDP port.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as port:
        port.bind(("127.0.0.1", 0))
        host_port = port.getsockname()[1]
    return h(f"mp host {host_port}")


def write_json(path, data):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(data, indent=2) + "\n")


def verify_save(h, path, removed):
    manifest = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    rows = {row[0]: row for row in h("whereis ")["items"]}
    car = rows.get(manifest["car"])
    trailer = rows.get(manifest["trailer"])
    verdict("saved car survives", car is not None)
    verdict("saved trailer survives", trailer is not None)
    current = None
    if not removed:
        catalog = probe(h, "Catalog")["cars"]
        current = next(row for row in catalog if row["id"] == manifest["item"])
    if trailer:
        expected = "attached False" if removed else "attached True slot " + current["slotName"]
        verdict("loaded attachment state", expected in trailer[3], trailer[3])
    if removed:
        try:
            h("get TrailerHitches.Plugin.Version")
            verdict("TrailerHitches absent after restart", False)
        except RuntimeError as e:
            verdict("TrailerHitches absent after restart", "no type found" in str(e))
    else:
        verdict("slot index stable after restart", current["slot"] == manifest["slot"])
        if car and trailer:
            measure = probe(h, "Measure", manifest["car"])
            print("MEASURE reloaded", json.dumps(measure, sort_keys=True), flush=True)
    verdict("no new load exceptions", h("errors").get("count", 0) == 0)


def run(h, args):
    results = []
    started = False
    auto_save = safety = None
    try:
        wait_probe(h, "Ready")
        catalog = probe(h, "Catalog")["cars"]
        added = [row for row in catalog if row["added"]]
        actual = {row["id"] for row in added}
        verdict("nine unsupported stock car variants receive a hitch", actual == EXPECTED,
                f"missing={sorted(EXPECTED - actual)} unexpected={sorted(actual - EXPECTED)}")
        for row in added:
            verdict(f"item {row['id']}: cloned slot fields, collider hierarchy and shared metal", row["structure"])
        candidates = added if args.photos or args.keep_save_pair else catalog
        if args.keep_save_pair:
            candidates = [next(row for row in added if row["id"] == 371)]
        # The probe uses the mod's car/driver/wheel/trailer rule, without prefab-name tests.
        rp = h("roadpoint")
        yaw = rp["yaw"]
        f = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        right = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
        x, z = rp["gx"] + right[0] * 1.8, rp["gz"] + right[1] * 1.8
        auto_save = h("get TLDRevamp.TestSafety.BlockAutoSave")["value"]
        safety = h("get TLDRevamp.Fixes.MissingTrailerSlot.Enabled")["value"]
        h("set TLDRevamp.TestSafety.BlockAutoSave True")
        # A single-machine host gives the existing stock hitch path and shared-object diagnostics.
        started = True
        host_local(h)
        # Put the player by the test lane before cloning anything, so the game keeps both vehicles loaded.
        h(f"tpground {x + right[0] * 10:.3f} {z + right[1] * 10:.3f}")
        deadline = time.monotonic() + 30
        while h("tpground status")["waiting"]:
            if time.monotonic() >= deadline:
                raise RuntimeError("player did not reach the test lane")
            time.sleep(0.2)
        wait_probe(h, "Ready")
        for row in candidates:
            car = trailer = None
            owned = []
            keep = False
            label = f"item {row['id']} {row['name']}"
            try:
                car = spawn(h, row["id"], x, z, yaw, owned)
                trailer = spawn(h, TRAILER_ID, x - f[0] * 8, z - f[1] * 8, yaw, owned)
                attached = h(f"mount hitch {car['net']} {trailer['net']}")
                verdict(label + ": stock drawbar attaches", "hitched" in attached, str(attached))
                time.sleep(args.settle)
                wait_probe(h, "SpawnState", car["ticket"])
                wait_probe(h, "SpawnState", trailer["ticket"])
                measure = probe(h, "Measure", car["save"])
                print("MEASURE", row["name"], json.dumps(measure, sort_keys=True), flush=True)
                results.append({"prefab": row, "measurement": measure})
                verdict(label + ": attachment survives settling", measure["attached"])
                if row["added"]:
                    verdict(label + ": table target", math.dist(row["position"], measure["local"]) < 0.001)
                    verdict(label + ": hitch and drawbar ground heights measured", measure["hitchHeight"] is not None and measure["drawbarHeight"] is not None)
                    verdict(label + ": drawbar level against ground", measure["pitch"] is not None and
                            abs(measure["pitch"]) <= args.max_tilt and abs(measure["roll"]) <= args.max_tilt)
                    verdict(label + ": drawbar seated on target", measure["gap"] is not None and measure["gap"] < 0.05)
                    restored = probe(h, "RecordRoundTrip", car["save"], False)
                    verdict(label + ": save record returns to same owner/slot", restored["attached"] and restored["inSlot"] and restored["index"] == row["slot"])
                    try:
                        h("set TLDRevamp.Fixes.MissingTrailerSlot.Enabled False")
                        stock = probe(h, "RecordRoundTrip", car["save"], True)
                        verdict(label + ": game unknown-slot fallback attaches without slot", stock["attached"] and not stock["inSlot"])
                    finally:
                        h("set TLDRevamp.Fixes.MissingTrailerSlot.Enabled True")
                    missing = probe(h, "RecordRoundTrip", car["save"], True)
                    verdict(label + ": removed slot leaves trailer loose", not missing["attached"] and not missing["inSlot"])
                    if args.keep_save_pair:
                        write_json(args.keep_save_pair, {"item": row["id"], "car": measure["save"], "trailer": restored["trailer"], "slot": row["slot"]})
                        keep = True
                        print("Pair retained. Save manually in this disposable copy; restart and reload before --verify-save.", flush=True)
                if args.photos:
                    h(f"mp lookat {car['net']}")
                    h(f"shot trailerhitches_{row['id']}")
            except BridgeUnavailable:
                raise
            except Exception as e:
                verdict(label + ": test completes", False, str(e))
            finally:
                if not keep:
                    cleanup_unavailable = None
                    for ticket in reversed(owned):
                        try:
                            probe(h, "Cleanup", ticket)
                        except BridgeUnavailable as e:
                            verdict(label + ": cleanup", False, str(e))
                            cleanup_unavailable = e
                        except RuntimeError as e:
                            verdict(label + ": cleanup", False, str(e))
                    if cleanup_unavailable is not None:
                        raise cleanup_unavailable
        verdict("no game exceptions", h("errors").get("count", 0) == 0)
    except Exception as e:
        verdict("measurement run completes", False, str(e))
    finally:
        try:
            restore = ["mp stop"] if started else []
            if safety is not None:
                restore.append(f"set TLDRevamp.Fixes.MissingTrailerSlot.Enabled {safety}")
            if auto_save is not None:
                restore.append(f"set TLDRevamp.TestSafety.BlockAutoSave {auto_save}")
            for command in restore:
                try:
                    h(command)
                except RuntimeError as e:
                    verdict("restore: " + command, False, str(e))
        finally:
            if args.out:
                write_json(args.out, results)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=27050)
    parser.add_argument("--photos", action="store_true")
    parser.add_argument("--settle", type=float, default=5)
    parser.add_argument("--max-tilt", type=float, default=3)
    parser.add_argument("--out")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--keep-save-pair")
    group.add_argument("--verify-save")
    parser.add_argument("--removed", action="store_true")
    args = parser.parse_args()
    if args.removed and not args.verify_save:
        parser.error("--removed requires --verify-save")
    h = Bridge(args.port)
    if args.verify_save:
        verify_save(h, args.verify_save, args.removed)
    else:
        run(h, args)
    passed = sum(ok for _, ok in VERDICTS)
    print(f"SUMMARY TrailerHitches: {passed}/{len(VERDICTS)} PASS", flush=True)
    return 0 if passed == len(VERDICTS) and VERDICTS else 1


if __name__ == "__main__":
    raise SystemExit(main())
