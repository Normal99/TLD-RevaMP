#!/usr/bin/env python3
"""In-game check of the bug report (Ctrl+Insert / /bug / Settings button) on one isolated test copy.

  TLD_DIR=<copy>/game TLD_TEST_COMPAT=<copy>/compat TLD_BRIDGE_PORT=<port> python3 tools/bugreport_check.py run

Launches the copy, starts a new world, saves it, loads that save and hosts it (so the session starts from a save file),
then checks that: the action recorder started by itself with its banner; a report taken through the note box and one taken
directly each give a bundle with REPORT.md, repro.json and the starting world; the banner shows saving and then saved; the
shared report zip holds none of that; tools/bugrepro.py validates and installs the bundle. Screenshots go to the copy's
BepInEx/tldrevamp-evidence. Prints PASS/FAIL per step; exit code 1 on a failure.
"""
import json
import pathlib
import subprocess
import sys
import tempfile
import time
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
FAILS = []


def check(ok, what, detail=""):
    print(("PASS " if ok else "FAIL ") + what + (f" ({detail})" if detail else ""), flush=True)
    if not ok:
        FAILS.append(what)
    return ok


def bug(b, arg="status"):
    return b("bug " + arg)


def wait_phase(b, phases, timeout=60):
    t0 = time.time()
    seen = []
    while time.time() - t0 < timeout:
        r = bug(b)
        if not seen or seen[-1] != (r["phase"], r.get("banner")):
            seen.append((r["phase"], r.get("banner")))
        if r["phase"] in phases:
            return r, seen
        time.sleep(0.25)
    return bug(b), seen


def shot(b):
    try:
        print("     screenshot:", b("screenshot").get("path"))
    except Exception as e:  # a missing screenshot is evidence lost, not a failure of the report
        print("     screenshot failed:", e)


def feedback_dir():
    import tldenv
    return tldenv.GAME_DIR / "BepInEx" / "tldrevamp-feedback"


def newest(pattern, after):
    files = [p for p in feedback_dir().glob(pattern) if p.stat().st_mtime >= after]
    return max(files, key=lambda p: p.stat().st_mtime) if files else None


def verify_bundle(path, note):
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        report = z.read("REPORT.md").decode("utf-8") if "REPORT.md" in names else ""
        check("REPORT.md" in names and "repro.json" in names, "the bundle has REPORT.md and repro.json", str(names[:12]))
        check(note in report, "REPORT.md carries the note")
        check(any(n.startswith("world/start/TLDsaveData_") and n.endswith(".tlds") for n in names), "the bundle holds the starting world", str([n for n in names if n.startswith("world")]))
        # the lease table only exists when the world was saved inside a session; this check saves before hosting
        print("     lease table in the bundle:", any(n.endswith(".tlds.leases") for n in names))
        check(not any(".players" in n for n in names), "no player records in the bundle")
        check("blackbox.json" in names and "stats.json" in names, "the usual report data is in the bundle")
        if "repro.json" in names:
            repro = json.loads(z.read("repro.json"))
            kinds = {}
            for row in repro["rows"]:
                kinds[row[2]] = kinds.get(row[2], 0) + 1
            check(repro["complete"] is True and kinds.get(1, 0) >= 20, "repro.json is complete with pose samples", f"{len(repro['rows'])} rows, poses {kinds.get(1, 0)}")
        print("     REPORT.md:\n" + "\n".join("       " + l for l in report.splitlines()[:14]))




def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd != "run":
        print(__doc__)
        return 2
    import game
    from bench import Bridge
    with game.ProtectedSession():
        game.launch()
        game.newgame(20261028)
        b = Bridge()
        try:
            run_session(b)
        finally:
            game.quit_game()
    print(("FAILED: " + "; ".join(FAILS)) if FAILS else "ALL PASS")
    return 1 if FAILS else 0


def run_session(b):
    start = time.time()
    b("errors reset")
    check(bug(b)["auto"] is True, "the action recorder is on by default")
    r = b("mp save")
    check("error" not in r, "the new world is saved", str(r))
    time.sleep(2)
    import game
    name = "autosave1"
    b2 = game.loadsave(name)
    b = b2
    r = b("mp host 27070")
    time.sleep(4)
    r = bug(b)
    for _ in range(20):
        if r["recording"]:
            break
        time.sleep(1); r = bug(b)
    check(r["recording"] is True, "recording started by itself when the session started", str(r))
    check(bool(r["startWorld"]) and "autosave1" in r["startWorld"], "the starting world file is known", r.get("startWorld"))
    # the recording banner itself is drawn by ReproRecorder (`bug status` reports only the report banner): look at the screenshot
    shot(b)
    time.sleep(6)

    # 1: the Ctrl+Insert path (note box), then the direct one
    bug(b, "panel")
    check(bug(b).get("panel") is True, "the note box opens in bug mode")
    shot(b)
    b("markpanel save barrel fell through the floor")
    r, seen = wait_phase(b, ("Saved", "Failed"))
    print("     banner states:", [s[1] for s in seen])
    check(r["phase"] == "Saved", "the report from the note box is saved", str(r))
    check(any(s[0] == "Saving" and s[1] and "saving" in s[1] for s in seen), "the banner showed saving")
    check(r.get("banner") is not None and "saved" in r["banner"], "the banner shows saved", str(r.get("banner")))
    shot(b)
    bundle = newest("TLDRevamp-bug-*.zip", start)
    check(bundle is not None, "a bundle zip exists")
    if bundle:
        verify_bundle(bundle, "barrel fell through the floor")
    shared = newest("TLDRevamp-report-*.zip", start)
    if shared:
        with zipfile.ZipFile(shared) as z:
            bad = [n for n in z.namelist() if n in ("REPORT.md", "repro.json") or n.startswith("world")]
            check(not bad, "the shared report zip has none of the bundle's extras", str(bad))
    else:
        check(False, "the normal report zip was made")

    time.sleep(1)
    bug(b, "report second one")
    r, _ = wait_phase(b, ("Saved", "Failed"))
    check(r["phase"] == "Saved", "a second report works right after", str(r))
    errs = b("errors")
    check(errs.get("exceptions", 0) == 0, "no exceptions in the game's log", str(errs.get("exceptions")))

    if bundle:
        tools = pathlib.Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory() as d:
            saves = pathlib.Path(d) / "Saves"; saves.mkdir()
            p = subprocess.run([sys.executable, "-I", str(tools / "bugrepro.py"), str(bundle), "--out", str(pathlib.Path(d) / "out"), "--validate", "--install", str(saves)],
                               capture_output=True, text=True)
            check(p.returncode == 0, "bugrepro.py validates the recording and installs the world", (p.stdout + p.stderr)[-300:])
            check((saves / "TLDsaveData_bugrepro.tlds").is_file(), "the world is installed as bugrepro")


if __name__ == "__main__":
    sys.exit(main())
