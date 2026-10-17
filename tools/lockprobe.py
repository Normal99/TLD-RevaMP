#!/usr/bin/env python3
"""Single player, seed 141641351 start station (leasepair --site 245 620): the game's physics lock (mountStuff:
crates, shelves, trunks) stores and releases items on its own, locally, once a second. In multiplayer each machine
does that by itself — an item released on one machine only (it's a member of its container's shared group, no state
of its own) would sit in different places: leasepair 3 had one loose item 1.5-3 m apart in 3 of 4 runs.
Watches the station from the moment it spawns: every store, release and move > 0.3 m.
Then: which items never come to rest (awake in most samples) and how far they crept - leasepair drift2: 22-25 objects
on the laptop sent states nonstop (20 Hz) with mm of motion; a turd crept 2.6 m. --vanilla / --off A,B: mod fixes off.
  TLD_SEED=141641351 tools/lockprobe.py [--secs 60] [--vanilla | --off A,B]"""
import sys, time, math, argparse, collections, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
ap = argparse.ArgumentParser(); ap.add_argument("--secs", type=int, default=60); ap.add_argument("--vanilla", action="store_true"); ap.add_argument("--off", default="")
a = ap.parse_args()
SX, SZ, R = 246.5, 622.0, 60

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    print("  physics lock setting:", b("get settingsscript.s.S.IPhysicsLock"), flush=True)
    offs = []
    if a.vanilla:
        from drivebench import ALL_FIXES
        offs = list(ALL_FIXES)
    elif a.off: offs = [f"TLDRevamp.Fixes.{n}.Enabled" for n in a.off.split(",")]
    was = {}
    for f in offs:
        try: was[f] = b(f"get {f}").get("value"); b(f"set {f} False")
        except Exception as ex: print("  (no switch", f, ex, ")")
    print(f"  fixes off: {len(was)}", flush=True)
    awake = collections.Counter(); samples = 0; first = {}
    b(f"tpground {SX - 8} {SZ - 8}"); t0 = time.time()
    prev = {}; stats = collections.Counter()
    while time.time() - t0 < a.secs:
        snap = b(f"mp physlocks {SX} {SZ} {R}")
        now = {r[0]: r for r in snap["items"]}
        t = time.time() - t0
        if not prev and now:
            held = collections.Counter((r[7], r[5]) for r in now.values() if r[5])
            print(f"  t+{t:4.1f}s first look: {len(now)} items, held by containers: {dict(held)}", flush=True)
        for k, r in now.items():
            p = prev.get(k)
            if p is None:
                if prev: print(f"  t+{t:4.1f}s new     {r[1]} at {r[2]:.1f},{r[3]:.1f},{r[4]:.1f} {('in ' + r[7] + ' ' + r[5]) if r[5] else 'loose'} rb {r[11]}", flush=True); stats["new"] += 1
                continue
            if p[5] != r[5] or p[6] != r[6]:
                ev = "STORED  " if r[5] else "RELEASED"; stats[ev.strip()] += 1
                print(f"  t+{t:4.1f}s {ev} {r[1]} {('in ' + r[7] + ' ' + r[5]) if r[5] else 'from ' + p[7] + ' ' + p[5]} at {r[2]:.1f},{r[3]:.1f},{r[4]:.1f} rb {r[11]}", flush=True)
            d = math.dist(p[2:5], r[2:5])
            if d > 0.3:
                stats["moved"] += 1
                print(f"  t+{t:4.1f}s moved   {r[1]} {d:.2f} m to {r[2]:.1f},{r[3]:.1f},{r[4]:.1f} {('in ' + r[7]) if r[5] else 'loose'} rb {r[11]}", flush=True)
        prev = now
        if t > 10:   # after the spawn has settled
            samples += 1
            for k, r in now.items():
                first.setdefault(k, r)
                if r[11] == "awake": awake[k] += 1
        time.sleep(1.0)
    restless = [(awake[k] / samples, k) for k in awake if samples and awake[k] / samples > 0.8]
    print(f"  never at rest (awake > 80 % of {samples} samples after 10 s): {len(restless)}", flush=True)
    for f_, k in sorted(restless, reverse=True)[:25]:
        r0, r1 = first[k], prev.get(k, first[k])
        print(f"    {r1[1]:<26} awake {f_:4.0%}  crept {math.dist(r0[2:5], r1[2:5]):.2f} m  {('in ' + r1[7] + ' ' + r1[5]) if r1[5] else 'loose'}", flush=True)
    stats["restless"] = len(restless)
    print(f"VERDICT: {dict(stats)} over {a.secs} s", flush=True)
    for f, v in was.items(): b(f"set {f} {v}")
    b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
