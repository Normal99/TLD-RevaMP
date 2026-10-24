#!/usr/bin/env python3
"""Hands-off game control for automated testing.

  tools/game.py status              is the game running / bridge up / in a world?
  tools/game.py launch              start an isolated hidden test copy and verify its bridge
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
    import launchguard
    # A game that is quitting (the last test's) changes its Wine processes for a few seconds, and the guard refuses
    # the status of processes that don't match the lease: wait that out instead of failing the next test at its start.
    deadline = time.monotonic() + 30
    while True:
        try:
            return launchguard.run(launchguard.specification(bool(REMOTE)), "status", REMOTE).get("running", False)
        except RuntimeError as e:
            if "changed PID/start identity" not in str(e) or time.monotonic() > deadline: raise
            time.sleep(2)


def wait_launcher_gone(timeout=30):
    if running():
        raise RuntimeError("owned game still running; no shared Steam-wrapper polling")


# The game build the mod's patches were last verified on. A Steam update of the beta replaces Assembly-CSharp.dll under
# the mod: Harmony patches may then target changed methods, and test results stop meaning what they used to.
BUILD_FILE = ROOT / "tools/game_build.json"
GAME_DLLS = ("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll")
_build_checked = set()


def game_build():
    """The target machine's game build: sha256 of the game's script DLLs and Steam's build id (None when unreadable)."""
    g = RGAME if REMOTE else str(tldenv.GAME_DIR)
    managed = f'{g}/TheLongDrive_Data/Managed'
    # Steam's build id: its appmanifest next to the library, or a copy's copy.json (tools/installs: <copy>/game)
    r = sh(" ; ".join(f'sha256sum "{managed}/{d}"' for d in GAME_DLLS) +
           f' ; cat "{g}/../../appmanifest_{APP_ID}.acf" 2>/dev/null | grep -m1 \'"buildid"\' || grep -m1 \'"GameBuildId"\' "{g}/../copy.json"')
    out = {"buildid": None}
    for line in r.stdout.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[-1].endswith(GAME_DLLS):
            out[next(d for d in GAME_DLLS if parts[-1].endswith(d))] = parts[0]
        elif ('"buildid"' in line or '"GameBuildId"' in line) and len(parts) >= 2:
            out["buildid"] = parts[-1].strip('",')
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


def require_test_identity():
    import launchguard
    return launchguard.run(launchguard.specification(bool(REMOTE)), "identity", REMOTE)


class ProtectedSession:
    """with ProtectedSession(): ... → saves backed up on entry, autosave slots restored on exit (always)."""
    def __enter__(self):
        require_test_identity()
        self.backup = backup_saves()
        print("saves backed up to", self.backup)
        return self

    def __exit__(self, *exc):
        restore_autosaves(self.backup)
        print("autosave slots restored from", self.backup)
        return False


def prefix_launch_cmd(args=(), compat=None, game_dir=None, port=None):
    """Return only the target-side guarded launcher, including every prefix override caller."""
    import launchguard
    if compat is not None and game_dir is None:
        raise RuntimeError("a prefix override requires its own explicit game directory and port")
    if compat is not None and port is None:
        raise RuntimeError("a prefix override requires its own explicit port")
    spec = launchguard.specification(bool(REMOTE), game_dir, compat, port)
    spec["args"] = list(args)
    return launchguard.command(spec, "launch")


def start(args=(), admit_wait=300, cmd=None):
    """The guarded launch. A refusal by this machine's admission broker for memory pressure is waited out (up to
    admit_wait s), not failed: the broker clears its pause only after 60 s of healthy readings, and with no game
    running nothing reads — each test of a tier failed in turn at its launch (2026-10-22, the desktop swapping while its
    owner worked). Its status call is such a reading; a manual pause or a still unhealthy machine stays refused."""
    deadline = time.monotonic() + admit_wait
    while True:
        result = sh(cmd or prefix_launch_cmd(args))   # cmd: another copy's own guarded launch (tools/thirdplayer.py)
        if not result.returncode: return
        try:
            reason = json.loads(result.stdout).get("guard_error", "target unavailable")
        except ValueError:
            reason = "target unavailable"
        if "admission broker refused" not in reason or time.monotonic() > deadline:
            raise RuntimeError("guarded hidden launch refused: " + reason)
        print("  the admission broker paused launches (memory pressure): waiting for it", flush=True)
        if REMOTE:
            # the laptop's own watcher feeds its broker; a pause right after a big test (30 min of bots) cost the next
            # test its laptop (2026-10-22): try again until it clears
            time.sleep(10); continue
        try:
            import testlaunch; broker = str(testlaunch.broker_path())
        except Exception: broker = None
        while time.monotonic() < deadline:
            st = subprocess.run([broker, "status"], capture_output=True, text=True).stdout if broker else ""
            if "paused" not in st.split("\n", 1)[0] and "manual" not in st: break
            time.sleep(1)


def follow_steam():
    """When this machine's game is a copy (tools/installs: TLD_DIR=<copies>/test/game) and Steam has updated its own
    game since, bring the copy's game files up to Steam's first, so the tests keep running the build Steam has (and the
    second machine gets from Steam)."""
    info = tldenv.GAME_DIR.parent / "copy.json"
    if REMOTE or not info.exists(): return
    try:
        have = json.loads(info.read_text()).get("GameBuildId")
        acf = (tldenv.STEAM / f"steamapps/appmanifest_{APP_ID}.acf").read_text()
    except (OSError, ValueError): return
    import re
    m = re.search(r'"buildid"\s+"(\d+)"', acf)
    if not m or m.group(1) == have: return
    print(f"the copy {tldenv.GAME_DIR.parent.name} has game build {have}, Steam has {m.group(1)}: updating its game files")
    r = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tools/installs/Installs.Cli"), "-c", "Release", "--",
                        "sync", tldenv.GAME_DIR.parent.name, "--root", str(tldenv.GAME_DIR.parent.parent)])
    if r.returncode != 0: print("!! updating the copy's game files failed: the test runs the old game build")


def launch(timeout=180):
    import testdisplay
    if running():
        raise RuntimeError("already-running game refused; a bridge reply cannot authorize reuse")
    check_game_build()
    start(())
    t0 = time.monotonic()
    last = "the bridge never answered"
    while time.monotonic() - t0 < timeout:
        try:
            b = Bridge()
        except (OSError, RuntimeError) as e:
            last = "bridge: " + str(e)
            time.sleep(2)
            continue
        profile = "remote" if REMOTE else "desktop"
        try:
            testdisplay.configure(b, profile)
            testdisplay.check(testdisplay.verify(b, profile), profile)
            return
        except RuntimeError as e:
            last = str(e)
            time.sleep(1)  # Bridge can start before menu/settings objects exist; never accept a partial profile.
    kill_prefix()
    raise RuntimeError("guarded game/bridge profile did not come up; no fallback (last: " + last + ")")


def quit_game(timeout=60):
    if not running():
        return
    # Bridge checks the lease, exact PID/start, listener, hidden window and dataPath before Quit.
    Bridge()("call UnityEngine.Application.Quit")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not running():
            return
        time.sleep(1)
    kill_prefix()


def kill_prefix(compat=None, game_dir=None, port=None):
    """Signal only exact leased PID/start identities on the selected machine; no name/regex kills."""
    import launchguard
    if compat is not None and (game_dir is None or port is None):
        raise RuntimeError("cleanup override requires game directory, prefix and port")
    spec = launchguard.specification(bool(REMOTE), game_dir, compat, port)
    # Right after a quit, Wine's processes are still exiting: one can change identity between the guard's check and its
    # signal, and the guard refuses rather than signal a PID it can no longer prove. Asking again once they are gone is safe.
    deadline = time.monotonic() + 15
    while True:
        try:
            return launchguard.run(spec, "cleanup", REMOTE)
        except RuntimeError as e:
            if not ("identity changed" in str(e) or "No such process" in str(e)) or time.monotonic() > deadline: raise
            time.sleep(1)


def verify_profile(bridge):
    import testdisplay
    profile = "remote" if REMOTE else "desktop"
    testdisplay.configure(bridge, profile)
    return testdisplay.check(testdisplay.verify(bridge, profile), profile)


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
                    verify_profile(b)
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
                    verify_profile(b)
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
# BepInEx runs this patcher before loading the game; deploy the exact preloader that was built.
PRELOADER_DLL = ROOT / "src/TLDRevamp.Preloader/bin/Release/netstandard2.1/TLDRevamp.Preloader.dll"
APPROVED_MODS = ROOT / "packaging/approved-mods.txt"   # ships next to TLDRevamp.dll (mods joiners may download)
# the example mods (src/Mods): deployed only when a test names them (TLD_TEST_MODS=CarTrailer,HeadUnit,TrailerHitches), and removed
# from the game otherwise, so no other test runs with them
EXAMPLE_MODS = ("CarTrailer", "HeadUnit", "ApiProbe", "TrailerHitches", "TvBrowser")   # ApiProbe: the mod API test mod (tools/modapi2.py), never shipped
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
    require_test_identity()
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
    require_test_identity()
    if running(): quit_game()
    if not PRELOADER_DLL.exists():
        raise RuntimeError("built preloader missing; build the preloader before deploy")
    g = tldenv.GAME_DIR
    dst = g / "BepInEx/plugins/TLDRevamp"; dst.mkdir(parents=True, exist_ok=True)
    for f in (plugin_dll(), MP_API_DLL, APPROVED_MODS, ROOT / "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"):
        if f.exists(): shutil.copy2(f, dst / f.name)
    preloader_dir = g / "BepInEx/patchers/TLDRevamp"
    preloader_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(PRELOADER_DLL, preloader_dir / PRELOADER_DLL.name)
    mods = sorted((ROOT / "thirdparty/TLDLoaderMods").glob("*.dll"))   # as build.sh: third-party TLDLoader mods (not in git)
    if mods: (g / "BepInEx/TLDLoaderMods").mkdir(parents=True, exist_ok=True)
    for m in mods: shutil.copy2(m, g / "BepInEx/TLDLoaderMods" / m.name)
    local_mods(g)
    cfg = g / "BepInEx/config/tldrevamp.core.cfg"
    txt = cfg.read_text() if cfg.exists() else ""
    import re
    cfg.parent.mkdir(parents=True, exist_ok=True)
    cfg.write_text(re.sub(r"(?m)^Bridge = .*$", "Bridge = true", txt) if re.search(r"(?m)^Bridge = ", txt) else txt + "\n[Debug]\nBridge = true\n")
    print("deployed locally")


def deploy():
    """Copy the built plugin to TLD_REMOTE (the game there must be restarted to load it)."""
    require_test_identity()
    if not PRELOADER_DLL.exists():
        raise RuntimeError("built preloader missing; build the preloader before deploy")
    dll = plugin_dll()
    remote_dir = tldenv.rsync_path(RGAME) + "/BepInEx/plugins/TLDRevamp/"
    stub = ROOT / "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"
    files = [str(dll)] + [str(f) for f in (stub, MP_API_DLL, APPROVED_MODS) if f.exists()]
    r = subprocess.run(["rsync", "-a", "-s", "-e", " ".join(ssh_base()[:-1])] + files + [f"{REMOTE}:{remote_dir}"], capture_output=True, text=True)
    if r.returncode != 0: sys.exit("deploy to " + REMOTE + " failed: " + r.stderr)
    patcher_dir = remote_dir.replace("plugins/TLDRevamp/", "patchers/TLDRevamp/")
    r = subprocess.run(["rsync", "-a", "-s", "--mkpath", "-e", " ".join(ssh_base()[:-1]), str(PRELOADER_DLL), f"{REMOTE}:{patcher_dir}"], capture_output=True, text=True)
    if r.returncode != 0: sys.exit("deploying preloader to " + REMOTE + " failed: " + r.stderr)
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
