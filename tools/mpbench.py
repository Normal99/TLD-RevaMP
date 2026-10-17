#!/usr/bin/env python3
"""Multiplayer host load: the real player stands at the start, N fake players (generation centres, as the host has for
every remote player) drive in a circle `radius` m away. Fresh seeded world per player count.

  tools/mpbench.py                       counts 0,1,3,5,9 extra players, radius 5000 m, 25 m/s
  tools/mpbench.py --counts 0,9 --radius 3000 --profile
"""
import sys, time, json, pathlib, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge

WARM, WINDOW = 10, 30


def one(n, radius, speed, sets, profile):
    seeded.set_now(sets)
    seeded.restart_same_seed()
    b = Bridge()
    vs0 = b("get UnityEngine.QualitySettings.vSyncCount")["value"]
    b("set UnityEngine.QualitySettings.vSyncCount 0"); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    b("set mainscript.s.player.godModeTillGrounded True"); b("set kaposztaleves.s.settings.god True")
    try:
        r = b(f"fakeplayers {n} {speed} {radius}")
        time.sleep(WARM)
        if profile: b("set TLDRevamp.ScriptProfiler.SlowFrameMs 20"); b("profile start" + (" " + profile if isinstance(profile, str) else ""))
        b("reset"); b("loopprof start"); b("alloc reset"); time.sleep(WINDOW)
        s = b("stats"); ph = b("loopprof stop 200"); a = b("alloc")
        prof = b("profile stop 20") if profile else None
        phys = b("physics")
        b("fakeplayers 0")
    finally:
        b(f"set UnityEngine.QualitySettings.vSyncCount {vs0}"); b("set kaposztaleves.s.settings.god False")
    d = {q["p"]: q["avgMsAll"] for q in ph["phases"]}
    out = {"players": n + 1, "genArounds": r.get("genArounds"), "fps": s["fps"], "p50": s["p50ms"], "p99": s["p99ms"], "p999": s["p999ms"],
           "max": s["maxms"], "hitches50": s["hitches50ms"], "gc": a["collections"], "allocKB": a["allocKBperFrame"], "heapMB": a["heapMB"],
           "update": d.get("Update.ScriptRunBehaviourUpdate"), "render": d.get("PostLateUpdate.FinishFrameRendering"),
           "physics": d.get("FixedUpdate.PhysicsFixedUpdate"), "rigidbodies": phys.get("rigidbodies")}
    if prof: out["top"] = [(x["m"], x["msPerSec"], x["maxMs"]) for x in prof["byTime"][:12]]; out["spikes"] = [(x["m"], x["maxMs"], x["over5ms"]) for x in prof["bySpike"][:8]]
    return out


def main():
    counts, radius, speed, profile, sets, label = [0, 1, 3, 5, 9], 5000, 25, False, [], "mpbench"
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--counts": counts = [int(x) for x in next(it).split(",")]
        elif a == "--radius": radius = float(next(it))
        elif a == "--speed": speed = float(next(it))
        elif a == "--profile": profile = True
        elif a == "--methods": profile = next(it)
        elif a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--vanilla":
            import drivebench
            sets += [(p_, "False") for p_ in drivebench.ALL_FIXES + ["TLDRevamp.Fixes.AlignNearestRoad.Enabled", "TLDRevamp.Fixes.AiRaycastNoAlloc.Enabled",
                                                                 "TLDRevamp.Fixes.RebaseParticles.Enabled", "TLDRevamp.Fixes.IdlePourPause.Enabled", "TLDRevamp.Fixes.MirrorThrottle.Enabled"]]
        else: label = a
    res = []
    with game.ProtectedSession():
        if not game.bridge_up():
            game.launch()
        # every --set is restored to what it was (a default-off switch set True must not leak into later runs)
        from bench import Bridge
        originals = [(p_, Bridge()(f"get {p_}")["value"]) for p_, _ in sets]
        try:
            run_counts(counts, radius, speed, sets, profile, res)
        finally:
            if originals: seeded.set_now(originals)
    path = pathlib.Path(__file__).resolve().parent.parent / "bench" / (time.strftime("%Y%m%d-%H%M%S") + f"-{label}.json")
    path.write_text(json.dumps(res, indent=1))


def run_counts(counts, radius, speed, sets, profile, res):
        for n in counts:
            r = one(n, radius, speed, sets, profile)
            res.append(r)
            print(f"  players {r['players']:2} (centres {r['genArounds']}): fps {r['fps']:6} p50 {r['p50']:6} p99 {r['p99']:6} p99.9 {r['p999']:6} max {r['max']:7} >50ms {r['hitches50']:3}"
                  f" gc {r['gc']:3} alloc {r['allocKB']:6} KB/f | update {r['update']} render {r['render']} physics {r['physics']} bodies {r['rigidbodies']}", flush=True)
            if profile:
                for m in r["top"][:8]: print(f"        time  {m[1]:>8} ms/s  max {m[2]:>7}  {m[0]}")
                for m in r["spikes"][:5]: print(f"        spike max {m[1]:>7} ms  >5ms x{m[2]:<4} {m[0]}")


if __name__ == "__main__":
    main()
