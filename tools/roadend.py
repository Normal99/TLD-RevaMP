#!/usr/bin/env python3
"""The Road End mod (src/Mods/RoadEnd) in single player: does the main road stop at its end, with the grave and the
note there, while the land and the side roads go on?

  TLD_TEST_MODS=RoadEnd tools/roadend.py [--no-build] [--seed N] [--explore] [--out DIR]

Starts a new game on the test copy (TLD_DIR / TLD_TEST_COMPAT / TLD_BRIDGE_PORT), teleports to just before the end the
mod works out from the main road, and waits for the world there to be made. Then it reads RoadEnd.Probe.State and
takes screenshots: the end of the road with the grave from a few metres back, the note from above, and the edge of
the land from the air.

PASS: the end is known; no main road piece past it, terrain still made past it, no building next to the grave; the
grave and the note stand at the end of the road.
--explore also prints the game's generators, the grave and letter prefabs and the fonts (for working on the mod).
"""
import os
os.environ.setdefault("TLD_TEST_MODS", "RoadEnd")
import argparse, datetime, json, math, pathlib, shutil, sys, time

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
    try:
        return b(cmd)
    except RuntimeError as e:
        if not str(e).endswith(": "):
            raise
        return {}


def camera_shot(b, out, label, cam, look, fov=60):
    """A still from a free camera at cam looking at look (global x, y, z): yaw 0 looks along +z, pitch down is positive."""
    dx, dy, dz = look[0] - cam[0], look[1] - cam[1], look[2] - cam[2]
    yaw = math.degrees(math.atan2(dx, dz))
    pitch = -math.degrees(math.atan2(dy, math.hypot(dx, dz)))
    b("cinema free")
    b(f"cinema fov {fov}")
    take = "version 1\\nmove %.3f %.3f %.3f %.2f %.2f %d" % (*cam, yaw, pitch, fov)
    shot_cmd(b, "cinema shot load " + take)
    time.sleep(2.5)
    p = shot(b, out, label)
    shot_cmd(b, "cinema shot unload")
    b("cinema off")
    return p


def probe(b, what, *args):
    return b("call RoadEnd.Probe." + " ".join([what, *map(str, args)]))["value"]


def state(b):
    return json.loads(probe(b, "State"))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seed", type=int, default=game.DEFAULT_SEED)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--explore", action="store_true")
    ap.add_argument("--out")
    args = ap.parse_args()
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = pathlib.Path(args.out or ROOT / "bench" / f"roadend-{stamp}")
    out.mkdir(parents=True, exist_ok=True)
    result = {"seed": args.seed, "checks": {}}
    checks = result["checks"]
    if not args.no_build:
        game.build()
    with game.ProtectedSession():
        try:
            game.launch()
            game.newgame(args.seed)
            b = game.Bridge()
            b("set TLDRevamp.TestSafety.BlockAutoSave True")
            b("overlay off")
            if args.explore:
                for what in ("Generators", "Start", "Prefab 18", "Prefab item:90"):
                    v = probe(b, *what.split())
                    result[what] = v
                    print(f"--- {what}: {v}", flush=True)
                result["fonts"] = b("call RoadEnd.NoteText.Fonts")["value"]
                print("--- fonts:", result["fonts"], flush=True)
            s = state(b)
            print("start:", s, flush=True)
            sign = s["sign"]
            # 1. go to the end line's chunk border so both road chunks there are made
            cut = s["cut"]
            z = sign * (cut - 400)
            b(f"tpg 0 {z:.0f} hold"); time.sleep(2); b(f"tpground 0 {z:.0f}")
            t0 = time.monotonic()
            while time.monotonic() - t0 < 90:
                s = state(b)
                if s["cutKnown"] and s["roadEnd"] and s["grave"]: break
                time.sleep(2)
            print(f"after {time.monotonic() - t0:.0f} s:", s, flush=True)
            checks["end known"] = bool(s["cutKnown"])
            checks["road end found"] = s["roadEnd"] is not None
            if not s["roadEnd"]:
                return finish(result, out)
            end = s["roadEnd"]
            # 2. stand on the road 40 m before its end, facing it; wait for the place to fill in
            dx, dz = 0.0, float(sign)
            px, pz = end[0] - dx * 40, end[2] - dz * 40
            b(f"tpground {px:.1f} {pz:.1f}")
            time.sleep(12)
            s = state(b)
            result["state"] = s
            print("at the end:", s, flush=True)
            past = s["past"]
            checks["no main road past the end"] = past["mainRoad"] == 0
            checks["the land goes on past the end"] = past["tiles"] > 0
            checks["no building next to the grave"] = s["buildingsNearGrave"] == 0
            checks["grave placed"] = s["grave"] is not None and math.dist(s["grave"][::2], end[::2]) < 15
            checks["note placed"] = s["note"] is not None and math.dist(s["note"][::2], end[::2]) < 10
            gy = (s["grave"] or end)[1]
            # 3. pictures: the end of the road with the grave, the note from above, the edge from the air
            n = s["note"]
            result["shots"] = [
                camera_shot(b, out, "end-of-road", (end[0] - dx * 16, end[1] + 1.8, end[2] - dz * 16), (end[0] + dx * 6, gy + 1.6, end[2] + dz * 6)),
                camera_shot(b, out, "note", (n[0] - dx * 0.8, n[1] + 1.3, n[2] - dz * 0.8), tuple(n), fov=50),
            ]
            time.sleep(15)   # the land around the camera's next spot finishes generating
            result["shots"].append(camera_shot(b, out, "end-from-air", (end[0] - dx * 150 + 40, end[1] + 70, end[2] - dz * 150), (end[0], end[1], end[2] + dz * 40)))
            result["errors"] = b("errors")
        finally:
            if game.running():
                game.quit_game()
            p = tldenv.GAME_DIR / "BepInEx/LogOutput.log"
            if p.exists():
                shutil.copy2(p, out / "bep.log")
    return finish(result, out)


def finish(result, out):
    ok = all(result["checks"].values()) and result["checks"]
    result["verdict"] = "PASS" if ok else "FAIL"
    (out / "result.json").write_text(json.dumps(result, indent=2))
    for k, v in result["checks"].items():
        print(f"{'PASS' if v else 'FAIL'}  {k}")
    print(f"{result['verdict']} -> {out}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
