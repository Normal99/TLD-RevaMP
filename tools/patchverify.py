#!/usr/bin/env python3
"""The fixes that patch a game method at runtime (src/TLDRevamp/Fixes/GamePatch.cs) checked in game, each with its own
Verify switch on, in a fresh seeded world.

  tools/patchverify.py [--no-build] SCENARIO [SCENARIO...]

  spawn    OutfitDistanceNoAlloc: 20 s standing at the start (the player's model before and after every render)
  pile     TankcapIdle (switched on for the run): 300 items of 10 kinds, a barrel flipped open to pour over them, 30 s
  carpark  WheelSizeWrites: 16 cars standing, 15 s
  return   FarItemLookup: the start area stored and placed again (returnbench's round), cars keep their parts
  arrival  AlignNearestRoad, RoadNearestPrune, ObjPlaceInPlace: two jumps 30 km out, 15 s each (terrain generation:
           run it alone, no other game on the machine)

A fix passes when it is patched (its Stats "patched"), did checked work (> 0) and had 0 mismatches.
"""
import json, os, sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench, game, seeded, physbench, carpark, returnbench
from bench import Bridge, vec, place_on_ground

FIXES = {
    "spawn": ["OutfitDistanceNoAlloc"],
    "pile": ["TankcapIdle"],
    "carpark": ["WheelSizeWrites"],
    "return": ["FarItemLookup"],
    "arrival": ["AlignNearestRoad", "RoadNearestPrune", "ObjPlaceInPlace"],
}
VERDICTS = []


def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)


def stats(b, fix):
    v = b(f"get TLDRevamp.Fixes.{fix}.Stats")["value"]
    return json.loads(v) if isinstance(v, str) else v


def check(b, fix, work_key, more=None):
    s = stats(b, fix)
    work = s.get(work_key, 0)
    ok = s.get("patched", 0) >= 1 and work > 0 and s.get("verifyMismatches", 0) == 0 and (more is None or more(s))
    verdict(f"{fix}: patched, {work_key} {work}, mismatches {s.get('verifyMismatches', 0)}", ok, json.dumps(s))
    return s


def spawn(b):
    time.sleep(20)
    check(b, "OutfitDistanceNoAlloc", "verifyChecks")


def pile(b):
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    cx, gy, cz = physbench.find_test_area(b, home)
    ox, oz = cx - physbench.COLS * physbench.SPACING / 2, cz - physbench.COLS * physbench.SPACING / 2
    place_on_ground(b, ox - 3, cz, home[1] + 300, timeout=30)
    time.sleep(3)
    print("  ", b(f"spawnpile {physbench.ITEM_MIX} {physbench.COUNT} {ox} {gy} {oz} {physbench.COLS} {physbench.SPACING} {physbench.HEIGHT} {physbench.SEED}"))
    time.sleep(12)
    b("set mainscript.s.player.BodyRot.eulerAngles 0,90,0")
    print("   pour:", b("pourtest start"))
    for _ in range(9):
        time.sleep(2); print("   pour:", b("pourtest status"))
    time.sleep(10)
    check(b, "TankcapIdle", "verifyChecks", lambda s: s["skippedWrites"] > 0)
    b("clearpile")


def carpark_(b):
    carpark.build(b)
    s0 = stats(b, "WheelSizeWrites")
    time.sleep(15)
    s = check(b, "WheelSizeWrites", "skipped")
    print(f"   in 15 s: writes {s['writes'] - s0['writes']}, skipped {s['skipped'] - s0['skipped']}")
    b("clearpile")


def return_(b):
    remove_dist = b("get itemPlaceRemoveScript.s.itemRemoveDist")["value"]
    spawn_dist = b("get itemPlaceRemoveScript.s.itemSpawnDist")["value"]
    try: b("loopprof stop")   # returnbench starts its own
    except RuntimeError: pass
    try:
        r = returnbench.one(b, remove_dist, spawn_dist)
    finally:
        b(f"set itemPlaceRemoveScript.s.itemRemoveDist {remove_dist}")
        b(f"set itemPlaceRemoveScript.s.itemSpawnDist {spawn_dist}")
    print(f"   stored {r['stored']} → live {r['liveAfter']}, worst frame {r['maxms']} ms, cars keep their parts: {r['carsSame']}")
    verdict("cars keep their parts through storing and placing", r["carsSame"], f"{r['carsBefore']} | {r['carsAfter']}")
    check(b, "FarItemLookup", "verifyChecks")


def arrival(b):
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    for dx, dz, heading in seeded.AREAS[:2]:
        place_on_ground(b, home[0] + dx, home[2] + dz, home[1] + 300, timeout=40)
        b(f"set mainscript.s.player.BodyRot.eulerAngles 0,{heading},0")
        time.sleep(15)
    place_on_ground(b, home[0], home[2], home[1] + 300, timeout=40)
    check(b, "AlignNearestRoad", "verifyChecks")
    check(b, "RoadNearestPrune", "verifyChecks")
    check(b, "ObjPlaceInPlace", "verifyChecks")


SCENARIOS = {"spawn": spawn, "pile": pile, "carpark": carpark_, "return": return_, "arrival": arrival}


def main():
    names = [a for a in sys.argv[1:] if not a.startswith("--")]
    bad = [n for n in names if n not in SCENARIOS]
    if not names or bad: sys.exit(__doc__ + (f"\nunknown: {bad}" if bad else ""))
    with game.ProtectedSession():
        if "--no-build" not in sys.argv: game.build()
        game.quit_game(); game.launch()
        seeded.restart_same_seed()
        b = Bridge(); b("bg on")
        ping = b("ping")
        verdict("no patch failed to apply", ping.get("patchFailures", 0) == 0, f"{ping.get('patchFailures')} {ping.get('failedPatches')}")
        sets = [(bench.GOD, "True"), ("TLDRevamp.TestSafety.BlockAutoSave", "True"), ("UnityEngine.QualitySettings.vSyncCount", "0"),
                ("TLDRevamp.Fixes.TankcapIdle.Enabled", "True")]
        sets += [(f"TLDRevamp.Fixes.{f}.Verify", "True") for n in names for f in FIXES[n] if f != "WheelSizeWrites"]
        bench.game_errors()   # the baseline: only errors from here on count
        restore = bench.apply_changes(b, sets)
        try:
            time.sleep(5)
            for n in names:
                print(f"-> {n}", flush=True)
                SCENARIOS[n](b)
        finally:
            bench.restore_settings(restore)
        print("game errors:", bench.game_errors()[:5])
        if "--keep" not in sys.argv: game.quit_game()
    print(f"{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed")
    sys.exit(0 if all(ok for _, ok in VERDICTS) else 1)


if __name__ == "__main__":
    main()
