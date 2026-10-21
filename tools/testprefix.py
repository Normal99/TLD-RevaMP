#!/usr/bin/env python3
"""The tests' own Proton prefix (TLD_TEST_COMPAT / TLD_REMOTE_TEST_COMPAT), made from a working one with the game's
config left out, for a machine where a prefix Proton makes from nothing never starts the game (the laptop's Proton
Experimental, 2026-10-17: the game process sat at 0 s CPU time; GE-Proton on the desktop made one fine).

  tools/testprefix.py make [--remote] [--from <compatdata folder>]   copy it (default: the game's Steam prefix), then
        remove the game's settings and saves (Documents/TheLongDrive), its Unity logs (AppData/LocalLow/Genesz) and its
        PlayerPrefs (the registry key Software\\Genesz\\TheLongDrive in user.reg)
  tools/testprefix.py lowgfx [--remote]   the game's graphics effects off in the test prefix (shadows, grass, post-
        processing, mirrors, fog, …: the laptop's own play settings, which every earlier laptop result ran with). The
        game must run in the test prefix: it writes its settings file (defaults) first, then the effects are switched
        off in it and the game quits (it reads the file at start). The laptop ran the defaults at 15 fps, 2026-10-17.
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
# The saves and the game's settings go; a seeded settings file stays: the guarded
# launcher needs a settings file with the mandatory profile before the first frame,
# and re-seeding it would leave the game audible for one start.
KEEP=""
[ -f "$U/Documents/TheLongDrive/settings.tldc" ] && KEEP="$D/.settings-keep.tldc" && cp -a "$U/Documents/TheLongDrive/settings.tldc" "$KEEP"
rm -rf "$U/Documents/TheLongDrive" "$U/AppData/LocalLow/Genesz"
if [ -f "$KEEP" ]; then
  mkdir -p "$U/Documents/TheLongDrive"
  cp -a "$KEEP" "$U/Documents/TheLongDrive/settings.tldc"
  rm -f "$KEEP"
  echo "kept the seeded settings.tldc"
fi
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

LOW_GFX = {"BEnableShadows": False, "BMirrors": False, "BPostProcessing": False, "BDof": False, "BEnableSunGlassesEffects": False,
           "BHeatHaze": False, "BReflection": False, "BGrass": False, "BSmoke": False, "BFog": False, "BAntialiasing": False,
           "IAnisotropic": 0, "BCloudShadow": False, "BSkyObjs": False}
EDIT = r'''set -e
F="$1/settings.tldc"; [ -f "$F" ] || { echo "no $F"; exit 1; }
python3 - "$F" "$2" <<'PY'
import sys, json
p = sys.argv[1]; s = json.load(open(p, encoding="utf-8-sig")); low = json.loads(sys.argv[2])
missing = [k for k in low if k not in s]
if missing: sys.exit("not in the settings file: " + ", ".join(missing))
s.update(low); json.dump(s, open(p, "w"), indent=4); print("graphics effects off in", p)
PY
'''


def lowgfx():
    import json, os, time, mplib, game
    remote = tldenv.need_second() if REMOTE else None
    if not mplib._in_test_prefix(remote) or not mplib._game_prefixes(remote)[1]:
        sys.exit("the game must be running in the test prefix (start it with tools/game.py launch)")
    from bench import Bridge
    Bridge(remote)("call settingsscript.s.SaveSettings"); time.sleep(2)   # the game's own file, with its defaults
    r = run(EDIT, tldenv.REMOTE_DOCS if REMOTE else str(tldenv.DOCS), json.dumps(LOW_GFX))
    print(r.stdout.strip(), r.stderr.strip())
    if r.returncode != 0: sys.exit(r.returncode)
    env = dict(os.environ, TLD_REMOTE=remote) if remote else dict(os.environ)
    subprocess.run([sys.executable, str(tldenv.TOOLS / "game.py"), "quit"], env=env)   # read again at the next start


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
    if cmd == "lowgfx": lowgfx(); sys.exit(0)
    r = run('[ -d "$1/pfx" ] && echo "$1: $(du -sh "$1" | cut -f1)" || echo "$1: not made yet"', dest)
    print(r.stdout.strip(), r.stderr.strip())
