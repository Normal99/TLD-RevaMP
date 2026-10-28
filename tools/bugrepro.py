#!/usr/bin/env python3
"""Unpack a bug bundle (TLDRevamp-bug-*.zip), check its recording and set up the replay. Never launches a game.

  python tools/bugrepro.py TLDRevamp-bug-20261028-120000.zip --validate
  python tools/bugrepro.py TLDRevamp-bug-20261028-120000.zip --install "<a test copy's Saves folder>"

The bundle is made in the game with Ctrl+Insert (or /bug note); docs/BUG-REPORTS.md says what is in it.
--install copies the starting world into that folder as the save "bugrepro" (load it in a test copy, then replay).
Use a disposable test copy's folder, never a personal save folder.
"""
import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
SAVE_NAME = "bugrepro"


def unpack(bundle, out):
    """Extract without trusting the names: nothing leaves `out`, links and odd names are refused."""
    out = pathlib.Path(out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(bundle) as z:
        for info in z.infolist():
            name = info.filename
            if name.startswith("/") or "\\" in name or ":" in name or any(p in ("", "..", ".") for p in name.rstrip("/").split("/")):
                raise ValueError("refusing zip entry name: %r" % name)
            if (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("refusing a link entry: %r" % name)
            target = (out / name).resolve()
            if out not in target.parents and target != out:
                raise ValueError("refusing zip entry outside the folder: %r" % name)
        z.extractall(out)
    return out


def world_files(folder):
    d = pathlib.Path(folder) / "world" / "start"
    return sorted(p for p in d.glob("*") if p.is_file()) if d.is_dir() else []


def installed_name(source):
    """TLDsaveData_<x>.tlds -> TLDsaveData_bugrepro.tlds, and the files that follow it (.leases, TLDsaveProp_<x>.tldsp)."""
    n = source.name
    if n.startswith("TLDsaveData_"):
        rest = n[len("TLDsaveData_"):]
        suffix = rest[rest.index(".tlds"):] if ".tlds" in rest else None
        return "TLDsaveData_%s%s" % (SAVE_NAME, suffix) if suffix else None
    if n.startswith("TLDsaveProp_") and n.endswith(".tldsp"):
        return "TLDsaveProp_%s.tldsp" % SAVE_NAME
    return None


def install(folder, saves):
    saves = pathlib.Path(saves)
    if not saves.is_dir():
        raise ValueError("not a folder: %s" % saves)
    copied = []
    for src in world_files(folder):
        name = installed_name(src)
        if name:
            shutil.copy2(src, saves / name)
            copied.append(name)
    if not any(n.startswith("TLDsaveData_") and n.endswith(".tlds") for n in copied):
        raise ValueError("the bundle has no starting world (a client's bundle, or a new world that was never saved)")
    return copied


def unpack_car(f):
    """The car state packed into a driving row's f column (src ReproCarState): the same bits, low to high."""
    f = int(f)
    return {"engine": bool(f & 1), "kinematic": bool(f & 2), "asleep": bool(f & 4), "driving": bool(f & 8), "handbrake_latched": bool(f & 16),
            "grounded_driven": f >> 5 & 15, "driven_wheels": f >> 9 & 15, "gear": (f >> 13 & 15) - 1, "clutch_pct": f >> 17 & 127,
            "handbrake_pct": f >> 24 & 127, "motor_torque": bool(f >> 31 & 1), "brake_torque": bool(f >> 32 & 1)}


def drive_report(repro, still=0.5):
    """Why a car did not move: the driving rows where the throttle was down and the car stood still, and what the car was doing
    in them. Returns text lines. Recordings made before the car state was recorded have none."""
    cols = repro["cols"]
    t, kind, x, g, h, f = (cols.index(c) for c in ("t", "kind", "x", "g", "h", "f"))
    drive = [r for r in repro["rows"] if r[kind] == 26]
    if not drive:
        return ["no driving rows"]
    if all(r[f] == 0 and r[g] == 0 and r[h] == 0 for r in drive):
        return ["%d driving rows, but no car state in them (recorded by a build before the car state was recorded): only the inputs are known" % len(drive)]
    stuck = [r for r in drive if r[x] > 0.5 and r[h] < still]
    moving = [r for r in drive if r[x] > 0.5 and r[h] >= still]
    lines = ["%d driving samples: %d with the throttle past half and the car still (speed < %s), %d with it moving" % (len(drive), len(stuck), still, len(moving))]
    if not stuck:
        lines.append("the car moved whenever the throttle was down")
        return lines
    checks = [("engine not running", lambda c: not c["engine"]), ("clutch applied (> 50%)", lambda c: c["clutch_pct"] > 50), ("in neutral", lambda c: c["gear"] == 0),
              ("handbrake applied (> 20%) or latched", lambda c: c["handbrake_pct"] > 20 or c["handbrake_latched"]), ("body kinematic (frozen)", lambda c: c["kinematic"]),
              ("body asleep", lambda c: c["asleep"]), ("no driven wheel on the ground", lambda c: c["driven_wheels"] > 0 and c["grounded_driven"] == 0),
              ("no motor torque on the driven wheels", lambda c: not c["motor_torque"]), ("brake torque on a driven wheel", lambda c: c["brake_torque"]),
              ("car.driving false", lambda c: not c["driving"])]
    cars = [unpack_car(r[f]) for r in stuck]
    movingcars = [unpack_car(r[f]) for r in moving]
    lines.append("while the throttle was down and the car still (compared with the same checks while it moved):")
    for name, test in checks:
        share = sum(1 for c in cars if test(c)) / len(cars)
        other = (sum(1 for c in movingcars if test(c)) / len(movingcars)) if movingcars else None
        lines.append("  %-42s %4.0f%%%s" % (name, share * 100, "" if other is None else "   (moving: %.0f%%)" % (other * 100)))
    rpm = sorted(r[g] for r in stuck)
    lines.append("  engine rpm while still: median %.0f" % rpm[len(rpm) // 2])
    return lines


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--out", help="folder to unpack into (default: next to the zip, named after it)")
    ap.add_argument("--validate", action="store_true", help="check repro.json with tools/replay_repro.py --validate-only")
    ap.add_argument("--drive", action="store_true", help="say what the car was doing while the throttle was down and it did not move")
    ap.add_argument("--install", metavar="SAVES_FOLDER", help="copy the starting world there as the save %r" % SAVE_NAME)
    args = ap.parse_args()
    bundle = pathlib.Path(args.bundle)
    out = pathlib.Path(args.out) if args.out else bundle.with_suffix("")
    folder = unpack(bundle, out)
    print("unpacked to", folder)
    report = folder / "REPORT.md"
    if report.is_file():
        print("\n" + report.read_text(encoding="utf-8"))
    repro = folder / "repro.json"
    code = 0
    if args.validate:
        if not repro.is_file():
            print("no repro.json: the action recorder was not running, nothing to replay")
            code = 1
        else:
            code = subprocess.call([sys.executable, "-I", str(HERE / "replay_repro.py"), str(repro), "--validate-only"])
    if args.drive:
        if not repro.is_file():
            print("no repro.json: nothing to read")
        else:
            print("\ndriving:")
            for line in drive_report(json.loads(repro.read_text(encoding="utf-8"))):
                print("  " + line)
    if args.install:
        names = install(folder, args.install)
        print("installed the starting world as %r (%d files); load it in the test copy, then replay" % (SAVE_NAME, len(names)))
    if repro.is_file():
        print("\nreplay, with the world loaded and hosted in one test copy, a second joined (see docs/REPRO-FORMAT.md, Running a check):")
        print("  python tools/replay_repro.py %s --actor-port 27060 --observer-port 27061" % repro)
    return code


if __name__ == "__main__":
    sys.exit(main())
