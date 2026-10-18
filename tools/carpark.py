#!/usr/bin/env python3
"""The physbench car park (16 full cars, 4×4, 9 m apart) built in a fresh seeded world, and left standing for measuring.

  tools/carpark.py [--profile T.M,T.M|all] [--ab Path.To.Bool] [--set Path=Value] [--window 12] [label]

The player stands 10 m beside the park, as physbench leaves it. After 10 s of
settling: --profile → the script profiler on those methods for one window; --ab → off,on,on,off windows of fps/p99
with the frame-phase profile; neither → one window. bench/<time>-<label>.json.
"""
import json, sys, time, pathlib, datetime, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded, physbench
from bench import Bridge, vec

ROOT = pathlib.Path(__file__).resolve().parent.parent


def build(b, facing=False):
    physbench.configure(items="2,16,30,123", count=16, cols=4, spacing=9.0, height=1.0)
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    cx, gy, cz = physbench.find_test_area(b, home)
    ox, oz = cx - physbench.COLS * physbench.SPACING / 2, cz - physbench.COLS * physbench.SPACING / 2
    bench.place_on_ground(b, cx - physbench.COLS * physbench.SPACING / 2 - 10, cz, home[1] + 300, timeout=30)
    time.sleep(3)
    print("   ", b(f"spawnpile {physbench.ITEM_MIX} {physbench.COUNT} {ox} {gy} {oz} {physbench.COLS} {physbench.SPACING} "
                   f"{physbench.HEIGHT} {physbench.SEED}"))
    time.sleep(10)
    return [round(cx, 1), round(gy, 1), round(cz, 1)]


def window(b, secs):
    b("reset"); b("loopprof start"); time.sleep(secs)
    s = b("stats"); ph = b("loopprof stop 200"); phy = b("physics")
    return {"awake": phy.get("awake"), "rigidbodies": phy.get("rigidbodies"), "fps": s["fps"], "p50ms": s["p50ms"], "p99ms": s["p99ms"], "maxms": s["maxms"],
            "phases": [{"p": q["p"], "avg": q["avgMsAll"]} for q in sorted(ph.get("phases", []), key=lambda q: -q["avgMsAll"])[:10]]}


def main():
    prof, ab, sets, secs, facing, rest = None, None, [], 12, False, []
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--profile": prof = next(it).split(",")
        elif a == "--ab": ab = next(it)
        elif a == "--set": sets.append(tuple(next(it).split("=", 1)))
        elif a == "--window": secs = float(next(it))
        else: rest.append(a)
    label = rest[0] if rest else "carpark"
    out = {"label": label, "time": datetime.datetime.now().isoformat(timespec="seconds"), "sets": dict(sets)}
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        seeded.set_now(sets)
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        restore = bench.apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "False"),
                                          (bench.GOD, "True"), *sets])
        try:
            out["area"] = build(b, facing)
            if prof:
                print("   ", b("profile start" + ("" if prof == ["all"] else " " + " ".join(prof))))
                time.sleep(secs)
                p = b("profile stop 40"); out["profile"] = p
                for m in p.get("byTime", [])[:25]:
                    print(f"  {m['m'][:70]:70} {m['msPerSec']:>8} ms/s  {m['callsPerSec']:>6}/s  max {m['maxMs']}")
            elif ab:
                res = {"False": [], "True": []}
                for v in ("False", "True", "True", "False"):
                    b(f"set {ab} {v}"); time.sleep(2)
                    w = window(b, secs); res[v].append(w)
                    print(f"  {ab}={v}: fps {w['fps']} p50 {w['p50ms']} p99 {w['p99ms']} max {w['maxms']} awake {w['awake']}/{w['rigidbodies']}")
                    for q in w["phases"][:6]: print(f"      {q['p'][:56]:56} {q['avg']}")
                out["ab"] = {"path": ab, "results": res}
                for k in ("fps", "p99ms"):
                    a = [w[k] for w in res["False"]]; c = [w[k] for w in res["True"]]
                    print(f"{ab} {k}: off {a} on {c}   mean {st.mean(a):.2f} → {st.mean(c):.2f}")
            else:
                w = window(b, secs); out["window"] = w
                print(f"  fps {w['fps']} p50 {w['p50ms']} p99 {w['p99ms']} max {w['maxms']}")
                for q in w["phases"]: print(f"    {q['p'][:60]:60} {q['avg']}")
        finally:
            bench.restore_settings(restore)
    f = ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    f.write_text(json.dumps(out, indent=1))
    print("saved", f.relative_to(ROOT))


if __name__ == "__main__":
    main()
