#!/usr/bin/env python3
"""Main road only: what the buildings' wait costs per frame (14c6057's LineAhead and PoisWaitForLinks).

  tools/mainroadperf.py [--seconds 60] [--speed 30] [--draw 15000] [--seed 20260924] [--out FILE]

One new main-road-only game on the test copy (TLD_DIR / TLD_TEST_COMPAT / TLD_BRIDGE_PORT); run it with TLD_TEST_FPS=0
so frames aren't capped. Both switches together, in the order on, off, off, on (drift cancels): --seconds standing on
the main road, then --seconds gliding north along it at --speed m/s (new ground each time, like driving). Per window
the frame stats (bridge `stats` after `reset`; mean = wall time / frames, the game's fps field is 1000 / p50), the
buildings held for links, and the profiled road methods' ms per frame (PROFILED).
"""
import argparse, json, pathlib, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game  # noqa: E402

ROAD = "TLDRevamp.WorldGen.MainRoadOnlyRoads"
# what the switches add to: NeedLineAhead is UpdChunksNeeded's postfix and calls UpdChunksToRemove; the buildings' wait
# is RoadsDoneAtPlace's postfix, called from AddPois
PROFILED = "roadGenScript.UpdChunksNeeded roadGenScript.UpdChunksToRemove poiGenScript.AddPois roadGenScript.RoadsDoneAtPlace"


def switch(b, on):
    b(f"set {ROAD}.LineAhead {on}")
    b(f"set {ROAD}.PoisWaitForLinks {on}")


def stand(b, seconds):
    b("reset"); time.sleep(seconds)
    return b("stats")


def glide(b, seconds, speed):
    """North along the main road (global x 0), standing on the ground at the spot the speed has reached by now: one
    `tpground` (global, grounds the player) per step. Each bridge command is verified first (~0.2 s), so steps are
    ~5 a second, a few metres apart at car speed; where the ground isn't there yet the player is held until it is."""
    b("reset")
    g0 = b("gpos")
    t0, t_end, steps, waited = time.time(), time.time() + seconds, 0, 0
    while time.time() < t_end:
        r = b(f"tpground 0 {g0['gz'] + speed * (time.time() - t0):.1f}")
        steps += 1
        waited += 1 if r.get("tpground") == "waiting" else 0
    s = b("stats")
    s["distanceM"] = round(b("gpos")["gz"] - g0["gz"])
    s["steps"], s["stepsHeld"] = steps, waited
    return s


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seconds", type=int, default=60)
    ap.add_argument("--speed", type=float, default=30.0)
    ap.add_argument("--draw", type=float, default=15000.0)
    ap.add_argument("--seed", type=int, default=game.DEFAULT_SEED)
    ap.add_argument("--out", default="mainroadperf.json")
    a = ap.parse_args()
    res = {"draw": a.draw, "seconds": a.seconds, "speed": a.speed, "windows": []}
    with game.ProtectedSession():
        try:
            game.launch()
            b = game.Bridge()
            b("terrain select MainRoadOnly")
            game.newgame(a.seed)
            b = game.Bridge()
            for cmd in ("set TLDRevamp.TestSafety.BlockAutoSave True", "set kaposztaleves.s.settings.god True",
                        "set gamemodesettingsscript.s.S.survival 1", "set UnityEngine.QualitySettings.vSyncCount 0",
                        "set settingsscript.s.S.BEnableFpsLimit False", f"set settingsscript.s.S.FDrawDistance {a.draw}",
                        "overlay off", "bg on"):
                b(cmd)
            b("tpg 0 0 hold"); time.sleep(2); b("tpground 0 0"); time.sleep(20)
            for on in (True, False, False, True):
                switch(b, on)
                time.sleep(3)
                for kind in ("standing", "driving"):
                    h0 = int(b(f"get {ROAD}.PoisHeldForLinks")["value"])
                    b("profile start " + PROFILED)
                    t0 = time.time()
                    s = stand(b, a.seconds) if kind == "standing" else glide(b, a.seconds, a.speed)
                    s["meanMs"] = round((time.time() - t0) * 1000.0 / s["frames"], 3)
                    s["methods"] = {m["m"]: m["msPerFrame"] for m in b("profile stop 10")["byTime"]}
                    s.update(on=on, kind=kind, held=int(b(f"get {ROAD}.PoisHeldForLinks")["value"]) - h0)
                    res["windows"].append(s)
                    print(f"  {'on ' if on else 'off'} {kind:8}: mean {s['meanMs']} ms, p50 {s.get('p50ms')} ms, p99 {s.get('p99ms')} ms, "
                          f"hitches {s.get('hitches50ms')}, held +{s['held']}" + (f", {s['distanceM']} m" if kind == "driving" else "")
                          + "; ms/frame " + ", ".join(f"{m.split('.')[-1]} {v}" for m, v in s["methods"].items()), flush=True)
                    if kind == "driving":
                        time.sleep(10)   # the new area settles before the next standing window
            res["errors"] = {"MainRoadOnlyRoads": b(f"get {ROAD}.Errors")["value"], "last": b(f"get {ROAD}.LastError")["value"]}
        finally:
            if game.running():
                game.quit_game()
    pathlib.Path(a.out).write_text(json.dumps(res, indent=1))
    for kind in ("standing", "driving"):
        m = {on: [w for w in res["windows"] if w["kind"] == kind and w["on"] == on] for on in (True, False)}
        mean = {on: sum(w["meanMs"] for w in m[on]) / len(m[on]) for on in m}
        p50 = {on: sum(w["p50ms"] for w in m[on]) / len(m[on]) for on in m}
        meth = {on: sum(sum(w["methods"].values()) for w in m[on]) / len(m[on]) for on in m}
        print(f"{kind}: mean on {mean[True]:.2f} ms, off {mean[False]:.2f} ms ({mean[True] - mean[False]:+.2f}); "
              f"p50 on {p50[True]:.2f}, off {p50[False]:.2f} ({p50[True] - p50[False]:+.2f}); "
              f"the profiled road methods on {meth[True]:.4f} ms/frame, off {meth[False]:.4f} ({meth[True] - meth[False]:+.4f})")
    print("errors", res.get("errors"), "->", a.out)


if __name__ == "__main__":
    sys.exit(main())
