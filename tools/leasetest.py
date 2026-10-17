#!/usr/bin/env python3
"""Lease end-to-end (two machines), two paths:
S1 freed: laptop hosts, desktop joins; server grants a test key to the client, the client arms the same key,
   ServerOnLeave fires while the client stays connected -> LeaseFreed -> the client's LeaseAsked clears.
S2 content: desktop hosts, laptop joins; the laptop hops (global coords, `tpg`) toward the known building site
   (~27.7 km) until a fresh poi leases, waits for the capture to complete (LeaseDone), drops; the desktop hops
   to the site and takes the shared objects (LeaseTaken).
"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def gpos(who): return who("gpos")
def in_world(who):
    try: return who("get DataFromMenuScript.s.mainmenu")["value"] is False and who("get mainscript.s.player")["value"] is not None
    except Exception: return False

def hop(who, gx, gz, tag=""):
    """One hop to a GLOBAL position (`tpg` converts): high above, god till grounded, settle. Origin-safe."""
    who("set mainscript.s.player.godModeTillGrounded True")
    who(f"tpg {gx} {gz}")
    g = gpos(who)
    ok = until(lambda: "y" in who(f"ground {g['ux']} {g['uz']}"), 20)
    g = gpos(who)
    gr = who(f"ground {g['ux']} {g['uz']}")
    if "y" in gr: who(f"teleportyaw {g['ux']} {gr['y'] + 2} {g['uz']} 0")
    who("set mainscript.s.player.godModeTillGrounded True")
    time.sleep(4)
    if not in_world(who): raise RuntimeError(f"{tag}: left the world during a hop")
    return ok

ONLY1 = "--only-s1" in sys.argv
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    # ---------- S1: freed path (laptop hosts; the desktop is the client that armed the key)
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        print("S1: laptop joined the desktop host (the server)")
        b("call TLDRevamp.Net.Entities.ServerLease 1 fake,1")
        lt = b("leasetable")
        print("  server granted:", [r for r in lt["leases"] if r["key"] == "fake,1"])
        L("leasearm fake,1")
        before = L("mp entities")["leaseKeys"]
        b("call TLDRevamp.Net.Entities.ServerOnLeave 1")
        time.sleep(1.5)
        lt = b("leasetable")
        after = L("mp entities")["leaseKeys"]
        freed = "fake,1" in before and "fake,1" not in after
        print(f"  client had armed the key: {'fake,1' in before}; after LeaseFreed it's cleared: {'fake,1' not in after}; "
              f"server freed counter {lt['freed']}, lease still registered: {[r for r in lt['leases'] if r['key'] == 'fake,1']}")
        print("S1 VERDICT:", "PASS" if freed and lt["freed"] >= 1 else "FAIL")
        b("mp stop"); L("mp stop"); time.sleep(3)

    if ONLY1: raise SystemExit
    # ---------- S2: content path (desktop hosts; the laptop leases a real building, completes, drops)
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        print("S2: desktop hosts, laptop joined")
        site = None
        laptop_last = gpos(L)   # the desktop follows one hop behind: it stays alive by streaming, and its own
        for n in range(1, 30):  # lease gate stays cold (pois are >250 m from it while the laptop searches)
            g = gpos(L)
            try:
                hop(L, g["gx"] + 1200, g["gz"] + 1200, tag=f"laptop hop {n}")
            except RuntimeError as ex:
                print(f"  laptop hop {n} failed ({ex}) — reloading and rejoining")
                L("mp stop"); L("call menuhandler.s.PressedRestart")
                until(lambda: in_world(L), 60); time.sleep(3)
                L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
                L("mp stop"); L(f"mp join {DESKTOP_IP}:27070")
                until(lambda: L("mp status")["client"]["ready"], 150)
                L("set kaposztaleves.s.settings.god True")
                continue
            try:
                hop(b, laptop_last["gx"], laptop_last["gz"], tag=f"desktop follow {n}")
            except RuntimeError as ex:
                print(f"  desktop follow {n} failed: {ex}")
            laptop_last = gpos(L)
            g = gpos(L)
            pois = L(f"pois {g['ux']} {g['uz']} 500")["pois"]
            fresh = [w for w in pois if not w["items"] and not w["fixed"]]
            print(f"  laptop hop {n}: gx={g['gx']:.0f} pois {len(pois)} fresh {[(w['name'][:10], int(w['d'])) for w in fresh[:3]]}")
            if fresh:
                near = min(fresh, key=lambda w: w["d"])
                if near["d"] > 220:
                    hop(L, near["gx"], near["gz"], tag="laptop close-in")   # `pois` gives global poi positions
                g = gpos(L)
                ok = until(lambda: L("mp itemsnear " + f"{g['ux']} {g['uz']} 400")["leases"]["spawns"] >= 1, 15)
                done = until(lambda: L("mp itemsnear " + f"{g['ux']} {g['uz']} 400")["leases"]["done"] >= 1, 12)
                print(f"  laptop lease fired {ok}, capture complete (LeaseDone sent): {done} at gx={g['gx']:.0f}")
                site = (g["gx"], g["gz"])
                break
        if site is None:
            print("S2: no fresh poi within ~35 km — inconclusive")
        else:
            L("mp stop"); time.sleep(2)
            lt = b("leasetable")
            print("  after the drop, the server kept the lease:", [r for r in lt["leases"] if r["done"]])
            d0 = gpos(b)
            dist = math.hypot(site[0] - d0["gx"], site[1] - d0["gz"])
            steps = max(1, int(dist // 1200) + 1)
            for n in range(1, steps + 1):
                g = gpos(b)
                rem = steps - n + 1
                try:
                    hop(b, g["gx"] + (site[0] - g["gx"]) / rem, g["gz"] + (site[1] - g["gz"]) / rem, tag=f"desktop hop {n}")
                except RuntimeError as ex:
                    print("  desktop hop failed:", ex); break
            g = gpos(b)
            items_before = b("get savedatascript.s.items.Count")["value"]
            time.sleep(6)
            pois = b(f"pois {g['ux']} {g['uz']} 500")["pois"]
            items_after = b("get savedatascript.s.items.Count")["value"]
            print(f"  desktop at the site: pois {[(w['name'][:10], w['items'], int(w['d'])) for w in pois[:3]]}, items {items_before} → {items_after}")
            de_leases = b("leasetable")
            taken = any(w["items"] for w in pois)
            print(f"  server lease state: {[(r['key'][:14], r['holder'], r['done']) for r in de_leases['leases'][:4]]}, "
                  f"desktop built-ins disabled {b('mp entities')['builtInDisabled']}")
            print("S2 VERDICT:", "PASS — content path (poi marked spawned on the desktop)" if taken else "EMPTY — bug")
        b("mp stop"); b("set kaposztaleves.s.settings.god False")
finally:
    L.close()
