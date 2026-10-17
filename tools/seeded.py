#!/usr/bin/env python3
"""Reproducible A/B: same seed, same test areas, fresh (restarted) world for every configuration.

  tools/seeded.py Path.To.Bool [label]     A/B a runtime switch: off, on, on, off
  tools/seeded.py --none [label]           one baseline pass, no switch
  tools/seeded.py --methods "T.M ..." [label]  one seeded pass with the per-method profiler
  add --cycle to rebuild + redeploy the mod and relaunch the game first

Each configuration: set the fixed seed → the game's own "Restart (same map, same seed)" → wait for the world →
drive the fixed list of areas (spawn + offset, fixed heading). The game remembers visited areas, so restarting
is what makes every configuration see identical, untouched world generation.
"""
import json, sys, time, datetime, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bench
from bench import Bridge, vec, place_on_ground, scenario_drive
import game

SEED = int(__import__("os").environ.get("TLD_SEED", "20260924"))   # TLD_SEED: another world (e.g. a play session's)
# (dx, dz from spawn, heading in degrees): 30 km out in four directions, driving tangentially
AREAS = [(30000, 0, 0), (0, 30000, 90), (-30000, 0, 180), (0, -30000, 270)]
DRIVE_SECONDS = 45


def restart_same_seed():
    game.newgame(SEED)


def area_scenarios():
    out = []
    for i, (dx, dz, heading) in enumerate(AREAS):
        def fn(b, dx=dx, dz=dz, heading=heading):
            home = vec(b("get mainscript.s.player.transform.position")["value"])
            place_on_ground(b, home[0] + dx, home[2] + dz, home[1] + 300, timeout=30)
            time.sleep(8)
            stats = scenario_drive(b, seconds=DRIVE_SECONDS, heading_deg=heading)
            place_on_ground(b, home[0], home[2], home[1] + 300, timeout=30)
            return stats
        out.append((f"area{i}", fn))
    return out


ALL_FIXES = ["TLDRevamp.Fixes.RoadPlaceCache.Enabled", "TLDRevamp.Fixes.Vector3dComparer.Enabled",
             "TLDRevamp.Fixes.AsyncTerrain.Enabled", "TLDRevamp.Fixes.RoadNearestPrune.Enabled",
             "TLDRevamp.Fixes.ParallelRoadGen.Enabled", "TLDRevamp.Fixes.DotChunkMemo.Enabled",
             "TLDRevamp.Fixes.ItemSpawnSpread.Enabled"]
# Not toggled: ParallelObjGen (off by default), ParallelRoadGen.GuardMissingChunks (stability guard; only acts where
# vanilla would stall world generation, which would break the test itself).


def set_now(sets):
    b = Bridge()
    for path, val in sets:
        b(f"set {path} {val}")


def one_pass(label, sets, profile=False, before_restart=()):
    if before_restart:
        set_now(before_restart)  # so the whole world generation runs in this configuration
    restart_same_seed()
    return bench.run(label, sets=sets, scenarios=area_scenarios(), profile=profile)


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    cycle = "--cycle" in args
    args = [a for a in args if a != "--cycle"]
    # Whole session protected: saves backed up first, autosave slots restored at the end no matter what
    with game.ProtectedSession():
        if cycle:
            game.build()
            game.quit_game()
            game.launch()
        elif not game.bridge_up():
            game.launch()
        _main(args)


def _main(args):
    if args[0] == "--vanilla-vs-mod":
        vanilla_vs_mod(args[1] if len(args) > 1 else "vanilla-vs-mod")
        return
    if args[0] == "--none":
        one_pass(args[1] if len(args) > 1 else "seeded-baseline", [])
        return
    if args[0] == "--set":
        # tools/seeded.py --set Path=Value label: one seeded pass with a runtime value (e.g. a Verify switch)
        one_pass(args[2] if len(args) > 2 else "seeded-set", [tuple(args[1].split("=", 1))])
        return
    if args[0] == "--profile":
        # tools/seeded.py --profile label: one seeded pass with the whole-game per-script profiler
        one_pass(args[1] if len(args) > 1 else "seeded-profile-all", [], profile=True)
        return
    if args[0] == "--methods":
        # tools/seeded.py --methods "T.M T2.M2" label: one seeded pass with the per-method profiler
        one_pass(args[2] if len(args) > 2 else "seeded-profile", [], profile=args[1])
        return
    path = args[0]
    label = args[1] if len(args) > 1 else "seeded-ab"
    results = {"False": [], "True": []}
    for val in ("False", "True", "True", "False"):
        r = one_pass(f"{label}-{val}", [(path, val)])
        results[val].append(r)
    keys = ["fps", "p99ms", "p999ms", "maxms", "hitches50ms"]
    print(f"\n{path}, seed {SEED}, {len(AREAS)} areas, 2 passes each (order off,on,on,off)")
    print(f"{'area/metric':18}" + "".join(f"{'off'+str(i+1):>9}" for i in range(2)) + "".join(f"{'on'+str(i+1):>9}" for i in range(2)))
    for i in range(len(AREAS)):
        for k in keys:
            row = [r["scenarios"][f"area{i}"].get(k) for r in results["False"]] + [r["scenarios"][f"area{i}"].get(k) for r in results["True"]]
            print(f"{'area'+str(i)+'/'+k:18}" + "".join(f"{v:>9}" for v in row))
    out = bench.BENCH / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}-summary.json"
    out.write_text(json.dumps({"path": path, "seed": SEED, "areas": AREAS, "results": results}, indent=2))
    print("saved", out.relative_to(bench.ROOT))


def vanilla_vs_mod(label, order=("False", "True", "True", "False", "False", "True")):
    results = {"False": [], "True": []}
    try:
        for val in order:
            sets = [(p, val) for p in ALL_FIXES]
            name = "vanilla" if val == "False" else "mod"
            results[val].append(one_pass(f"{label}-{name}", sets, before_restart=sets))
    finally:
        set_now([(p, "True") for p in ALL_FIXES])  # leave the mod fully on
    import statistics as st
    keys = ["fps", "p50ms", "p99ms", "p999ms", "maxms", "hitches50ms"]
    print(f"\nVANILLA (all fixes off) vs MOD (all on), seed {SEED}, {len(AREAS)} areas, {len(results['False'])} passes each")
    print(f"{'area/metric':16}{'vanilla mean':>14}{'(min–max)':>16}{'mod mean':>11}{'(min–max)':>16}{'change':>9}")
    for i in range(len(AREAS)):
        for k in keys:
            a = [r["scenarios"][f"area{i}"][k] for r in results["False"]]
            b = [r["scenarios"][f"area{i}"][k] for r in results["True"]]
            ma, mb = st.mean(a), st.mean(b)
            ch = f"{(mb - ma) / ma * 100:+.0f}%" if ma else ""
            print(f"{'area'+str(i)+'/'+k:16}{ma:>14.2f}{f'({min(a):.1f}–{max(a):.1f})':>16}{mb:>11.2f}{f'({min(b):.1f}–{max(b):.1f})':>16}{ch:>9}")
        print()
    out = bench.BENCH / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}-summary.json"
    out.write_text(json.dumps({"seed": SEED, "areas": AREAS, "fixes": ALL_FIXES, "results": results}, indent=2))
    print("saved", out.relative_to(bench.ROOT))


if __name__ == "__main__":
    main()
