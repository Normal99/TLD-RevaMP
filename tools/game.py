#!/usr/bin/env python3
"""Hands-off game control for automated testing.

  tools/game.py status              is the game running / bridge up / in a world?
  tools/game.py launch              start the game through Steam and wait for the mod's bridge
  tools/game.py quit                quit cleanly (Application.Quit), kill only if it hangs
  tools/game.py newgame [seed]      from the main menu: start a new game with a fixed seed, wait for the world
  tools/game.py cycle [seed]        build + deploy the mod, quit, launch, new seeded game (one iteration)
  tools/game.py gamecheck           is the game's code still the build the mod was verified on? (exit 1 if not)
  tools/game.py gameaccept          record the installed game build as verified (after reviewing a game update)

Save protection: every command that can run the game backs up the save folder first
(TLD_BACKUPS, default ~/tld-revamp-backups/) and, when it's done, puts the autosave slots back exactly as they were,
so test worlds can never replace the player's autosaves.
"""
import os, shutil, subprocess, sys, time, datetime, pathlib, json
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from bench import Bridge, vec, REMOTE, ssh_base
import tldenv

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCS = tldenv.DOCS
SAVES = DOCS / "Saves"
BACKUPS = tldenv.BACKUPS
APP_ID = tldenv.APP_ID
DEFAULT_SEED = 20260924


RDOCS = tldenv.REMOTE_DOCS
RGAME = tldenv.REMOTE_GAME


def sh(cmd, **kw):
    """Run a shell command on the target machine (this one, or TLD_REMOTE over SSH)."""
    if REMOTE:
        return subprocess.run(ssh_base() + [cmd], capture_output=True, text=True, **kw)
    return subprocess.run(cmd, shell=True, capture_output=True, text=True, **kw)


def running():
    # [.]: the pattern must not match the shell running it (a plain one matched a test's own shell and killed it, 10-08)
    return sh("pgrep -f 'TheLongDrive[.]exe'").returncode == 0


def wait_launcher_gone(timeout=30):
    """Steam's launch wrapper of the last run (reaper / pressure-vessel, "AppId=1017180") can outlive the game by a few
    seconds; a launch inside that window came up and died at once (4 times on 10-08, both machines: the log ends after
    the first frames, no error). Wait for it to go."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if sh(f"pgrep -f 'AppId[=]{APP_ID}'").returncode != 0: return
        time.sleep(1)
    print("!! the last launch's Steam wrapper is still there after", timeout, "s")


# The game build the mod's patches were last verified on. A Steam update of the beta replaces Assembly-CSharp.dll under
# the mod: Harmony patches may then target changed methods, and test results stop meaning what they used to.
BUILD_FILE = ROOT / "tools/game_build.json"
GAME_DLLS = ("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll")
_build_checked = set()


def game_build():
    """The target machine's game build: sha256 of the game's script DLLs and Steam's build id (None when unreadable)."""
    g = RGAME if REMOTE else str(tldenv.GAME_DIR)
    managed = f'{g}/TheLongDrive_Data/Managed'
    r = sh(" ; ".join(f'sha256sum "{managed}/{d}"' for d in GAME_DLLS) +
           f' ; grep -m1 \'"buildid"\' "{g}/../../appmanifest_{APP_ID}.acf"')
    out = {"buildid": None}
    for line in r.stdout.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[-1].endswith(GAME_DLLS):
            out[next(d for d in GAME_DLLS if parts[-1].endswith(d))] = parts[0]
        elif '"buildid"' in line and len(parts) >= 2:
            out["buildid"] = parts[-1].strip('"')
    return out


def check_game_build(force=False):
    """Warn loudly when the game's code differs from the verified build (once per machine per process)."""
    where = REMOTE or "this machine"
    if where in _build_checked and not force:
        return True
    _build_checked.add(where)
    try:
        want = json.loads(BUILD_FILE.read_text())
    except (OSError, ValueError):
        print(f"!! no verified game build recorded ({BUILD_FILE.name}): run tools/game.py gameaccept once")
        return False
    have = game_build()
    changed = [d for d in GAME_DLLS if have.get(d) != want.get(d)]
    if not changed:
        return True
    bar = "!" * 100
    print(bar)
    print(f"!! GAME DLL CHANGED on {where}: the game is not the build the mod was verified on.")
    for d in changed:
        print(f"!!   {d}: verified {str(want.get(d))[:16]}…  now {str(have.get(d))[:16]}…")
    print(f"!!   Steam build id: verified {want.get('buildid')}, now {have.get('buildid')}")
    print("!! Patched game methods may have changed — results of this run may not mean what they did.")
    print("!! Diff the game code (tools/gamediff.py), review the mod's patches, then: tools/game.py gameaccept")
    print(bar)
    return False


def accept_game_build():
    have = game_build()
    if any(not have.get(d) for d in GAME_DLLS):
        sys.exit("could not read the game's DLLs on " + (REMOTE or "this machine"))
    have["acceptedAt"] = datetime.datetime.now().isoformat(timespec="seconds")
    BUILD_FILE.write_text(json.dumps(have, indent=2) + "\n")
    print("verified game build recorded:", json.dumps(have))


def bridge_up():
    try:
        Bridge()("ping")
        return True
    except Exception:
        return False


def backup_saves():
    if REMOTE:
        name = f"saves-{datetime.datetime.now():%Y%m%d-%H%M%S}"
        r = sh(f'set -e; B="$HOME/tld-revamp-backups/{name}"; mkdir -p "$B"; '
               f'[ -d "{RDOCS}/Saves" ] && cp -a "{RDOCS}/Saves" "$B/Saves"; cp -a "{RDOCS}"/*.tldc "$B/" 2>/dev/null || true; echo "$B"')
        if r.returncode != 0: sys.exit("remote save backup failed: " + r.stderr)
        return pathlib.PurePosixPath(r.stdout.strip())
    dest = BACKUPS / f"saves-{datetime.datetime.now():%Y%m%d-%H%M%S}"
    dest.mkdir(parents=True, exist_ok=True)
    if SAVES.exists():
        shutil.copytree(SAVES, dest / "Saves")
    for f in DOCS.glob("*.tldc"):
        shutil.copy2(f, dest / f.name)
    return dest


def restore_autosaves(backup):
    """Put autosave slots back exactly as in `backup` (and remove autosave files the session created)."""
    if REMOTE:
        r = sh(f'S="{RDOCS}/Saves"; B="{backup}/Saves"; '
               f'for f in "$S"/*autosave*; do [ -e "$f" ] || continue; [ -e "$B/$(basename "$f")" ] || rm -f "$f"; done; '
               f'for f in "$B"/*autosave*; do [ -e "$f" ] && cp -a "$f" "$S/"; done; true')
        if r.returncode != 0: print("!! remote autosave restore failed:", r.stderr)
        return
    src = backup / "Saves"
    before = {p.name for p in src.glob("*autosave*")} if src.exists() else set()
    for p in SAVES.glob("*autosave*"):
        if p.name not in before:
            p.unlink()
    for name in before:
        shutil.copy2(src / name, SAVES / name)


class ProtectedSession:
    """with ProtectedSession(): ... → saves backed up on entry, autosave slots restored on exit (always)."""
    def __enter__(self):
        self.backup = backup_saves()
        print("saves backed up to", self.backup)
        return self

    def __exit__(self, *exc):
        restore_autosaves(self.backup)
        print("autosave slots restored from", self.backup)
        return False


def prefix_launch_cmd(args=(), compat=None):
    """The shell command that starts the game through Proton in the tests' own prefix (tldenv.TEST_COMPAT, or `compat`:
    a second copy of the game in a prefix of its own), the way Steam's launch does it (Steam Linux Runtime entry point
    → proton waitforexitandrun), or None without one."""
    if REMOTE:
        c0, steam, gdir, proton, slr = tldenv.REMOTE_TEST_COMPAT, tldenv.REMOTE_STEAM, RGAME, tldenv.REMOTE_PROTON, tldenv.REMOTE_SLR
    else:
        c0, steam, gdir, proton, slr = tldenv.TEST_COMPAT, str(tldenv.STEAM), str(tldenv.GAME_DIR), str(tldenv.PROTON), str(tldenv.SLR)
    compat = compat or c0
    if not compat: return None
    q = tldenv.remote_quote
    env = {"STEAM_COMPAT_DATA_PATH": compat, "STEAM_COMPAT_CLIENT_INSTALL_PATH": steam, "STEAM_COMPAT_INSTALL_PATH": gdir,
           "STEAM_COMPAT_APP_ID": APP_ID, "SteamAppId": APP_ID, "SteamGameId": APP_ID, "STEAM_COMPAT_LIBRARY_PATHS": steam + "/steamapps",
           "STEAM_COMPAT_TOOL_PATHS": proton + ":" + slr, "STEAM_COMPAT_MOUNTS": f"{steam}/steamapps/common/Steamworks Shared:{proton}:{slr}",
           "STEAM_COMPAT_FLAGS": "search-cwd", "STEAM_COMPAT_PROTON": "1", "STEAM_COMPAT_SHADER_PATH": f"{steam}/steamapps/shadercache/{APP_ID}",
           "SteamClientLaunch": "1", "SteamEnv": "1", "SteamOverlayGameId": APP_ID,
           "WINEDLLOVERRIDES": "winhttp=n,b"}   # winhttp: BepInEx's doorstop
    extra = " ".join(q(a) for a in args)
    # only the game in the background ({ … & }): `a && b &` backgrounds the whole list, and the shell running it (ssh's,
    # or ours with captured output) then waits for the game
    # the desktop session's display and bus, taken from the running Steam client (an ssh shell has none: the game sat
    # before its first frame, 0 s CPU)
    session = ('S=$(pgrep -xo steam) && export $(tr "\\0" "\\n" < /proc/$S/environ | grep -E '
               '"^(DISPLAY|WAYLAND_DISPLAY|XDG_RUNTIME_DIR|XAUTHORITY|DBUS_SESSION_BUS_ADDRESS|XDG_SESSION_TYPE|XDG_CURRENT_DESKTOP)=" | xargs); ')
    return (session + f'mkdir -p {q(compat)} && cd {q(gdir)} && {{ nohup setsid env ' + " ".join(f"{k}={q(v)}" for k, v in env.items()) +
            f' {q(slr + "/_v2-entry-point")} --verb=waitforexitandrun -- {q(proton + "/proton")} waitforexitandrun {q(gdir + "/TheLongDrive.exe")} {extra}'
            f' >{q(compat + "/launch.log")} 2>&1 < /dev/null & }}')


def start(args=()):
    """Start the game (extra command-line args for it): in the tests' own Proton prefix when one is set, else by Steam."""
    cmd = prefix_launch_cmd(args)
    if cmd:
        r = sh(cmd)
        if r.returncode != 0: sys.exit(f"launch in the test prefix failed: {r.stderr.strip()[:300]}")
    elif REMOTE:
        sh("nohup setsid steam -applaunch " + APP_ID + "".join(" " + a for a in args) + " >/dev/null 2>&1 &")
    else:
        subprocess.Popen(["steam", "-applaunch", APP_ID, *args], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         start_new_session=True)


def launch(timeout=180):
    check_game_build()
    if running() and bridge_up():
        return
    if not running(): wait_launcher_gone()
    start(())
    t0 = time.time()
    while time.time() - t0 < timeout:
        if bridge_up():
            time.sleep(3)
            print(f"game up after {time.time() - t0:.0f}s")
            return
        time.sleep(2)
    sys.exit("game/bridge did not come up")


def quit_game(timeout=60):
    if not running():
        return
    try:
        Bridge()("call UnityEngine.Application.Quit")
    except Exception:
        pass
    t0 = time.time()
    while time.time() - t0 < timeout:
        if not running():
            print(f"game quit after {time.time() - t0:.0f}s")
            # the next launch starts a new log: keep this session's (tests that restart a game mid-run need it)
            sh(f'cp -f "{RGAME}/BepInEx/LogOutput.log" "{RGAME}/BepInEx/LogOutput.prev.log"')
            time.sleep(3)  # let Steam notice
            wait_launcher_gone()
            return
        time.sleep(1)
    print("!! game did not quit cleanly; killing it")
    sh("pkill -f 'TheLongDrive[.]exe'")
    kill_prefix()
    time.sleep(5)


def kill_prefix(compat=None):
    """Everything still running in the tests' own prefix (or `compat`) — Proton's steam.exe, wineserver, the runtime: a
    game killed alone left them, and the next launch in that prefix waited on the old wineserver for good."""
    compat = compat or (tldenv.REMOTE_TEST_COMPAT if REMOTE else tldenv.TEST_COMPAT)
    if not compat: return
    sh(f'C={tldenv.remote_quote(compat)}; for p in $(pgrep -u "$(id -u)"); do [ "$p" = "$$" ] && continue; '
       'tr "\\0" "\\n" < /proc/$p/environ 2>/dev/null | grep -qE "^(STEAM_COMPAT_DATA_PATH|WINEPREFIX)=$C(/pfx)?/?$" && kill -9 $p; done; true')


def wait_world(seed, timeout=180):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            b = Bridge()
            if (b("get DataFromMenuScript.s.mainmenu")["value"] is False
                    and str(b("get DataFromMenuScript.s.seed")["value"]) == str(seed)
                    and b("get mainscript.s.player")["value"] is not None):
                pos = vec(b("get mainscript.s.player.transform.position")["value"])
                if b(f"ground {pos[0]} {pos[2]}")["hit"]:
                    # Test worlds must not autosave (also restored at file level by ProtectedSession). Runtime-only mod
                    # flag: never touch the player's own BAutoSave setting (the game persists it when the settings menu
                    # closes; that turned the player's autosave off on 2026-09-25).
                    b("set TLDRevamp.TestSafety.BlockAutoSave True")
                    time.sleep(10)
                    return b
        except Exception:
            pass
        time.sleep(2)
    sys.exit("world did not load")


def loadsave(name, timeout=180):
    """Load the player's save `name` (autosave blocked by the mod first) and wait until the player stands in the world."""
    b = Bridge()
    r = b(f"loadsave {name}")
    if "error" in r:
        sys.exit(f"loadsave: {r['error']}")
    time.sleep(5)
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            b = Bridge()
            if b("get DataFromMenuScript.s.mainmenu")["value"] is False and b("get mainscript.s.player")["value"] is not None:
                pos = vec(b("get mainscript.s.player.transform.position")["value"])
                if b(f"ground {pos[0]} {pos[2]}")["hit"]:
                    b("set TLDRevamp.TestSafety.BlockAutoSave True")
                    time.sleep(10)  # let the surroundings and items finish loading
                    return b
        except Exception:
            pass
        time.sleep(2)
    sys.exit("save did not load")


def newgame(seed=DEFAULT_SEED):
    b = Bridge()
    if b("get DataFromMenuScript.s.mainmenu")["value"] is not True:
        # Already in a world: use the game's own same-seed restart
        b(f"set DataFromMenuScript.s.seed {seed}")
        b("call menuhandler.s.PressedRestart")
    else:
        b(f"set DataFromMenuScript.s.seed {seed}")
        b("call menuhandler.s.PressedStart")
    time.sleep(5)
    wait_world(seed)
    print(f"in a new world, seed {seed}")


def plugin_dll():
    """The TLDRevamp.dll tests deploy: this repo's build, or TLD_PLUGIN_DLL (an older version built in a git worktree,
    for A/B runs - never check old sources out over this tree)."""
    return pathlib.Path(os.environ["TLD_PLUGIN_DLL"]) if os.environ.get("TLD_PLUGIN_DLL") else ROOT / "src/TLDRevamp/bin/Release/netstandard2.1/TLDRevamp.dll"


# the multiplayer API other mods reference: TLDRevamp.dll needs it next to it
MP_API_DLL = ROOT / "src/TLDRevamp.Multiplayer/bin/Release/netstandard2.1/TLDRevamp.Multiplayer.dll"
APPROVED_MODS = ROOT / "packaging/approved-mods.txt"   # ships next to TLDRevamp.dll (mods joiners may download)
# the example mods (src/Mods): deployed only when a test names them (TLD_TEST_MODS=CarTrailer,HeadUnit), and removed
# from the game otherwise, so no other test runs with them
EXAMPLE_MODS = ("CarTrailer", "HeadUnit", "ApiProbe")   # ApiProbe: the mod API test mod (tools/modapi2.py), never shipped
def test_mods():
    return [m for m in os.environ.get("TLD_TEST_MODS", "").split(",") if m in EXAMPLE_MODS]

def build_test_mods():
    for m in test_mods():
        r = subprocess.run(["dotnet", "build", str(ROOT / f"src/Mods/{m}/{m}.csproj"), "-c", "Release", "-nologo", "-v", "q"], capture_output=True, text=True)
        if r.returncode != 0 or "0 Error(s)" not in r.stdout: print(r.stdout[-1500:]); sys.exit(f"building {m} failed")

def local_mods(g=None):
    """this machine's game: the example mods the test names, none of the others"""
    g = g or tldenv.GAME_DIR
    for m in EXAMPLE_MODS:
        md = g / "BepInEx/plugins" / m
        if m in test_mods(): md.mkdir(parents=True, exist_ok=True); shutil.copy2(mod_dll(m), md / f"{m}.dll")
        elif md.exists(): shutil.rmtree(md)

def mod_dll(m): return ROOT / f"src/Mods/{m}/bin/Release/netstandard2.1/{m}.dll"


def build():
    build_test_mods()
    r = subprocess.run([str(ROOT / "tools/build.sh")], capture_output=True, text=True)
    if r.returncode != 0 or "0 Error(s)" not in r.stdout:
        print(r.stdout[-2000:], r.stderr[-2000:])
        sys.exit("build failed")
    local_mods()
    print("built + deployed")
    if REMOTE:
        deploy()


def deploy_local():
    """Copy the already-built plugin into this machine's game (never under a running game) and turn the bridge on."""
    if running(): quit_game()
    g = tldenv.GAME_DIR
    dst = g / "BepInEx/plugins/TLDRevamp"; dst.mkdir(parents=True, exist_ok=True)
    for f in (plugin_dll(), MP_API_DLL, APPROVED_MODS, ROOT / "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"):
        if f.exists(): shutil.copy2(f, dst / f.name)
    local_mods(g)
    cfg = g / "BepInEx/config/tldrevamp.core.cfg"
    txt = cfg.read_text() if cfg.exists() else ""
    import re
    cfg.parent.mkdir(parents=True, exist_ok=True)
    cfg.write_text(re.sub(r"(?m)^Bridge = .*$", "Bridge = true", txt) if re.search(r"(?m)^Bridge = ", txt) else txt + "\n[Debug]\nBridge = true\n")
    print("deployed locally")


def deploy():
    """Copy the built plugin to TLD_REMOTE (the game there must be restarted to load it)."""
    dll = plugin_dll()
    remote_dir = tldenv.rsync_path(RGAME) + "/BepInEx/plugins/TLDRevamp/"
    stub = ROOT / "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"
    files = [str(dll)] + [str(f) for f in (stub, MP_API_DLL, APPROVED_MODS) if f.exists()]
    r = subprocess.run(["rsync", "-a", "-s", "-e", " ".join(ssh_base()[:-1])] + files + [f"{REMOTE}:{remote_dir}"], capture_output=True, text=True)
    if r.returncode != 0: sys.exit("deploy to " + REMOTE + " failed: " + r.stderr)
    mods = sorted((ROOT / "thirdparty/TLDLoaderMods").glob("*.dll"))   # third-party TLDLoader mods (not in git)
    if mods:
        mods_dir = remote_dir.replace("BepInEx/plugins/TLDRevamp/", "BepInEx/TLDLoaderMods/")
        r = subprocess.run(["rsync", "-a", "-s", "--mkpath", "-e", " ".join(ssh_base()[:-1])] + [str(m) for m in mods] + [f"{REMOTE}:{mods_dir}"], capture_output=True, text=True)
        if r.returncode != 0: sys.exit("deploying TLDLoader mods to " + REMOTE + " failed: " + r.stderr)
    plugins = remote_dir.replace("plugins/TLDRevamp/", "plugins/")
    for m in EXAMPLE_MODS:
        if m in test_mods():
            r = subprocess.run(["rsync", "-a", "-s", "--mkpath", "-e", " ".join(ssh_base()[:-1]), str(mod_dll(m)), f"{REMOTE}:{plugins}{m}/"], capture_output=True, text=True)
            if r.returncode != 0: sys.exit(f"deploying {m} to {REMOTE} failed: " + r.stderr)
        else: sh(f'rm -rf "{plugins}{m}"')
    cfg = remote_dir.replace("plugins/TLDRevamp/", "config/tldrevamp.core.cfg")
    r = sh(f"""f="{cfg}"; if [ -f "$f" ] && grep -q '^Bridge = ' "$f"; then sed -i 's/^Bridge = .*/Bridge = true/' "$f"; """
           f"""else mkdir -p "$(dirname "$f")"; printf '\\n[Debug]\\nBridge = true\\n' >> "$f"; fi""")
    if r.returncode != 0: sys.exit("enabling the bridge on " + REMOTE + " failed: " + r.stderr)
    print("deployed to", REMOTE)


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    seed = int(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_SEED
    if cmd == "status":
        print(json.dumps({"running": running(), "bridge": bridge_up()}))
    elif cmd == "launch":
        launch()
    elif cmd == "quit":
        quit_game()
    elif cmd == "deploy":
        deploy()
    elif cmd == "newgame":
        newgame(seed)  # wrap test sessions in ProtectedSession (tools/seeded.py does)
    elif cmd == "gamecheck":
        ok = check_game_build(force=True)
        print(json.dumps(game_build()))
        sys.exit(0 if ok else 1)
    elif cmd == "gameaccept":
        accept_game_build()
    elif cmd == "cycle":
        build()
        quit_game()
        launch()
        newgame(seed)  # caller wraps the whole test session in ProtectedSession
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
