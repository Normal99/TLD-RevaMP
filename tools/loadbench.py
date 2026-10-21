#!/usr/bin/env python3
"""Save and load times: a fresh seeded world (optionally driven for a while to grow it), saved with the game's autosave,
then loaded back --loads times.

  tools/loadbench.py [--drive-min 0] [--loads 2] [--set Path=Value] [label]

From YujiroX2's play log (docs/TELEMETRY.md): `save` records (ms, bytes) and `load` records (firstUsableS: Load
pressed → the player can move), plus the slow frames of each load (`hitch` records in its first 20 s). The test
prefix's autosave slots are put back afterwards (ProtectedSession). bench/<time>-<label>.json.
"""
import json, sys, time, pathlib, datetime, os
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded, drivebench, tldenv
from bench import Bridge

ROOT = pathlib.Path(__file__).resolve().parent.parent
SESSIONS = tldenv.GAME_DIR / "BepInEx/tldrevamp-feedback/sessions"


def records(after, kinds):
    out = []
    for f in sorted(SESSIONS.glob("*.jsonl"), key=os.path.getmtime):
        if f.stat().st_mtime < after: continue
        for line in f.read_text(errors="replace").splitlines():
            try: d = json.loads(line)
            except ValueError: continue
            if d.get("k") in kinds: d["_file"] = f.name; out.append(d)
    return out


def main():
    drive_min, loads, sets, rest = 0.0, 2, [], []
    it = iter(sys.argv[1:])
    for a in it:
        if a == "--drive-min": drive_min = float(next(it))
        elif a == "--loads": loads = int(next(it))
        elif a == "--set": sets.append(tuple(next(it).split("=", 1)))
        else: rest.append(a)
    label = rest[0] if rest else "loadbench"
    t_open = time.time()
    res = {"label": label, "driveMin": drive_min, "sets": dict(sets)}
    with game.ProtectedSession():
        if not game.bridge_up(): game.launch()
        seeded.set_now(sets)
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        if drive_min > 0:
            b(f"set {bench.GOD} True")
            drivebench.sit_in_car(b); b("autodrive on 25")
            time.sleep(drive_min * 60)
            d = b("autodrive off"); res["driven"] = d.get("distance")
            time.sleep(3)
        res["stored"] = b("get savedatascript.s.data.itemData.items.Count")["value"]
        res["live"] = b("get savedatascript.s.items.Count")["value"]
        before = set(os.listdir(game.SAVES)) if game.SAVES.exists() else set()
        b("set TLDRevamp.TestSafety.BlockAutoSave False")
        b("call savedatascript.s.saveScreen.AutoSave")
        time.sleep(4)
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        new = sorted(set(os.listdir(game.SAVES)) - before) or sorted(os.listdir(game.SAVES))
        files = [f for f in new if f.startswith("TLDsaveData_") and f.endswith(".tlds")]
        res["saveFiles"] = {f: (game.SAVES / f).stat().st_size for f in new}
        name = pathlib.Path(files[0]).stem[len("TLDsaveData_"):] if files else None
        print("saved:", res["saveFiles"], "→ loading", name)
        res["loads"] = []
        for i in range(loads):
            t0 = time.time()
            b = game.loadsave(name)   # the game's save screen; returns 10 s after the player stands on the ground
            res["loads"].append({"wallS": round(time.time() - t0 - 10, 1)})
    recs = records(t_open, ("load", "save", "hitch"))
    res["saveRecords"] = [r for r in recs if r["k"] == "save"]
    res["loadRecords"] = [r for r in recs if r["k"] == "load"]
    res["hitches"] = [r for r in recs if r["k"] == "hitch"]
    print("save:", [(r.get("ms"), r.get("bytes"), r.get("auto")) for r in res["saveRecords"]])
    print("loads:", [(r["_file"], r.get("kind"), r.get("firstUsableS")) for r in res["loadRecords"]])
    for h in sorted(res["hitches"], key=lambda h: -h.get("ms", 0))[:8]:
        print(f"  hitch {h.get('ms')} ms t {h.get('t')} {h['_file']} loading {h.get('loading')} gen {h.get('gen')}")
    f = ROOT / "bench" / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    f.write_text(json.dumps(res, indent=1))
    print("saved", f.relative_to(ROOT))


if __name__ == "__main__":
    main()
