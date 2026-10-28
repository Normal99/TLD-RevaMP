#!/usr/bin/env python3
"""Unpack a bug bundle (TLDRevamp-bug-*.zip), check its recording and set up the replay. Never launches a game.

  python tools/bugrepro.py TLDRevamp-bug-20261028-120000.zip --validate
  python tools/bugrepro.py TLDRevamp-bug-20261028-120000.zip --install "<a test copy's Saves folder>"

The bundle is made in the game with Ctrl+Insert (or /bug note); docs/BUG-REPORTS.md says what is in it.
--install copies the starting world into that folder as the save "bugrepro" (load it in a test copy, then replay).
Use a disposable test copy's folder, never a personal save folder.
"""
import argparse
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


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("--out", help="folder to unpack into (default: next to the zip, named after it)")
    ap.add_argument("--validate", action="store_true", help="check repro.json with tools/replay_repro.py --validate-only")
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
    if args.install:
        names = install(folder, args.install)
        print("installed the starting world as %r (%d files); load it in the test copy, then replay" % (SAVE_NAME, len(names)))
    if repro.is_file():
        print("\nreplay, with the world loaded and hosted in one test copy, a second joined (see docs/REPRO-FORMAT.md, Running a check):")
        print("  python tools/replay_repro.py %s --actor-port 27060 --observer-port 27061" % repro)
    return code


if __name__ == "__main__":
    sys.exit(main())
