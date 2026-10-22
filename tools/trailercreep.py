#!/usr/bin/env python3
"""A parked, unhitched trailer on a slope, in single player on this machine (TLDRevamp.Fixes.TrailerCreep): the game's
own trailer, pointing downhill, left alone for a while with the fix off, then a second one beside it with the fix on.
Prints how far each rolled.
  tools/trailercreep.py [--no-build] [--secs 30] [--item 53] [--grade 0.06] [--walk]"""
import sys, time, math, pathlib, re
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

def arg(name, default): return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default
SECS = float(arg("--secs", "30")); ITEM = int(arg("--item", "53")); GRADE = float(arg("--grade", "0.06"))
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def gy(b, x, z):
    r = b(f"groundg {x} {z}")
    return r.get("gy")

def find_slope(b, x0, z0):
    """a spot of open ground 3-12 % steep within ~150 m: (x, z, downhill yaw, grade)"""
    best = None
    for rad in (25, 50, 80, 120, 160):
        for k in range(16):
            a = k * math.pi / 8
            x, z = x0 + math.cos(a) * rad, z0 + math.sin(a) * rad
            h = gy(b, x, z)
            if h is None: continue
            for d in range(8):
                yaw = d * 45.0
                fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
                h2 = gy(b, x + fx * 6, z + fz * 6); h3 = gy(b, x - fx * 6, z - fz * 6)
                if h2 is None or h3 is None: continue
                grade = (h3 - h2) / 12.0   # positive: downhill along yaw
                side = gy(b, x + fz * 3, z - fx * 3)
                if side is None or abs(side - h) > 0.4: continue   # not across a ditch
                if 0.03 <= grade <= 0.2 and (best is None or abs(grade - GRADE) < abs(best[3] - GRADE)): best = (x, z, yaw, grade)
        if best: return best
    return best

with game.ProtectedSession():
    if "--no-build" not in sys.argv: game.build()
    game.quit_game(); game.launch(); game.newgame()
    b = Bridge(); time.sleep(3)
    if "--list" in sys.argv: print(" trailers:", b("call TLDRevamp.Fixes.TrailerCreep.Trailers")["value"], flush=True)
    rp = b("roadpoint")
    spot = find_slope(b, rp["gx"], rp["gz"])
    print(" slope:", spot, flush=True)
    if not spot: sys.exit("NO SLOPE FOUND")
    x, z, yaw, grade = spot
    fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
    results = {}
    for i, on in enumerate((False, True)):
        b(f"set TLDRevamp.Fixes.TrailerCreep.Enabled {on}")
        sx, sz = x + fz * 5 * i, z - fx * 5 * i   # the second one beside the first
        b(f"tpground {sx - fz * 6} {sz + fx * 6}"); time.sleep(3)
        print(f" spawn (fix {'on' if on else 'off'}):", b(f"spawncarg {ITEM} 0 {sx} {sz} {yaw}"), flush=True)
        time.sleep(4)   # dropped onto the ground and settled
        def pos():
            rows = b(f"whereis {'cartrailer' if ITEM in (9200, 9201) else 'Utanfuto'}").get("items", [])
            pts = []
            for r in rows:
                v = [float(t) for t in re.findall(r"-?\d+(?:\.\d+)?(?:[eE]-?\d+)?", r[2])]
                if len(v) >= 3 and "attached False" in r[3]: pts.append(v[:3])
            if not pts: return None
            return min(pts, key=lambda v: math.dist((v[0], v[2]), (sx, sz)))
        p0 = pos(); time.sleep(SECS / 2); pm = pos(); time.sleep(SECS / 2); p1 = pos()
        moved = math.dist((p0[0], p0[2]), (p1[0], p1[2])) if p0 and p1 else -1
        late = math.dist((pm[0], pm[2]), (p1[0], p1[2])) if pm and p1 else -1
        print(f"  fix {'on' if on else 'off'}: the second half {late:.2f} m", flush=True)
        if on: results["late"] = late
        results[on] = moved
        print(f"  fix {'on' if on else 'off'}: rolled {moved:.2f} m in {SECS:.0f} s ({p0} -> {p1})", flush=True)
    print(" creep holds:", b("get TLDRevamp.Fixes.TrailerCreep.Held"), flush=True)
    if "--walk" in sys.argv:
        # the held trailer walked by its drawbar (the fix lets go while the player holds it), then left again;
        # first with the fix off, as a reference for how far a walk takes it
        b("mp host 27070"); time.sleep(3)
        ents = [e for e in b("mp entities")["entities"] if e["part"] < 0 and not e["car"]]
        p_ref = pos()
        net = min(ents, key=lambda e: math.dist((e["pos"][0], e["pos"][2]), (p_ref[0], p_ref[2])))["net"] if ents and p_ref else None
        # holding the drawbar, standing: the fix must let go of it (a player can then pull it). Scripted walking with
        # it isn't checked: the game drops the drawbar after 0.3 s of scripted walking even without the fix (the
        # trailer can't keep up with an instant full-speed walk)
        kept = {}
        for on in (False, True):
            b(f"set TLDRevamp.Fixes.TrailerCreep.Enabled {on}")
            print(" drawbar:", b(f"mount drawbar {net} 0") if net else "no trailer entity", flush=True)
            states = []
            for _ in range(6):
                time.sleep(0.25); states.append((b("mount drawbar state")["pickedUp"], round(float(b("get mainscript.s.player.pickupDist")["value"]), 2)))
            h0 = b("get TLDRevamp.Fixes.TrailerCreep.Held")["value"]; time.sleep(1.0)
            st1 = b("mount drawbar state"); h1 = b("get TLDRevamp.Fixes.TrailerCreep.Held")["value"]
            kept[on] = "Grip" in st1.get("pickedUp", "")
            print(f"  fix {'on' if on else 'off'}: held / hold distance {states}; still holding {kept[on]}, held steps {h1 - h0}", flush=True)
            print(" let go:", b("mount drawbar stop"), flush=True); time.sleep(2)
        if not kept[False]: print("  INCONCLUSIVE: the game dropped the drawbar without the fix too (this scripted pickup doesn't hold)", flush=True)
        else:
            verdict("the drawbar stays in the hand with the fix as without", kept[True], f"without {kept[False]}, with {kept[True]}")
            verdict("while the player holds the drawbar the fix lets go", h1 - h0 == 0, f"{h1 - h0} held steps in 1 s")
        time.sleep(4); pa = pos(); time.sleep(10); pb = pos()
        after = math.dist((pa[0], pa[2]), (pb[0], pb[2])) if pa and pb else -1
        print(f"  then {after:.2f} m in 10 s left alone", flush=True)
        verdict("left on the slope again, it stays (under 0.03 m in 10 s)", 0 <= after < 0.03, f"{after:.2f} m")
        b("mp stop")
    verdict("the slope makes a trailer roll without the fix", results[False] > 0.3, f"{results[False]:.2f} m")
    verdict("with the fix it stops (second half under 0.03 m)", 0 <= results["late"] < 0.03, f"{results['late']:.2f} m, {results[True]:.2f} m in all")
    if "--keep" not in sys.argv: game.quit_game()

print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
