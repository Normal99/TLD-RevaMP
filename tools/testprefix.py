#!/usr/bin/env python3
"""The tests' own Proton prefix (TLD_TEST_COMPAT / TLD_REMOTE_TEST_COMPAT), made from a working one with the game's
config left out, for a machine where a prefix Proton makes from nothing never starts the game (the laptop's Proton
Experimental, 2026-10-17: the game process sat at 0 s CPU time; GE-Proton on the desktop made one fine).

  tools/testprefix.py make [--remote] [--from <compatdata folder>]   copy it (default: the game's Steam prefix), then
        remove the game's settings and saves (Documents/TheLongDrive), its Unity logs (AppData/LocalLow/Genesz) and its
        PlayerPrefs (the registry key Software\\Genesz\\TheLongDrive in user.reg)
  tools/testprefix.py status [--remote]

The source prefix is only read. Without --from, a missing test prefix is otherwise made by Proton on the first launch."""
import sys, pathlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import tldenv

def arg(n, d=None): return sys.argv[sys.argv.index(n) + 1] if n in sys.argv else d
REMOTE = "--remote" in sys.argv
STRIP = r'''set -e
S="$1"; D="$2"
[ -d "$S/pfx" ] || { echo "no prefix at $S"; exit 1; }
[ ! -e "$D" ] || { echo "$D exists: remove it first"; exit 1; }
mkdir -p "$D"
cp -a "$S/." "$D/"
U="$D/pfx/drive_c/users/steamuser"
rm -rf "$U/Documents/TheLongDrive" "$U/AppData/LocalLow/Genesz"
python3 - "$D/pfx/user.reg" <<'PY'
import sys, re
p = sys.argv[1]; t = open(p, encoding="utf-8", errors="surrogateescape").read()
# a registry section runs from its [key] line to the next blank line
t2 = re.sub(r"(?ms)^\[Software\\\\Genesz\\\\TheLongDrive\][^\n]*\n.*?(?:\n\n|\Z)", "", t)
open(p, "w", encoding="utf-8", errors="surrogateescape").write(t2)
print("PlayerPrefs removed" if t2 != t else "no PlayerPrefs in user.reg")
PY
rm -f "$D/launch.log"
du -sh "$D" | cut -f1
'''

def run(script, *args):
    if REMOTE:
        q = tldenv.remote_quote
        return subprocess.run(tldenv.ssh_args() + ["bash -s -- " + " ".join(q(a) for a in args)], input=script, text=True, capture_output=True)
    return subprocess.run(["bash", "-s", "--", *args], input=script, text=True, capture_output=True)

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    dest = tldenv.REMOTE_TEST_COMPAT if REMOTE else tldenv.TEST_COMPAT
    if not dest: sys.exit("no test prefix configured (TLD_REMOTE_TEST_COMPAT / TLD_TEST_COMPAT)")
    if cmd == "make":
        src = arg("--from") or (f"{tldenv.REMOTE_STEAM}/steamapps/compatdata/{tldenv.APP_ID}" if REMOTE else str(tldenv.STEAM / f"steamapps/compatdata/{tldenv.APP_ID}"))
        r = run(STRIP, src, dest)
        print(r.stdout.strip(), r.stderr.strip()); sys.exit(r.returncode)
    r = run('[ -d "$1/pfx" ] && echo "$1: $(du -sh "$1" | cut -f1)" || echo "$1: not made yet"', dest)
    print(r.stdout.strip(), r.stderr.strip())
