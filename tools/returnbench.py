#!/usr/bin/env python3
"""Coming back to a place you left: the frame in which the game places a stored area's items again.

  tools/returnbench.py [--rounds 2] [--ab Path.To.Bool] [--set Path=Value] [label]

Fresh seeded world, standing at the start. Each round: the game's item streaming stores every item of the start area
(itemPlaceRemoveScript.itemRemoveDist set to 1 m for one streaming pass, then back to the game's value), and the next
passes place them all again (they're within itemSpawnDist) — what happens when you drive back to a town or your base.
Measured over the 4 s after the remove distance is back: worst frame, frames over 25/50 ms, the frame-phase profile.
--ab: off,on,on,off per round.
"""
import json, sys, time, pathlib, datetime, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parent.parent


def car_parts(b):
    return b("call TLDRevamp.Fixes.FarItemLookup.CarParts")["value"]


def one(b, remove_dist, spawn_dist):
    cars0 = car_parts(b)
    b("set itemPlaceRemoveScript.s.itemSpawnDist 0")   # nothing placed while the area is being stored
    b("set itemPlaceRemoveScript.s.itemRemoveDist 1")
    t0 = time.time()
    while time.time() - t0 < 5:
        time.sleep(0.5)
        live = b("get savedatascript.s.items.Count")["value"]
        if live < 10: break
    time.sleep(1.2)   # the destroy wave done
    b(f"set itemPlaceRemoveScript.s.itemRemoveDist {remove_dist}")
    time.sleep(1.2)
    far = b("get savedatascript.s.data.itemData.items.Count")["value"]
    b("reset"); b("loopprof start")
    b(f"set itemPlaceRemoveScript.s.itemSpawnDist {spawn_dist}")
    time.sleep(4)
    s = b("stats"); lp = b("loopprof stop 8")
    back = b("get savedatascript.s.items.Count")["value"]
    cars1 = car_parts(b)
    hs = lp.get("hitches", [])
    worst = max(hs, key=lambda h: h["wall"]) if hs else None
    return {"stored": far, "liveAfter": back, "maxms": s["maxms"], "p99ms": s["p99ms"], "hitches50ms": s["hitches50ms"],
            "over25": len(hs), "worst": worst, "carsBefore": cars0, "carsAfter": cars1, "carsSame": cars0 == cars1, "phases": lp.get("phases", [])[:6]}


def main():
    rounds, ab, sets, rest = 2, None, [], []
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--rounds": rounds = int(next(it))
        elif a == "--ab": ab = next(it)
        elif a == "--set": sets.append(tuple(next(it).split("=", 1)))
        else: rest.append(a)
    label = rest[0] if rest else "returnbench"
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        restore = bench.apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "True"), ("settingsscript.s.S.FFPSLimit", "30"),
                                          (bench.GOD, "True"), ("TLDRevamp.PlayerLoopProfiler.HitchMs", "25"), *sets])
        remove_dist = b("get itemPlaceRemoveScript.s.itemRemoveDist")["value"]
        spawn_dist = b("get itemPlaceRemoveScript.s.itemSpawnDist")["value"]
        res = {"False": [], "True": []}
        try:
            time.sleep(5)
            order = [None] * rounds if ab is None else ["False", "True", "True", "False"] * rounds
            for v in order:
                if v is not None: b(f"set {ab} {v}")
                r = one(b, remove_dist, spawn_dist)
                r["value"] = v
                res.setdefault(str(v), []).append(r)
                w = r["worst"]
                print(f"  {ab or ''}={v}: stored {r['stored']} → live {r['liveAfter']}, max {r['maxms']} ms, p99 {r['p99ms']}, "
                      f">25 {r['over25']}, >50 {r['hitches50ms']}, cars+parts same: {r['carsSame']}" + (f"; worst {w['wall']} ms: " + ", ".join(f"{p} {ms}" for p, ms in w["top"]) if w else ""))
        finally:
            b(f"set itemPlaceRemoveScript.s.itemRemoveDist {remove_dist}")
            b(f"set itemPlaceRemoveScript.s.itemSpawnDist {spawn_dist}")
            bench.restore_settings(restore)
    if ab:
        for k in ("maxms", "over25"):
            a = [r[k] for r in res["False"]]; c = [r[k] for r in res["True"]]
            print(f"{ab} {k}: off {a} on {c}   mean {st.mean(a):.1f} → {st.mean(c):.1f}")
    out = ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    out.write_text(json.dumps({"label": label, "ab": ab, "sets": dict(sets), "results": res}, indent=1))
    print("saved", out.relative_to(ROOT))


if __name__ == "__main__":
    main()
