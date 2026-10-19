#!/usr/bin/env python3
"""Run every car prefab with TrailerHitches in the background test game and hitch the stock trailer to it.

The test uses the repository's isolated Proton prefix and protected save workflow. A single-machine host is enough to
exercise the game's real trailer drawbar, receiver slot, and save-record attachment path. Set TLD_TEST_MODS only here;
the regular test launcher deploys/removes this example mod through tools/game.py.
  tools/trailerhitches.py [--no-build]
"""
import json
import math
import os
import pathlib
import socket
import sys
import time

os.environ["TLD_TEST_MODS"] = "TrailerHitches"
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import clear_road, until

SEED = 20261019
TRAILER_ID = 53
VERDICTS = []


def verdict(name, ok, detail=""):
    VERDICTS.append((name, bool(ok)))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)


def roots(h):
    return {e["net"]: e for e in h("mp entities")["entities"] if e["part"] < 0}


def spawn(h, item_id, x, z, yaw):
    before = set(roots(h))
    result = h(f"spawncarg {item_id} 0 {x:.3f} {z:.3f} {yaw:.3f}")
    until(lambda: set(roots(h)) - before, 20)
    created = set(roots(h)) - before
    if not created:
        raise RuntimeError(f"item {item_id} did not become a shared root: {result}")
    net = min(created)
    time.sleep(0.8)
    return net, roots(h).get(net, {})


def delete(h, net):
    try:
        h(f"deleteshare {net}")
        until(lambda: net not in roots(h), 10)
    except Exception as e:
        print(f"  cleanup {net}: {e}", flush=True)


def run():
    with game.ProtectedSession():
        if game.running(): game.quit_game()
        if "--no-build" not in sys.argv:
            game.build()
        else:
            game.deploy_local()
        game.launch()
        game.newgame(SEED)
        h = Bridge()
        h("set TLDRevamp.TestSafety.BlockAutoSave True")
        h("set kaposztaleves.s.settings.god True")
        items = h("prefabswith carscript")["items"]
        # Only root car prefabs belong here. The runtime query includes nested occurrences as a safety net; names with
        # 'full' are the game's completed car prefabs and the other root vehicles are its randomized body prefabs.
        candidates = [(int(row[0]), str(row[1])) for row in items if "car" in str(row[1]).lower()]
        if "--photos" in sys.argv:
            # Capture the visually distinct receiver-bearing models after the complete attachment sweep.
            photo_ids = {243, 244, 371, 372, 388, 390, 391, 396, 397}
            candidates = [(item_id, name) for item_id, name in candidates if item_id in photo_ids]
        if not candidates:
            raise RuntimeError("the game reported no car prefabs")
        print(f"  testing {len(candidates)} car prefab IDs: {candidates}", flush=True)

        rp = h("roadpoint")
        f = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
        road = clear_road(h, rp["gx"], rp["gz"], f[0], f[1])
        yaw = road["yaw"]
        x, z = road["gx"] + 1.8, road["gz"]
        f = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.bind(("0.0.0.0", 0))
            port = probe.getsockname()[1]
        host = h(f"mp host {port}")
        print(f"  host on UDP {port}:", host, flush=True)
        time.sleep(1)
        for i, (item_id, item_name) in enumerate(candidates):
            # Tear down each pair after checking it so the same generated, clear patch of road can be reused.
            car = trailer = None
            try:
                car, car_info = spawn(h, item_id, x, z, yaw)
                if not car_info.get("car"):
                    verdict(f"item {item_id} {item_name}: root is a car", False, str(car_info))
                    continue
                trailer, _ = spawn(h, TRAILER_ID, x - f[0] * 7.0, z - f[1] * 7.0, yaw)
                attached = h(f"mount hitch {car} {trailer}")
                slots = h(f"mount slots {car}")["slots"]
                receiver = next((s for s in slots if s[0] == "TrailerReceiverHitch"), None)
                used_receiver = attached.get("hitched") == "TrailerReceiverHitch"
                receiver_ok = (receiver is not None and receiver[2] == 1) if used_receiver else receiver is None
                verdict(f"item {item_id} {item_name}: stock hitch or added receiver attaches",
                        "hitched" in attached and receiver_ok,
                        f"car={car_info.get('name')} result={attached} receiver={receiver}")
                try:
                    # A detached trailer gives an unobstructed view of the receiver itself.
                    if "--photos" in sys.argv and used_receiver:
                        h(f"mount off {car} TrailerReceiverHitch")
                        delete(h, trailer)
                        trailer = None
                    car_pos = car_info.get("pos")
                    if car_pos and len(car_pos) >= 3:
                        # A rear three-quarter view shows the receiver's position against the bumper.
                        right = (math.cos(math.radians(yaw)), -math.sin(math.radians(yaw)))
                        cam_x = car_pos[0] + right[0] * 5.0 - f[0] * 2.0
                        cam_z = car_pos[2] + right[1] * 5.0 - f[1] * 2.0
                        ground = h(f"ground {cam_x:.3f} {cam_z:.3f}")
                        h(f"teleportyaw {cam_x:.3f} {ground['y'] + 1.5:.3f} {cam_z:.3f} {yaw:.3f}")
                        time.sleep(1)
                        h(f"mp lookat {car}")
                        time.sleep(0.25)
                    else:
                        h(f"mp lookat {car}")
                        time.sleep(0.25)
                    h(f"shot trailerhitches_{item_id}")
                except Exception:
                    pass
            except Exception as e:
                verdict(f"item {item_id} {item_name}: receiver and stock trailer attach", False, str(e))
            finally:
                if car is not None:
                    try: h(f"mount off {car} TrailerReceiverHitch")
                    except Exception: pass
                if trailer is not None: delete(h, trailer)
                if car is not None: delete(h, car)

        try:
            h("mp stop")
            errors = h("errors")
            print("  game errors:", errors.get("count"), errors.get("exceptions"), flush=True)
        except Exception as e:
            print("  shutdown diagnostics:", e, flush=True)


if __name__ == "__main__":
    run()
    passed = sum(ok for _, ok in VERDICTS)
    print(f"\nSUMMARY TrailerHitches: {passed}/{len(VERDICTS)} PASS", flush=True)
    if passed != len(VERDICTS): sys.exit(1)
