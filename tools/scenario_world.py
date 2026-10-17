#!/usr/bin/env python3
"""World contents exist once per session (spawn leases). Desktop hosts the seeded world, laptop joins (and loads the
host's world by itself).
  W1 start area: after joining, both machines show the same items/cars around the start house (fingerprint).
  W2 new building: the laptop reaches a building nobody visited first (it gets the lease and spawns it), then the
     desktop comes: same items, same places.
  tools/scenario_world.py [--no-build] [--watch]
"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP, compare_items
from determinism import go_global, glob

BUILD, PACE = True, 0.0
for a in sys.argv[1:]:
    if a == "--no-build": BUILD = False
    elif a == "--watch": PACE = 1.0

def until(fn, timeout=60, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

T0 = time.time()
print(f"games ready in {ensure_both(BUILD):.0f} s")
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed()
        b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
        home = glob(b)
        print("host:", b("mp host 27070")["server"]["port"], "| host items shared at start:", b(f"mp itemsnear {home[0]} {home[2]} 60")["leases"])
        L(f"mp join {DESKTOP_IP}:27070")
        ok = until(lambda: L("mp status")["client"]["ready"], 120)
        print(f"laptop loaded the host's world and is ready: {ok} ({time.time() - T0:.0f} s)")
        L("set kaposztaleves.s.settings.god True")
        time.sleep(8)
        # W1: start area — same contents on both
        d1, l1 = b(f"mp itemsnear {home[0]} {home[2]} 60"), L(f"mp itemsnear {home[0]} {home[2]} 60")
        if d1["hash"] != l1["hash"]: print("     host items not shared:", d1.get("unshared"), "| laptop items not shared:", l1.get("unshared"))
        m, oa, ob, worst = compare_items(b(f"mp itemslist {home[0]} {home[2]} 60")["items"], L(f"mp itemslist {home[0]} {home[2]} 60")["items"])
        print(f"  W1 start area: {m} items matched on both (worst {worst:.2f} m), only on the desktop {len(oa)}, only on the laptop {len(ob)}", oa[:5], ob[:5])
        print(f"     W1 fingerprints: desktop {d1['count']} items ({d1['shared']} shared) {d1['hash'][:8]} | laptop {l1['count']} items ({l1['shared']} shared) {l1['hash'][:8]} "
              f"→ {'SAME' if d1['hash'] == l1['hash'] else 'DIFFERENT'}; laptop leases {l1['leases']}", flush=True)
        # W3: the laptop picks up one of the host's items (takes ownership), moves it 8 m, it falls and settles
        pk = L("mp pickupnear 6")
        print("     W3 picked:", pk)
        if "net" in pk and pk["net"]:
            net = pk["net"]; time.sleep(1.5)
            lid = L("mp status")["client"]["id"]
            before = {e["net"]: e for e in L("mp entities")["entities"]}[net]["pos"]
            print("     W3 move:", L(f"mp moveitem {net} 8 0")); time.sleep(5)
            le3 = {e["net"]: e for e in L("mp entities")["entities"]}.get(net); de3 = {e["net"]: e for e in b("mp entities")["entities"]}.get(net)
            if le3 and de3:
                print(f"  W3 pickup: owner on the desktop {de3['owner']} (laptop = {lid}), moved {math.hypot(le3['pos'][0] - before[0], le3['pos'][2] - before[2]):.1f} m, "
                      f"desktop shows it {math.dist(le3['pos'], de3['pos']):.2f} m from where it is on the laptop", flush=True)
            else: print("  W3 pickup: entity missing", bool(le3), bool(de3))
        # W2: a building nobody visited — the laptop first
        max_net = max([e["net"] for e in L("mp entities")["entities"]] or [0])
        tx, tz = 29558.0, 94.0
        def stable(side, r=150, secs=4, timeout=90):
            last, since, t = None, time.time(), time.time()
            while time.time() - t < timeout:
                c = side(f"mp itemsnear {tx} {tz} {r}")["count"]
                if c != last: last, since = c, time.time()
                elif c > 0 and time.time() - since > secs: return c
                time.sleep(0.5)
            return last
        L(f"teleport {tx + 80} 400 {tz}"); L("set mainscript.s.player.godModeTillGrounded True")
        until(lambda: L(f"mp itemsnear {tx} {tz} 150")["leases"]["spawns"] >= 1, 90)
        print("     laptop items at the building (settled):", stable(L))
        go_global(b, tx + 80, tz)
        print("     desktop items at the building (settled):", stable(b))
        d2, l2 = b(f"mp itemsnear {tx} {tz} 150"), L(f"mp itemsnear {tx} {tz} 150")
        le, de = L("mp entities"), b("mp entities")
        new = [e for e in le["entities"] if e["net"] > max_net]
        print("     laptop asked leases for:", le["leaseKeys"])
        print("     first new shared objects:", [(e["name"][:18], round(e["pos"][0]), round(e["pos"][2])) for e in new[:8]])
        print(f"     W2 new shared objects: {len(new)}, within 150 m of the building: {sum(1 for e in new if math.hypot(e['pos'][0] - tx, e['pos'][2] - tz) < 150)}")
        print(f"     entities: laptop {le['count']} (rebound {le['rebound']}, far-record updates {le['farRecordUpdates']}), desktop {de['count']} (rebound {de['rebound']}, far-record updates {de['farRecordUpdates']})")
        if d2["hash"] != l2["hash"]: print("     desktop not shared:", d2.get("unshared")[:12], "| laptop not shared:", l2.get("unshared")[:12])
        m, oa, ob, worst = compare_items(L(f"mp itemslist {tx} {tz} 150")["items"], b(f"mp itemslist {tx} {tz} 150")["items"])
        print(f"  W2 new building: {m} items matched on both (worst {worst:.2f} m), only on the laptop {len(oa)}, only on the desktop {len(ob)}", oa[:5], ob[:5])
        print(f"     W2 fingerprints: laptop {l2['count']} items ({l2['shared']} shared) {l2['hash'][:8]} | desktop {d2['count']} items ({d2['shared']} shared) {d2['hash'][:8]} "
              f"→ {'SAME' if d2['hash'] == l2['hash'] else 'DIFFERENT'}; laptop leases {l2['leases']}; desktop leases {d2['leases']}", flush=True)
        print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False")
        print(f"total {time.time() - T0:.0f} s")
finally:
    L.close()
