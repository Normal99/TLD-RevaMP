#!/usr/bin/env python3
"""Driving benchmark on the fixed seed: a real car driven along the main road by the mod's scripted driver.

  tools/drivebench.py [label]                    one pass (fresh seeded world)
  tools/drivebench.py --speed 25 [label]         target speed in m/s (default 25 = 90 km/h)
  tools/drivebench.py --set Path=Value [label]   one pass with a runtime value (repeatable)
  tools/drivebench.py --ab Path.To.Bool [label]  off,on,on,off
  tools/drivebench.py --profile [label]          with the per-script profiler (rankings only; it inflates times)

Scenario: Car01Full spawned on the main road ~45 m from the start house, the player sits in (steps next to the seat
first), DriveLab holds the target speed in the right lane and steers around obstacles (seed 20260924 has a rock on
the road ~80 m west). 12 s to get up to speed, then a 40 s measured window with the frame-phase profiler.
A pass is INVALID when the car leaves the ground (< 2 wheels grounded in a sample), gets stuck, or can't hold speed.
"""
import sys, time, math, pathlib, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded
from bench import vec, place_on_ground

CAR = 2            # Car01Full
SPEED = 25.0       # m/s
WARMUP, WINDOW = 12, 40   # east route: rock (KO01) at ~58 s of driving at 25 m/s; end before it
NIGHT = False      # --night: drive at night (street lamps on: ~160 shadow-casting spot lights)
START = None       # --start X,Z (Unity coords): drive from the main road near this point (e.g. near the 2,500 m rebase line)
EAST = True        # seed 20260924: the road west of the start is blocked by a huge rock ~1.1 km out (user saw it)


def scenario_drive(b):
    if START is not None:
        home = vec(b("get mainscript.s.player.transform.position")["value"])
        place_on_ground(b, START[0], START[1], home[1] + 300, timeout=40); time.sleep(6)
    rp = b("roadpoint")
    heading = rp["yaw"] + (180.0 if EAST else 0.0)   # the road bone at the start points west
    if START is not None:
        # drive away from the origin (towards the rebase line): the bone direction whose XZ vector points outward
        import math as _m
        fx, fz = _m.sin(_m.radians(rp["yaw"])), _m.cos(_m.radians(rp["yaw"]))
        heading = rp["yaw"] if fx * rp["x"] + fz * rp["z"] > 0 else rp["yaw"] + 180.0
    yaw = math.radians(heading)
    x, z = rp["x"] + 6 * math.sin(yaw), rp["z"] + 6 * math.cos(yaw)
    g = b(f"ground {x} {z}")
    b(f"spawnpile {CAR} 1 {x} {g['y']} {z} 1 1 1.0 4242")
    time.sleep(4)
    b(f"carface {x} {g['y']} {z} {heading}"); time.sleep(2)
    b(f"drivein {x} {g['y']} {z}"); time.sleep(2)   # step next to the seat
    b(f"drivein {x} {g['y']} {z}"); time.sleep(2)   # sit
    tek0 = None
    if NIGHT:
        tek0 = float(b("get napszakvaltakozas.s.tekeres")["value"])
        t = float(b("get napszakvaltakozas.s.t")["value"])
        dt_ = float(b("get napszakvaltakozas.s.dt")["value"])
        b(f"set napszakvaltakozas.s.tekeres {tek0 + (dt_ + 20 - t)}")  # 20 s into the night (night lasts nt = 300 s)
    b(f"autodrive on {SPEED}")
    time.sleep(WARMUP)
    rb0 = int(b("get mainscript.s.visszarakvaCount")["value"])
    b("reset"); b("loopprof start"); b("alloc reset"); b("rendertime start")
    samples, invalid = [], []
    t_end = time.time() + WINDOW
    while time.time() < t_end:
        time.sleep(2)
        s = b("autodrive")
        gnd = sum(1 for w in s["wheels"] if w["ground"])
        samples.append({"t": round(WINDOW - (t_end - time.time()), 1), "v": s["speed"], "gear": s["gear"], "wheels": gnd, "pos": s["pos"]})
        if gnd < 2: invalid.append(f"airborne at {s['pos']}")
        if s["stuckSeconds"] > 2: invalid.append("stuck")
        if s.get("lookYaw") is not None and abs(s["lookYaw"]) > 5: invalid.append(f"view turned ({s['lookYaw']}°)")
        if s["speed"] < SPEED * 0.4:
            invalid.append(f"crash/stop at {s['pos']} ({s['lastObstacle']})")
            break
    stats = b("stats"); phases = b("loopprof stop 15"); render = b("render"); stats["alloc"] = b("alloc"); stats["rendertime"] = b("rendertime stop")
    stats["rebases"] = int(b("get mainscript.s.visszarakvaCount")["value"]) - rb0
    drv = b("autodrive off")
    if tek0 is not None:
        stats["nightT"] = b("get napszakvaltakozas.s.t")["value"]
        b(f"set napszakvaltakozas.s.tekeres {tek0}")
    speeds = [x["v"] for x in samples]
    if st.mean(speeds) < SPEED * 0.8: invalid.append(f"slow: mean {st.mean(speeds):.1f} m/s")
    stats.update({"phases": phases, "renderStats": render, "drive": {k: v for k, v in drv.items() if k != "wheels"},
                  "samples": samples, "meanSpeed": round(st.mean(speeds), 1), "valid": not invalid, "invalid": sorted(set(invalid))})
    return stats


# Every mod fix that affects play (world gen, wheels, rendering). Not toggled: ParallelRoadGen.GuardMissingChunks
# (stability guard), ImpactSoundLimiter (sound), ParallelObjGen/ParkedCarSleep (off by default).
ALL_FIXES = seeded.ALL_FIXES + ["TLDRevamp.Fixes.ObjGenFast.Enabled", "TLDRevamp.Fixes.RoadConnectMemo.Enabled",
             "TLDRevamp.Fixes.ChunksNeededInPlace.Enabled", "TLDRevamp.Fixes.SleepingWheelSkip.Enabled",
             "TLDRevamp.Fixes.GrassCameraThrottle.Enabled", "TLDRevamp.Fixes.BinocularLensCamera.Enabled",
             "TLDRevamp.Fixes.TinyRendererCull.Enabled", "TLDRevamp.Fixes.ObjPlaceInPlace.Enabled"]


def one_pass(label, sets, profile=False):
    seeded.set_now(sets)  # before the restart, so the new world is generated in this configuration
    seeded.restart_same_seed()
    return bench.run(label, sets=sets, scenarios=[("drive", scenario_drive)], profile=profile)


def show(r):
    s = r["scenarios"]["drive"]
    d = s["drive"]
    print(f"  {'VALID' if s['valid'] else 'INVALID ' + str(s['invalid'])}  mean {s['meanSpeed']} m/s, {d['distance']} m, avoid {d['avoidEvents']}")
    print(f"  fps {s['fps']:6} p50 {s['p50ms']:6} p99 {s['p99ms']:6} max {s['maxms']:6} >50 {s['hitches50ms']}  rebases in window {s.get('rebases')}")
    if "alloc" in s: print(f"  alloc {s['alloc']}")
    if "rendertime" in s:
        rt = s["rendertime"]
        print("  per camera (main thread ms/frame):", ", ".join(f"{c['cam']} {c['msPerFrame']} (x{c['rendersPerFrame']})" for c in rt["cameras"][:8]))
        print("  render counters per frame:", rt["counters"])
    for q in s["phases"]["phases"][:8]:
        print(f"      {q['p'][:44]:44} avg {q['avgMsAll']:>7} slow {q['avgMsSlow']:>7} max {q['maxMs']:>7}")


def main():
    global SPEED, EAST, NIGHT, START
    sets, ab, rest, prof = [], None, [], False
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--ab": ab = next(it)
        elif a == "--speed": SPEED = float(next(it))
        elif a == "--west": EAST = False
        elif a == "--night": NIGHT = True
        elif a == "--start": START = tuple(float(v) for v in next(it).split(","))
        elif a == "--vanilla-vs-mod": ab = "VANILLA"
        elif a == "--pending": ab = "PENDING"
        elif a == "--profile": prof = True
        elif a == "--methods": prof = next(it)
        else: rest.append(a)
    label = rest[0] if rest else "drivebench"
    with game.ProtectedSession():
        if not game.bridge_up():
            game.launch()
        # restore every --set / --ab switch to its value before the run, however main() exits
        import atexit
        paths = [p for p, _ in sets] + ([ab] if ab and ab not in ("PENDING", "VANILLA") else [])
        originals = [(p, bench.Bridge()(f"get {p}")["value"]) for p in paths]
        if originals: atexit.register(lambda: seeded.set_now(originals))
        if ab == "PENDING":
            pend = ["TLDRevamp.Fixes.MirrorThrottle.Enabled", "TLDRevamp.Fixes.RebaseParticles.Enabled", "TLDRevamp.Fixes.IdlePourPause.Enabled"]
            res = {"False": [], "True": []}
            try:
                for val in ("False", "True", "True", "False"):
                    r = one_pass(f"{label}-{'defaults' if val == 'False' else 'pending'}", sets + [("TLDRevamp.Fixes.MirrorThrottle.EveryN", "2")] + [(p, val) for p in pend])
                    show(r)
                    res[val].append(r["scenarios"]["drive"])
            finally:
                seeded.set_now([(p, "False") for p in pend])
            print("\nDEFAULTS vs DEFAULTS + pending visual options (mirrors every 2nd, rebase particles, idle pour pause), 2 passes each")
            for k in ("fps", "p50ms", "p99ms", "p999ms", "maxms"):
                a_ = [x[k] for x in res["False"]]; b_ = [x[k] for x in res["True"]]
                print(f"  {k:6} defaults {a_}  +pending {b_}   mean {st.mean(a_):.2f} → {st.mean(b_):.2f} ({(st.mean(b_) / st.mean(a_) - 1) * 100:+.0f}%)")
            return
        if ab == "VANILLA":
            res = {"False": [], "True": []}
            try:
                for val in ("False", "True", "True", "False"):
                    r = one_pass(f"{label}-{'vanilla' if val == 'False' else 'mod'}", sets + [(p, val) for p in ALL_FIXES])
                    show(r)
                    res[val].append(r["scenarios"]["drive"])
            finally:
                seeded.set_now([(p, "True") for p in ALL_FIXES])
            print(f"\nVANILLA (all fixes off) vs MOD (all on), driving bench, 2 passes each" +
                  ("" if all(x["valid"] for v in res.values() for x in v) else "  !! contains INVALID passes"))
            for k in ("fps", "p50ms", "p99ms", "p999ms", "maxms"):
                a_ = [x[k] for x in res["False"]]; b_ = [x[k] for x in res["True"]]
                print(f"  {k:6} vanilla {a_}  mod {b_}   mean {st.mean(a_):.2f} → {st.mean(b_):.2f} ({(st.mean(b_) / st.mean(a_) - 1) * 100:+.0f}%)")
            print("  collections/40s vanilla", [x["alloc"]["collections"] for x in res["False"]], "mod", [x["alloc"]["collections"] for x in res["True"]])
            return
        if ab is None:
            show(one_pass(label, sets, profile=prof))
            return
        res = {"False": [], "True": []}
        for val in ("False", "True", "True", "False"):
            r = one_pass(f"{label}-{val}", sets + [(ab, val)])
            show(r)
            res[val].append(r["scenarios"]["drive"])
        print(f"\n{ab}: driving benchmark, 2 passes each" + ("" if all(x["valid"] for v in res.values() for x in v) else "  !! contains INVALID passes"))
        for k in ("fps", "p99ms", "maxms"):
            a = [x[k] for x in res["False"]]; bb = [x[k] for x in res["True"]]
            print(f"  {k:6} off {a}  on {bb}   mean {st.mean(a):.2f} → {st.mean(bb):.2f}")


if __name__ == "__main__":
    main()
