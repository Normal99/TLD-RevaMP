#!/usr/bin/env python3
"""Validate the local evidence for the open issue ledger (docs/ISSUE-ACCEPTANCE.md).

  python3 tools/issue_evidence.py                 # the matrix for every tracked issue
  python3 tools/issue_evidence.py --issue 31     # one issue in detail
  python3 tools/issue_evidence.py --json          # machine-readable output
  python3 tools/issue_evidence.py --strict        # exit 1 while any gate is missing

This tool only reads the working tree. It never calls GitHub, never writes an issue, never
labels, never comments and never pushes. Issue titles and bodies were fetched once by a
maintainer and are recorded in this file as the ledger's source of truth, so the validator
works offline and in CI without a token.

Three states are reported for every gate, and they are kept apart on purpose:

  committed   the file is in HEAD (the dev baseline) and unchanged in the working tree
  patched     the file exists in the working tree but is not in HEAD (an uncommitted
              worker snapshot), or it is in HEAD and also modified (a local patch)
  missing     the file is nowhere: the gate still needs work

`account` gates are the ones a person must do on an external account (a NexusMods
submission, a headset test). The validator can only report that they are still open.
"""
import argparse
import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
LEDGER = pathlib.Path("docs/ISSUE-ACCEPTANCE.md")

# The open issues this ledger covers. #35 (tldrevamp.com with submission forms) is
# deliberately out of scope: it needs a website and a domain, not a code change.
TRACKED = (31, 32, 33, 34, 36, 37, 38, 39, 40, 41, 42)
EXCLUDED = (35,)

# The issue bodies as fetched from Normal99/TLD-RevaMP on 2026-10-20. They are recorded
# here so the validator is offline; re-fetch them with `gh issue view N` when refreshing.
ISSUE_BODIES = {
    31: ("Player Ragdoll sync",
         "Player ragdoll sync where when you die a ragdoll shows needs to be synced better. "
         "Items need to be dropped where you have died not where you flung off to with your ragdoll."),
    32: ("TerrainGeneration mod merged into RevaMP as a added feature.",
         "It also has to have aspects of it fixed and tested."),
    33: ("Make it easier for TLDloader devs to port over mods.",
         "Self explanatory."),
    34: ("Multiplayer settings",
         "A proper settings menu for the multiplayer session inside of the multiplayer menu screen. "
         "Here TPA can be set among other options like spawns etc. "
         "Cheats/dev menu can also be disabled or enabled here. "
         "More options can be added in the long run too."),
    36: ("More filmmaking tools",
         "More tools to make videos and other content easily sort of like Replay Mod on Minecraft "
         "or just more camera options. This can be a main feature on RevaMP or a standalone mod "
         "with revamp API."),
    37: ("Render terrain further",
         "Terrain doesn't render that far what is seen by the player is \"Distant terrain\" this "
         "results in floating roads at crossroads or roads going into the ground at close view. "
         "This would also require optimization if possible so that it can be used without a "
         "drastic performance hit."),
    38: ("RevaMP on other platforms",
         "Uploading RevaMP and the other mods on NexusMods with included instructions and other help. "
         "Users will be directed to github for any issues or serious posts."),
    39: ("Mod disable/enable",
         "Disable/enable mods from the mod browser without uninstalling them."),
    40: ("Add WIP watermark",
         "Add WIP wateramrks similar to My Summer Car mod WreckMP. This way videos showcasing "
         "the mod will clearly show that it is WIP. "
         "Decision has to be made if this will be a thing on all branches or not."),
    41: ("VR Rewrite",
         "VR support in The Long Drive is a buggy mess we have to rewrite it and make it usable "
         "and playable with. It should also be fully multiplayer compatible, optimized and "
         "efficiently."),
    42: ("No Clank README",
         "Human README for humans. Things will be dumbed down and much better then."),
}

# Ownership. "repo" is anything a contributor can land in this repository; "account" is a
# step on an external account that no code change can satisfy.
OWNER = {
    31: "repo", 32: "repo", 33: "repo", 34: "repo", 36: "repo", 37: "repo",
    38: "account", 39: "repo", 40: "repo", 41: "repo", 42: "account",
}

# Per issue: the gates that must be satisfied before the issue can be called done.
#   kind     "file"  a path in the working tree that must exist
#            "test"  an offline command that must succeed
#            "manual" a human/headset/two-machine step; the validator reports it as open
#   state    "committed"  expected in HEAD
#            "patched"    expected only as a working-tree change
#            "either"     either state is acceptable
GATES = {
    31: [
        {"id": "anchor", "kind": "file", "path": "src/TLDRevamp/Net/SessionRules.Death.cs", "state": "either",
         "note": "death anchor: the accepted Death() pose is recorded and the drops use it"},
        {"id": "corpse", "kind": "file", "path": "src/TLDRevamp/Net/Entities.Death.cs", "state": "either",
         "note": "the game's own PlayerRagdol is shared, so the host sees the real corpse"},
        {"id": "twomachine", "kind": "manual", "note": "tools/deathsync.py --ab on two machines over the relay"},
    ],
    32: [
        {"id": "terrainfixes", "kind": "file", "path": "src/TLDRevamp/Fixes/TerrainSync.cs", "state": "committed",
         "note": "terrain rebuilt when a road or building appears next to a tile"},
        {"id": "async", "kind": "file", "path": "src/TLDRevamp/Fixes/AsyncTerrain.cs", "state": "committed",
         "note": "tile building moved off the main thread"},
        {"id": "poi", "kind": "test", "path": "tools/unit/PoiBacklogTest.csproj", "state": "committed",
         "note": "POI/road backlog ordering is covered offline"},
    ],
    33: [
        {"id": "shim", "kind": "file", "path": "src/TLDLoaderShim/TLDLoader.cs", "state": "committed",
         "note": "the shim whose API the scanner and the ported mods target"},
        {"id": "scanner", "kind": "file", "path": "src/TLDRevamp/LoaderScan.cs", "state": "either",
         "note": "the scanner reports lifecycle, dependencies, type-load errors and member sites"},
        {"id": "scanner-cli", "kind": "file", "path": "tools/modscan/Program.cs", "state": "either",
         "note": "the scanner runs without starting the game"},
        {"id": "docs", "kind": "file", "path": "docs/MODDING-MULTIPLAYER.md", "state": "either",
         "note": "section 7 explains how to port a mod"},
    ],
    34: [
        {"id": "snapshot", "kind": "file", "path": "src/TLDRevamp/Net/SessionSettings.cs", "state": "either",
         "note": "the host owns one validated settings snapshot"},
        {"id": "wire", "kind": "file", "path": "src/TLDRevamp/Net/SessionSettingsData.cs", "state": "either",
         "note": "the wire format (no password value on the wire)"},
        {"id": "panel", "kind": "file", "path": "src/TLDRevamp/Net/MpScreen.cs", "state": "either",
         "note": "the session settings panel inside the multiplayer menu"},
        {"id": "offline", "kind": "test", "path": "tools/unit/SessionSettingsTest.csproj", "state": "either",
         "note": "round trip, schema, revision and policy matrix are covered offline"},
        {"id": "twomachine", "kind": "manual", "note": "host changes a setting, the joiner sees it read-only"},
    ],
    36: [
        {"id": "camera", "kind": "file", "path": "src/TLDRevamp/CinematicCamera.cs", "state": "either",
         "paths": ["src/TLDRevamp/CinematicCamera.cs", "src/TLDRevamp/Cinematic", "src/TLDRevamp/Fixes/CinematicCamera.cs"],
         "note": "the free/orbit camera and its lifecycle restore"},
        {"id": "twomachine", "kind": "manual", "note": "the camera changes nothing for the other player"},
    ],
    37: [
        {"id": "horizon", "kind": "file", "path": "src/TLDRevamp/Fixes/TerrainHorizon.cs", "state": "either",
         "note": "the far mesh is pulled onto the road and distant roads are drawn as ribbons"},
        {"id": "lab", "kind": "file", "path": "src/TLDRevamp/Fixes/TerrainHorizonLab.cs", "state": "either",
         "note": "the read-only horizon snapshot the comparison tool reads"},
        {"id": "tool", "kind": "tool", "path": "tools/terrainhorizon.py", "state": "either",
         "note": "the before/after coverage comparison"},
        {"id": "offline", "kind": "test", "path": "tools/unit/TerrainHorizonTest.csproj", "state": "either",
         "note": "the ribbon geometry and its rejections are covered offline"},
        {"id": "perf", "kind": "manual", "note": "frame timing and memory with the setting on and off"},
    ],
    38: [
        {"id": "install-doc", "kind": "file", "path": "packaging/INSTALL.txt", "state": "committed",
         "note": "the instructions that travel with the submission"},
        {"id": "nexus-page", "kind": "manual", "note": "the NexusMods page, uploaded by the account owner"},
    ],
    39: [
        {"id": "activation", "kind": "file", "path": "src/TLDRevamp/Net/ModActivation.cs", "state": "committed",
         "note": "the queued move and the intent receipt"},
        {"id": "preloader", "kind": "file", "path": "src/TLDRevamp.Preloader/ModActions.cs", "state": "committed",
         "note": "the preloader applies the queued move before the plugins load"},
        {"id": "offline", "kind": "test", "path": "tools/unit/ModActivationTest.csproj", "state": "committed",
         "note": "the crash-in-the-middle and protected-id cases are covered offline"},
        {"id": "in-game", "kind": "manual", "note": "disable, restart, enable, restart in the real game"},
    ],
    40: [
        {"id": "overlay", "kind": "file", "path": "src/TLDRevamp/Overlay.cs", "state": "either",
         "note": "the mark is drawn independently of the performance overlay"},
        {"id": "setting", "kind": "file", "path": "src/TLDRevamp/RevampSettings.cs", "state": "either",
         "note": "the toggle under Settings -> Revamp -> Branding"},
        {"id": "screenshot", "kind": "manual", "note": "a screenshot with the mark on and one with it off"},
    ],
    41: [
        {"id": "openxr", "kind": "file", "path": "src/TLDRevamp/Fixes/OpenXrBridge.cs", "state": "either",
         "note": "the OpenXR loader boundary with a fallback to the legacy path"},
        {"id": "tool", "kind": "tool", "path": "tools/quest3vr.py", "state": "either",
         "note": "the repeatable headset check (setup, health, recovery)"},
        {"id": "libs", "kind": "file", "path": "tools/libs.sh", "state": "either",
         "note": "the OpenXR assemblies are copied when the game has them"},
        {"id": "headset", "kind": "manual", "note": "a Quest 3 session, with the comfort and visual check"},
    ],
    42: [
        {"id": "readme", "kind": "file", "path": "README.md", "state": "either",
         "paths": ["README.md", "docs/HUMAN-README.md", "docs/README.md"],
         "note": "the human README (the repository README is currently the technical one)"},
        {"id": "account", "kind": "manual", "note": "the decision on where the human README is published"},
    ],
}


def git(*args):
    """Run a git command in the repository and return its stdout, or '' on failure."""
    try:
        result = subprocess.run(["git", "-C", str(ROOT)] + list(args), capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutError):
        return ""
    return result.stdout if result.returncode == 0 else ""


def git_ok(*args):
    """True when the git command succeeds. `cat-file -e` prints nothing, so the exit code is the answer."""
    try:
        result = subprocess.run(["git", "-C", str(ROOT)] + list(args), capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutError):
        return False
    return result.returncode == 0


def in_head(path):
    """True when the path is tracked in HEAD (the committed dev baseline)."""
    return git_ok("cat-file", "-e", "HEAD:" + path)


def is_modified(path):
    """True when the path is in HEAD but the working tree copy differs."""
    out = git("status", "--porcelain", "--", path)
    return any(line[:2].strip() in ("M", "A", "D", "R", "C", "T", "U") for line in out.splitlines())


def is_untracked(path):
    out = git("status", "--porcelain", "--untracked-files=all", "--", path)
    return any(line.startswith("??") for line in out.splitlines())


def file_state(path):
    """committed | patched | missing for one path, relative to the working tree.

    A directory counts as the state of its contents: committed when every file in it is
    tracked in HEAD, patched when any file in it is new or locally modified.
    """
    target = ROOT / path
    if not target.exists():
        return "missing"
    if target.is_dir():
        files = [f for f in sorted(target.rglob("*")) if f.is_file() and "__pycache__" not in f.parts]
        if not files:
            return "missing"
        for f in files:
            rel = f.relative_to(ROOT).as_posix()
            if is_untracked(rel) or is_modified(rel):
                return "patched"
        return "committed" if all(in_head(f.relative_to(ROOT).as_posix()) for f in files) else "patched"
    if is_untracked(path):
        return "patched"
    if is_modified(path):
        return "patched"
    if in_head(path):
        return "committed"
    return "patched"


def check_test(path):
    """Run one offline unit project. Returns (state, detail)."""
    project = ROOT / path
    if not project.exists():
        return "missing", "no such project"
    name = project.stem
    try:
        result = subprocess.run(
            ["dotnet", "run", "--project", str(project), "--configuration", "Release", "--disable-build-servers",
             "--property:BaseIntermediateOutputPath=obj/" + name + "/", "--property:OutputPath=bin/" + name + "/"],
            capture_output=True, text=True, timeout=900, cwd=str(ROOT))
    except (OSError, subprocess.TimeoutExpired) as error:
        return "missing", type(error).__name__
    if result.returncode == 0:
        return "committed", "passed"
    return "missing", "failed"


def check_tool(path):
    """Compile one Python tool the way CI does. Returns (state, detail)."""
    target = ROOT / path
    if not target.exists():
        return "missing", "no such tool"
    try:
        result = subprocess.run([sys.executable, "-c",
                                 "compile(open(%r, encoding='utf-8').read(), %r, 'exec')" % (str(target), str(target))],
                                capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutError) as error:
        return "missing", type(error).__name__
    if result.returncode == 0:
        return "committed", "compiles"
    return "missing", "does not compile"


def ledger_sections():
    """Split the ledger into per-issue sections. A section starts at a '## #N' heading."""
    if not (ROOT / LEDGER).exists():
        return {}
    text = (ROOT / LEDGER).read_text(encoding="utf-8", errors="replace")
    sections, current, buffer = {}, None, []
    for line in text.splitlines():
        match = re.match(r"^#{1,3}\s*#?(\d{1,3})\b", line)
        if match:
            if current is not None:
                sections[current] = "\n".join(buffer)
            current = int(match.group(1))
            buffer = [line]
        elif current is not None:
            buffer.append(line)
    if current is not None:
        sections[current] = "\n".join(buffer)
    return sections


def ledger_has(issue, gate_id):
    """True when this issue's own section of the ledger names the gate."""
    section = ledger_sections().get(issue)
    if section is None:
        return False
    return bool(re.search(r"(?<![A-Za-z0-9_])" + re.escape(gate_id) + r"(?![A-Za-z0-9_])", section))


def evaluate(issue, run_tests=False):
    """Evaluate one issue's gates. Returns a list of gate result dictionaries."""
    title, body = ISSUE_BODIES.get(issue, ("", ""))
    results = []
    for gate in GATES.get(issue, []):
        kind = gate["kind"]
        state, detail = "open", "needs a person"
        if kind in ("file", "test", "tool"):
            candidates = gate.get("paths") or [gate["path"]]
            chosen, state = None, "missing"
            for candidate in candidates:
                found = file_state(candidate)
                if found != "missing":
                    chosen, state = candidate, found
                    break
            chosen_path = chosen or (candidates[0] if candidates else "")
            if chosen is None:
                detail = "not present"
            else:
                if state == "committed":
                    detail = "in HEAD"
                elif state == "patched":
                    detail = "working tree only"
                else:
                    detail = "not present"
                if kind == "test" and run_tests and state != "missing":
                    _, outcome = check_test(chosen_path)
                    detail += ", " + outcome
                elif kind == "tool" and chosen is not None:
                    _, outcome = check_tool(chosen_path)
                    detail += ", " + outcome
        entry = {
            "issue": issue,
            "title": title,
            "gate": gate["id"],
            "kind": kind,
            "state": state,
            "detail": detail,
            "note": gate.get("note", ""),
            "path": chosen_path,
            "owner": OWNER.get(issue, "repo"),
            "in_ledger": ledger_has(issue, gate["id"]),
        }
        results.append(entry)
    return results


def summarise(entries):
    """Count the gate states for one issue."""
    counts = {"committed": 0, "patched": 0, "missing": 0, "open": 0}
    for entry in entries:
        counts[entry["state"]] = counts.get(entry["state"], 0) + 1
    return counts


def print_matrix(entries, wide=False):
    """Print the matrix. No colours: the output is read in a terminal and in CI logs."""
    header = "%-5s %-34s %-9s %-9s %-9s %s" % ("issue", "gate", "state", "owner", "ledger", "path")
    print(header)
    print("-" * len(header))
    for entry in entries:
        print("%-5s %-34s %-9s %-9s %-9s %s" % (
            "#%d" % entry["issue"], entry["gate"][:34], entry["state"], entry["owner"],
            "yes" if entry["in_ledger"] else "no", entry["path"]))
    print()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--issue", type=int, help="only this issue number")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    parser.add_argument("--run-tests", action="store_true", help="run the offline unit projects (slow)")
    parser.add_argument("--strict", action="store_true", help="exit 1 while a gate is missing or open")
    parser.add_argument("--list", action="store_true", help="print the issue titles and bodies and exit")
    args = parser.parse_args()

    if args.list:
        for issue in TRACKED:
            title, body = ISSUE_BODIES[issue]
            print("#%d %s" % (issue, title))
            print(body)
            print()
        for issue in EXCLUDED:
            title, body = ISSUE_BODIES.get(issue, ("", ""))
            print("#%d %s (excluded: needs a website and a domain)" % (issue, title))
        return 0

    issues = TRACKED if args.issue is None else (args.issue,)
    entries = []
    for issue in issues:
        entries.extend(evaluate(issue, run_tests=args.run_tests))

    if args.json:
        payload = {
            "repository": "Normal99/TLD-RevaMP",
            "ledger": str(LEDGER),
            "ledger_present": (ROOT / LEDGER).exists(),
            "issues": list(issues),
            "excluded": list(EXCLUDED),
            "gates": entries,
            "summary": {str(i): summarise([e for e in entries if e["issue"] == i]) for i in issues},
        }
        print(json.dumps(payload, indent=1))
        return 0

    if args.issue is not None:
        for issue in issues:
            group = [e for e in entries if e["issue"] == issue]
            if not group:
                continue
            print("#%d %s" % (issue, group[0]["title"]))
            print("  %-12s %-9s %-9s %-7s %s" % ("gate", "state", "owner", "ledger", "note"))
            for entry in group:
                print("  %-12s %-9s %-9s %-7s %s" % (entry["gate"], entry["state"], entry["owner"],
                                                    "yes" if entry["in_ledger"] else "no", entry["note"]))
            print()
    else:
        print_matrix(entries)

    print("legend: committed = in HEAD and unchanged; patched = working tree only or locally modified;")
    print("        missing = not present; open = a human, headset, account or two-machine step.")
    print("The tool only reads the working tree. It never contacts GitHub and never changes an issue.")
    if args.strict and any(e["state"] in ("missing", "open") for e in entries):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
