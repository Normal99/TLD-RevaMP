#!/usr/bin/env python3
"""Drop-out, lease half (two machines): the laptop drives down the road until it holds a building lease, then
drops (`mp stop`) before the spawn capture completes. The desktop then drives to the same spot. Whatever path
the lease took — freed (the laptop left too early) or done (its capture finished first) — the building must
end up populated on the desktop: it spawns it itself (freed), or it holds the laptop's shared objects (done)."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=20, step=0.25):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception:
            pass
        time.sleep(step)
    return False

def pos(who):
    p = who("get mainscript.s.player.transform.position")["value"]
    return tuple(float(v) for v in p.strip("()").split(","))

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        for who in (b, L):
            who("set TLDRevamp.TestSafety.BlockAutoSave True"); who("set kaposztaleves.s.settings.god True")
        b("mp host 27070")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 120)
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        print("laptop joined")

        # both take a car ON the road (the scenario recipe: spawn, face, sit twice) facing down the road
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        R = (F[1], -F[0])                                           # right of travel
        for who, side in ((b, -3.0), (L, 3.0)):                     # separate lanes: never the same seat
            cx, cz = rp["x"] + F[0] * 8 + R[0] * side, rp["z"] + F[1] * 8 + R[1] * side
            g = who(f"ground {cx} {cz}")
            who(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242")   # car06, the caddy: better handling
            time.sleep(3)
            who(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(1)
            who(f"drivein {cx} {g['y']} {cz}"); time.sleep(2)
            who(f"drivein {cx} {g['y']} {cz}"); time.sleep(3)
            print("  fuelup:", who("fuelup"))   # seated: fuel what the engine drinks
        l0 = pos(L)
        print("laptop in car:", L("get mainscript.s.player.Bsitting")["value"], "| desktop:", b("get mainscript.s.player.Bsitting")["value"])

        # the laptop drives until a building lease actually fires (a poi within 250 m of the road spawns),
        # then drops at once — inside the 4 s capture window, so the lease is undone
        L("autodrive on 20")
        t0 = time.time(); last = 0; kicked = 0; leased = False
        while time.time() - t0 < 420:
            p = pos(L); d = math.dist(p[:2], l0[:2])
            if d - last > 100: print(f"  laptop at {d:.0f} m"); last = d
            if d >= 1500: break
            if not leased and L("mp itemsnear " + f"{p[0]} {p[2]} 400")["leases"]["spawns"] >= 1:
                leased = True; print(f"  laptop leased a building at {d:.0f} m — dropping now (capture still open)")
                break
            st = L("autodrive")
            if st.get("stuckSeconds", 0) > 8 and kicked < 14:   # unstuck: out of the obstacle, then re-arm
                L("autodrive off"); print("   nudge:", L("nudge 12")); time.sleep(1); L("autodrive on 20"); kicked += 1
            time.sleep(1)
        L("autodrive off"); time.sleep(1)
        p = pos(L)
        le = L("mp itemsnear " + f"{p[0]} {p[2]} 400")["leases"]
        print(f"  laptop drove {math.dist(p[:2], l0[:2]):.0f} m (kicks {kicked}), leased: {leased}, asked {le['asked']}, spawns {le['spawns']}")
        le = L("mp itemsnear " + f"{p[0]} {p[2]} 400")["leases"]
        print(f"laptop after the drive: asked {le['asked']}, spawns {le['spawns']}, done {le['done']} (drove 450 m: {math.dist(pos(L)[:2], l0[:2]) >= 440})")

        # drop now: if a capture window (4 s) was still open, its lease is undone and must be freed
        L("mp stop"); time.sleep(2)
        de = b("mp itemsnear " + f"{p[0]} {p[2]} 400")["leases"]
        print(f"after the laptop dropped: server done {de['done']}, freed {de['freed']}")

        # the desktop drives to where the laptop dropped
        d0 = pos(b); goal = math.dist(p[:2], d0[:2])
        print(f"  desktop driving {goal:.0f} m to the spot")
        b("autodrive on 20")
        t0 = time.time(); kicked = 0
        while time.time() - t0 < 420:
            q = pos(b); dq = math.dist(q[:2], d0[:2])
            if dq >= goal - 30: break
            st = b("autodrive")
            if st.get("stuckSeconds", 0) > 8 and kicked < 14:
                b("autodrive off"); print("   nudge:", b("nudge 12")); time.sleep(1); b("autodrive on 20"); kicked += 1
            time.sleep(1)
        b("autodrive off"); time.sleep(1)
        print(f"  desktop drove {math.dist(pos(b)[:2], d0[:2]):.0f} m (kicks {kicked})")
        items_before = b("get savedatascript.s.items.Count")["value"]
        spawned = until(lambda: b("mp itemsnear " + f"{q[0]} {q[2]} 400")["leases"]["spawns"] >= 1, 30)
        time.sleep(6)   # let spawns settle (items can spread over frames)
        de = b("mp itemsnear " + f"{q[0]} {q[2]} 400")["leases"]
        items_after = b("get savedatascript.s.items.Count")["value"]
        print(f"desktop after its drive: spawns {de['spawns']} (spawned itself: {spawned}), "
              f"items {items_before} → {items_after}, freed {de['freed']}, done {de['done']}, reEnabled {de['reEnabled']}")
        print("VERDICT:", "building populated" if items_after > items_before else ("spawned by desktop" if spawned else "EMPTY — bug"))
        b("mp stop"); b("set kaposztaleves.s.settings.god False")
finally:
    L.close()
