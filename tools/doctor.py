#!/usr/bin/env python3
"""Check this machine (and the second one, if TLD_SECOND is set) is ready for the test tools. Read-only: it changes
nothing, starts nothing. Prints OK / WARN / FAIL per check; exit 1 when something FAILs.

  tools/doctor.py              this machine, and the second one when configured
  tools/doctor.py --local      this machine only
"""
import os, re, shutil, subprocess, sys, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import tldenv

ROOT = tldenv.TOOLS.parent
FAILS = []


def report(level, what, detail=""):
    if level == "FAIL": FAILS.append(what)
    print(f"{level:4}  {what}" + (f": {detail}" if detail else ""), flush=True)


def check(ok, what, detail="", fix="", level="FAIL"):
    report("OK" if ok else level, what, detail if ok else (fix or detail))
    return ok


def plugin_version():
    m = re.search(r'Version = "([^"]+)"', (ROOT / "src/TLDRevamp/Plugin.cs").read_text())
    return m.group(1) if m else None


def dll_version(path):
    """The version string TLDRevamp.dll carries (build.sh's guard reads it the same way)."""
    try: data = path.read_bytes()
    except OSError: return None
    m = re.search(rb"(?<![\x20-\x7e])0\.\d+\.\d+(?![\x20-\x7e])", data)
    return m.group(0).strip(b"\x00").decode() if m else None


def launch_options():
    """WINEDLLOVERRIDES launch option of the game per Steam account on this machine (localconfig.vdf)."""
    found = []
    for f in sorted((tldenv.STEAM / "userdata").glob("*/config/localconfig.vdf")):
        t = f.read_text(errors="replace")
        opts = None
        for m in re.finditer(r'"%s"\s*\{' % tldenv.APP_ID, t):
            i = j = m.end(); depth = 1
            while depth and j < len(t):   # the app's block, braces inside quoted strings skipped
                c = t[j]
                if c == '"':
                    j += 1
                    while j < len(t) and t[j] != '"': j += 2 if t[j] == "\\" else 1
                elif c == "{": depth += 1
                elif c == "}": depth -= 1
                j += 1
            lo = re.search(r'"LaunchOptions"\s*"((?:[^"\\]|\\.)*)"', t[i:j])
            if lo: opts = lo.group(1).replace('\\"', '"')
        found.append(opts or "")
    return found


def local():
    print(f"== this machine (settings: {', '.join(k for k in sorted(os.environ) if k.startswith('TLD_')) or 'defaults'})")
    check(sys.version_info >= (3, 9), "Python 3.9+", sys.version.split()[0])
    check(shutil.which("dotnet") is not None, "dotnet on PATH", fix="install the .NET SDK 8 or newer")
    check(shutil.which("steam") is not None, "steam on PATH", fix="the tools start the game with `steam -applaunch` (or through Proton in TLD_TEST_COMPAT; Steam must run)")
    check(tldenv.STEAM.is_dir(), "Steam folder", str(tldenv.STEAM), fix=f"{tldenv.STEAM} missing: set TLD_STEAM")
    g = tldenv.GAME_DIR
    if not check((g / "TheLongDrive_Data/Managed/Assembly-CSharp.dll").is_file(), "game install", str(g),
                 fix=f"no game at {g}: set TLD_DIR"):
        return
    check((g / "BepInEx/core/BepInEx.dll").is_file() and (g / "winhttp.dll").is_file(), "BepInEx 5 installed",
          fix="BepInEx/core/BepInEx.dll or winhttp.dll missing: install BepInEx 5.4.23 (see the release's INSTALL.txt)")
    opts = launch_options()
    check(any("winhttp=n,b" in o for o in opts), "Proton launch option", f"set on {sum('winhttp=n,b' in o for o in opts)} of {len(opts)} Steam account(s)",
          fix='no account has WINEDLLOVERRIDES="winhttp=n,b" %command% for the game (without it BepInEx never loads)', level="WARN")
    if tldenv.TEST_COMPAT:
        for name, d in (("TLD_PROTON", tldenv.PROTON / "proton"), ("TLD_SLR", tldenv.SLR / "_v2-entry-point")):
            check(d.is_file(), f"{name} for the tests' own prefix", str(d.parent), fix=f"{d} missing: set {name}")
    report("OK" if tldenv.TEST_COMPAT else "WARN", "tests in their own Proton prefix (TLD_TEST_COMPAT)", tldenv.TEST_COMPAT or
           "not set: tests run in, and change, the prefix you play in (game settings, saves)")
    check(tldenv.DOCS.is_dir(), "Proton prefix (saves)", str(tldenv.PREFIX),
          fix=f"{tldenv.DOCS} missing: run the game once, or set TLD_PREFIX", level="WARN")
    check((ROOT / "lib/Assembly-CSharp.dll").is_file() and (ROOT / "lib/BepInEx.dll").is_file(), "reference assemblies (lib/)",
          fix="run tools/libs.sh")
    want, dll = plugin_version(), g / "BepInEx/plugins/TLDRevamp/TLDRevamp.dll"
    have = dll_version(dll) if dll.is_file() else None
    check(have == want, "mod deployed", f"{have}", fix=f"game has {have or 'no TLDRevamp.dll'}, the source is {want}: run tools/build.sh",
          level="WARN")
    cfg = g / "BepInEx/config/tldrevamp.core.cfg"
    on = cfg.is_file() and re.search(r"(?m)^Bridge = true\s*$", cfg.read_text(errors="replace"))
    check(bool(on), "debug bridge enabled in this game's config", fix="tools/build.sh turns it on (Bridge = true)", level="WARN")
    for name, p in (("TLD_BACKUPS", tldenv.BACKUPS), ("TLD_CACHE", tldenv.CACHE)):
        r = p.resolve()
        inside = r == ROOT.resolve() or ROOT.resolve() in r.parents or g.resolve() in r.parents
        check(not inside and not str(r).startswith("/tmp/"), f"{name} outside the repo, the game and /tmp", str(p),
              fix=f"{p}: keep it elsewhere (backups in /tmp are lost on reboot)", level="WARN")
    import game   # only now: importing it needs nothing, but reads the bridge settings
    try:
        bridge = game.bridge_up()
    except Exception:
        bridge = False
    report("OK" if bridge else "INFO", "bridge answering", "the game is running with the bridge on" if bridge else "game not running (fine)")
    build = game.game_build()
    import json
    try: verified = json.loads(game.BUILD_FILE.read_text())
    except (OSError, ValueError): verified = {}
    same = all(build.get(d) == verified.get(d) for d in game.GAME_DLLS)
    check(same, "game build is the verified one (tools/game_build.json)", f"Steam build {build.get('buildid')}",
          fix=f"installed build {build.get('buildid')} differs from the verified {verified.get('buildid')}: "
              "beta branch? after a game update see tools/gamediff.py", level="WARN")


def second():
    host = tldenv.SECOND
    print("== second machine (TLD_SECOND)")
    if not host:
        report("INFO", "no second machine configured", "two-machine tests need TLD_SECOND=user@host")
        return
    def rsh(cmd):
        return subprocess.run(tldenv.ssh_args()[:-1] + ["-o", "ConnectTimeout=8", host, cmd], capture_output=True, text=True,
                              timeout=60)
    try: r = rsh("true")
    except subprocess.TimeoutExpired: r = None
    if not check(r is not None and r.returncode == 0, "ssh without a prompt", host,
                 fix=f"`ssh {host} true` failed{': ' + r.stderr.strip()[:200] if r else ' (timeout)'}; set up key login (TLD_SSH_KEY)"):
        return
    q = tldenv.remote_quote
    r = rsh("for c in rsync steam pgrep sha256sum; do command -v $c >/dev/null || echo missing $c; done; "
            f"[ -f {q(tldenv.REMOTE_GAME + '/TheLongDrive_Data/Managed/Assembly-CSharp.dll')} ] || echo missing game; "
            f"[ -f {q(tldenv.REMOTE_GAME + '/BepInEx/core/BepInEx.dll')} ] || echo missing bepinex; "
            f"[ -d {q(tldenv.REMOTE_DOCS)} ] || echo missing prefix; " +
            (f"[ -f {q(tldenv.REMOTE_PROTON + '/proton')} ] && [ -f {q(tldenv.REMOTE_SLR + '/_v2-entry-point')} ] || echo missing proton"
             if tldenv.REMOTE_TEST_COMPAT else "true"))
    missing = r.stdout.split()
    tools = [w for i, w in enumerate(missing) if i and missing[i - 1] == "missing" and w not in ("game", "bepinex", "prefix", "proton")]
    check(not tools, "rsync, steam, pgrep, sha256sum there", fix="missing: " + ", ".join(tools))
    check("game" not in missing, "game install there", tldenv.REMOTE_GAME, fix=f"no game at {tldenv.REMOTE_GAME}: set TLD_REMOTE_DIR")
    check("bepinex" not in missing, "BepInEx installed there", fix="install BepInEx 5.4.23 there")
    check("prefix" not in missing, "Proton prefix there", fix=f"{tldenv.REMOTE_DOCS} missing: run the game once, or set TLD_REMOTE_PREFIX",
          level="WARN")
    report("OK" if tldenv.REMOTE_TEST_COMPAT else "WARN", "tests in their own Proton prefix there (TLD_REMOTE_TEST_COMPAT)",
           tldenv.REMOTE_TEST_COMPAT or "not set: tests run in that machine's own game prefix")
    if tldenv.REMOTE_TEST_COMPAT:
        check("proton" not in missing, "TLD_REMOTE_PROTON / TLD_REMOTE_SLR there", fix=f"{tldenv.REMOTE_PROTON} or {tldenv.REMOTE_SLR} missing")
    if tldenv.SECOND_IP:
        report("INFO", "second machine's address for IP joins", tldenv.SECOND_IP)


if __name__ == "__main__":
    local()
    if "--local" not in sys.argv: second()
    print(f"\n{'FAILED: ' + ', '.join(FAILS) if FAILS else 'ready'}")
    sys.exit(1 if FAILS else 0)
