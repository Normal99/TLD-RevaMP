#!/usr/bin/env python3
"""Main road only (issue #32): does the main road run unbroken, with no crossroads, as you travel along it?

  tools/mainroad.py [--seed 20260924] [--km 8] [--step 600] [--old] [--mode MainRoadOnly] [--shots N] [--menu-shot] [--out DIR]

Starts a new game in the given terrain mode on the test copy (TLD_DIR / TLD_TEST_COMPAT / TLD_BRIDGE_PORT), then
teleports north along the main road in steps. At each stop it waits for the roads to be generated and reads the
bridge's `terrain roads`: the road generators, how many places the main road stops in the chunks the game keeps
(gaps), how many side roads are left in them, and roads that are generated in chunks the game no longer keeps.
The first --shots stops also get a screenshot from 40 m above the road looking north.

--menu-shot opens the game's new-game screen first and takes a screenshot of it (the Terrain dropdown).

--old turns the fix off (TLDRevamp.WorldGen.MainRoadOnlyRoads.Enabled = False) before the world is made: the game's
own behaviour, for an A/B.

PASS: every stop reports 0 gaps, every main road generated, no generated road piece the game can never place, and 0
side roads in main road only and old terrain mode.
"""
import argparse, datetime, json, pathlib, shutil, sys, time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, testdisplay, tldenv  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parent.parent


def shot(b, out, label):
    reply = b("screenshot")
    path = pathlib.Path(testdisplay.normalise_path(reply["path"]))
    deadline, last = time.monotonic() + 15, None
    while time.monotonic() < deadline:
        if path.is_file() and path.stat().st_size > 0:
            n = path.stat().st_size
            if n == last:
                shutil.copy2(path, out / (label + ".png"))
                return str(out / (label + ".png"))
            last = n
        time.sleep(0.3)
    return None


def shot_cmd(b, cmd):
    """A shot list command: its status always carries an "error" field, empty when the command worked."""
    try:
        return b(cmd)
    except RuntimeError as e:
        if not str(e).endswith(": "):
            raise
        return {}


def roads_ready(report):
    gens = [g for g in report["generators"] if g["listed"] and g["active"]]
    return gens and all(g["needed"] > 0 and g["generated"] > 0 and g["mainRoad"].get("mainChunks", 0) > 0 for g in gens)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seed", type=int, default=game.DEFAULT_SEED)
    ap.add_argument("--km", type=float, default=8.0)
    ap.add_argument("--step", type=float, default=600.0)
    ap.add_argument("--mode", default="MainRoadOnly")
    ap.add_argument("--old", action="store_true")
    ap.add_argument("--shots", type=int, default=3)
    ap.add_argument("--menu-shot", action="store_true")
    ap.add_argument("--out")
    args = ap.parse_args()
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = pathlib.Path(args.out or ROOT / "bench" / f"mainroad-{args.mode}{'-old' if args.old else ''}-{stamp}")
    out.mkdir(parents=True, exist_ok=True)
    result = {"seed": args.seed, "mode": args.mode, "old": args.old, "stops": []}
    with game.ProtectedSession():
        try:
            game.launch()
            b = game.Bridge()
            b(f"terrain select {args.mode}")
            if args.menu_shot:
                b("call menuhandler.s.OpenMenuWindow 2")
                time.sleep(2)
                result["menuShot"] = shot(b, out, "newgame-screen")
                result["menuTerrain"] = b("terrain")
            if args.old:
                b("set TLDRevamp.WorldGen.MainRoadOnlyRoads.Enabled False")
            game.newgame(args.seed)
            b = game.Bridge()
            b("set TLDRevamp.TestSafety.BlockAutoSave True")
            b("overlay off")
            result["terrain"] = b("terrain")
            z = 0.0
            n = 0
            while z <= args.km * 1000:
                x = 0.0
                if args.mode == "OldTerrain":   # the 2023 road wanders off x = 0: follow it
                    x = b(f"terrain oldroad {z:.0f}")["gx"]
                b(f"tpg {x:.0f} {z:.0f} hold")
                time.sleep(2)
                b(f"tpground {x:.0f} {z:.0f}")
                t0 = time.monotonic()
                report = b("terrain roads")
                while not roads_ready(report) and time.monotonic() - t0 < 60:
                    time.sleep(2)
                    report = b("terrain roads")
                time.sleep(4)   # the roads around the stop finish generating and get placed
                report = b("terrain roads")
                gens = [g for g in report["generators"] if g["listed"]]
                stop = {"z": z, "seconds": round(time.monotonic() - t0, 1), "generators": gens,
                        "gaps": sum(g["mainRoad"].get("gaps", 0) for g in gens),
                        "sideRoads": sum(g["mainRoad"].get("sideRoads", 0) for g in gens),
                        "mainNotGenerated": sum(g["mainRoad"].get("mainNotGenerated", 0) for g in gens),
                        "unregistered": sum(g.get("unregisteredElements", 0) for g in gens),
                        "unlisted": [g for g in report["generators"] if not g["listed"]]}
                if n < args.shots:
                    road = b("roadpoint")
                    ground = b(f"groundg {road['gx']} {road['gz']}")
                    b("cinema free")
                    b("cinema fov 60")
                    take = "version 1\\nmove %.3f %.3f %.3f %.3f 12 60" % (road["gx"], (ground.get("gy") or 0) + 40, road["gz"] - 60, 0.0)
                    shot_cmd(b, "cinema shot load " + take)
                    time.sleep(1.5)
                    stop["shot"] = shot(b, out, f"stop{n:02d}-z{int(z)}")
                    shot_cmd(b, "cinema shot unload")
                    b("cinema off")
                result["stops"].append(stop)
                g0 = gens[0]["mainRoad"] if gens else {}
                print(f"z {z:7.0f}: gaps {stop['gaps']}, side roads {stop['sideRoads']}, main chunks {g0.get('mainChunks')}, "
                      f"links {g0.get('links')}, pruned {g0.get('roadsPruned')}, off-line removed {g0.get('offLineRemoved')}, first gap '{g0.get('firstGap', '')}', "
                      f"off-line generated {sum(g.get('offLineGenerated', 0) for g in gens)}, "
                      f"generated outside needed {sum(g.get('generatedOutsideNeeded', 0) for g in gens)}, "
                      f"unregistered pieces {sum(g.get('unregisteredElements', 0) for g in gens)}, "
                      f"unplaced in range {sum(g.get('unplacedInRange', 0) for g in gens)}, main not generated {stop['mainNotGenerated']}", flush=True)
                z += args.step
                n += 1
            result["errors"] = b("errors")
        finally:
            if game.running():
                game.quit_game()
            for name, p in (("bep", tldenv.GAME_DIR / "BepInEx/LogOutput.log"),):
                if p.exists():
                    shutil.copy2(p, out / f"{name}.log")
    bad = [s for s in result["stops"] if s["gaps"] or s["mainNotGenerated"] or s["unregistered"]
           or (args.mode in ("MainRoadOnly", "OldTerrain") and s["sideRoads"])]
    result["verdict"] = "PASS" if not bad else "FAIL"
    (out / "result.json").write_text(json.dumps(result, indent=2))
    print(f"{result['verdict']}: {len(result['stops'])} stops, {len(bad)} with gaps or side roads -> {out}")
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
