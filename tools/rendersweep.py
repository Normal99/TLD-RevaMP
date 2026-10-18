#!/usr/bin/env python3
"""Upper bounds of rendering settings: RenderLab toggles (bridge `rtoggle`), each window between two baselines.

  tools/rendersweep.py [--carpark] [--window 10] [--toggles name=val,name=val,...] [label]

Fresh seeded world, standing at the start (or beside the physbench car park with --carpark). Default toggles: shadow
distance 50/30 m, 2/1 cascades, small shadow casters off (<0.5 m), reflection probes off, sun shadows off, the distant
and sky cameras off. Every toggle is undone (`rtoggle restore`) before the next baseline. bench/<time>-<label>.json.
"""
import json, sys, time, pathlib, datetime
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded, carpark
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT = ["shadowdist=50", "shadowdist=30", "cascades=2", "cascades=1", "smallcasters=0.5", "probe=", "sunshadows=off",
           "cam:DistantCamera=off", "cam:SkyCamera=off"]


def main():
    park, secs, toggles, rest = False, 10.0, DEFAULT, []
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--carpark": park = True
        elif a == "--window": secs = float(next(it))
        elif a == "--toggles": toggles = next(it).split(",")
        else: rest.append(a)
    label = rest[0] if rest else "rendersweep"
    rows = []
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        restore = bench.apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "False"), (bench.GOD, "True")])
        try:
            if park: carpark.build(b)
            else: time.sleep(8)
            for t in [None] + [x for tog in toggles for x in (tog, None)]:
                if t:
                    name, _, val = t.partition("=")
                    ch = b(f"rtoggle {name} {val}")
                time.sleep(1.5)
                w = carpark.window(b, secs)
                rows.append({"toggle": t, "changed": ch.get("changed") if t else None, **w})
                print(f"  {str(t):28} fps {w['fps']:6} p50 {w['p50ms']:6} p99 {w['p99ms']:6}" + (f"  ({ch.get('changed')} changed)" if t else ""), flush=True)
                if t: b("rtoggle restore")
        finally:
            b("rtoggle restore"); bench.restore_settings(restore)
    # each toggle against the mean of the baselines before and after it
    print("vs neighbouring baselines:")
    for i in range(1, len(rows) - 1, 2):
        base = (rows[i - 1]["fps"] + rows[i + 1]["fps"]) / 2
        print(f"  {rows[i]['toggle']:28} {base:6.1f} → {rows[i]['fps']:6.1f} fps ({(rows[i]['fps'] / base - 1) * 100:+.1f}%)")
    f = ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    f.write_text(json.dumps({"label": label, "carpark": park, "rows": rows}, indent=1))
    print("saved", f.relative_to(ROOT))


if __name__ == "__main__":
    main()
