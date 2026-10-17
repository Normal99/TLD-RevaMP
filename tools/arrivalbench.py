#!/usr/bin/env python3
"""Arrival ("loading into an area") benchmark on the fixed seed.

  tools/arrivalbench.py [label]              4 fixed areas 30 km out; stats from the jump until 15 s after
  tools/arrivalbench.py --profile [label]    same, with the script profiler + slow-frame attribution (> 25 ms)
  tools/arrivalbench.py --set Path=Value     runtime value(s) before the restart

Unlike tools/seeded.py (which waits 8 s after each jump and then drives), this measures the arrival itself: the
frames right after the world has to generate terrain, roads, scenery, buildings and their items around the player.
"""
import sys, time, json, pathlib, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded
from bench import Bridge, vec, place_on_ground

WINDOW = 15


def run(label, sets, profile):
    seeded.set_now(sets)
    seeded.restart_same_seed()
    b = Bridge()
    vs0 = b("get UnityEngine.QualitySettings.vSyncCount")["value"]
    b("set UnityEngine.QualitySettings.vSyncCount 0"); b("set TLDRevamp.TestSafety.BlockAutoSave True")
    b("set mainscript.s.player.godModeTillGrounded True"); b("set kaposztaleves.s.settings.god True")
    out = {"label": label, "sets": dict(sets), "areas": []}
    try:
        home = vec(b("get mainscript.s.player.transform.position")["value"])
        time.sleep(5)
        for i, (dx, dz, heading) in enumerate(seeded.AREAS):
            if profile: b("set TLDRevamp.ScriptProfiler.SlowFrameMs 25"); b("profile start" + (" " + profile if isinstance(profile, str) else ""))
            b("reset"); b("loopprof start"); b("alloc reset")
            t0 = time.time()
            place_on_ground(b, home[0] + dx, home[2] + dz, home[1] + 300, timeout=40)
            b(f"set mainscript.s.player.BodyRot.eulerAngles 0,{heading},0")
            time.sleep(max(0, WINDOW - (time.time() - t0)))
            s = b("stats"); ph = b("loopprof stop 20"); a = b("alloc"); bd = b("breakdown")
            prof = b("profile stop 20") if profile else None
            area = {"area": i, "fps": s["fps"], "p50": s["p50ms"], "p99": s["p99ms"], "p999": s["p999ms"], "max": s["maxms"],
                    "hitches50": s["hitches50ms"], "gc": a["collections"], "breakdown": bd, "slowFrames": ph["slowFrames"], "frames": ph["frames"],
                    "phasesSlow": [(q["p"], q["avgMsAll"], q["avgMsSlow"], q["maxMs"]) for q in ph["phases"][:8]]}
            if prof: area["slowScripts"] = prof.get("bySlowFrames", [])[:12]; area["spikes"] = [(x["m"], x["maxMs"], x["over5ms"]) for x in prof["bySpike"][:10]]
            out["areas"].append(area)
            gcm = next((m for m in bd.get("markers", []) if m["m"] == "GC.Collect"), {})
            print(f"  area{i} fps {s['fps']:6} p50 {s['p50ms']:6} p99 {s['p99ms']:6} p99.9 {s['p999ms']:6} max {s['maxms']:6} >50ms {s['hitches50ms']:3} gc {a['collections']}"
                  f" | slow {bd.get('slowFrames')} slowWithGc {bd.get('slowWithGc')} GC.Collect avgSlow {gcm.get('avgMsSlow')} ms", flush=True)
            if profile:
                for x in area["slowScripts"][:8]: print(f"      slow {x['msPerSlowFrame']:>7}  all {x['msPerFrameAll']:>7}  {x['m']}")
                for x in area["spikes"][:6]: print(f"      spike max {x[1]:>7} ms  >5ms x{x[2]:<4} {x[0]}")
            place_on_ground(b, home[0], home[2], home[1] + 300, timeout=40); time.sleep(3)
    finally:
        b(f"set UnityEngine.QualitySettings.vSyncCount {vs0}")
        b("set kaposztaleves.s.settings.god False")
    try:
        out["alignStats"] = Bridge()("alignstats")
        print("  building alignment:", json.dumps(out["alignStats"]))
    except Exception as e:
        print("  alignstats failed:", e)
    path = pathlib.Path(__file__).resolve().parent.parent / "bench" / (time.strftime("%Y%m%d-%H%M%S") + f"-{label}.json")
    path.write_text(json.dumps(out, indent=1))
    ms = [a["max"] for a in out["areas"]]; p99 = [a["p99"] for a in out["areas"]]; h = [a["hitches50"] for a in out["areas"]]
    print(f"  => mean max {st.mean(ms):.1f} ms, mean p99 {st.mean(p99):.1f} ms, hitches>50ms total {sum(h)}")
    return out


def main():
    sets, rest, prof = [], [], False
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--profile": prof = True
        elif a == "--methods": prof = next(it)
        else: rest.append(a)
    with game.ProtectedSession():
        if not game.bridge_up():
            game.launch()
        run(rest[0] if rest else "arrival", sets, prof)


if __name__ == "__main__":
    main()
