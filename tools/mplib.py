"""Shared helpers for two-machine scenarios: the remote machine's protected helper (Agent), restarting both games, the
fresh test world on both, the preflight checks, and the phase timer every test prints at exit (`[time] build …`)."""
import sys, os, json, subprocess, pathlib, time, threading, atexit, contextlib, hashlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, bench, tldenv, relaynet, relaypreflight, relaynet, relaypreflight

ROOT = pathlib.Path(__file__).resolve().parent.parent
REMOTE_HOST = tldenv.SECOND       # the second machine ("the laptop" in test output), user@host
DESKTOP_IP = tldenv.lan_ip()      # this machine ("the desktop", the host), as the second one reaches it


# ---- phase timer: where a test's time goes. Setup phases are timed where they run; "test" is the rest of the run.
_T0 = time.time()
PHASES = {}
KEPT = set()   # the machines whose game ensure_both kept
PHASE_ORDER = ("wait", "agent", "build", "restart", "world", "check", "relay", "join")


@contextlib.contextmanager
def phase(name):
    t = time.time()
    try: yield
    finally: PHASES[name] = PHASES.get(name, 0.0) + time.time() - t


def timing():
    total = time.time() - _T0
    setup = sum(PHASES.values())
    return {"phases": {k: round(v, 1) for k, v in PHASES.items()}, "setup": round(setup, 1),
            "test": round(total - setup, 1), "total": round(total, 1), "kept": sorted(KEPT)}


def _report_timing():
    if not PHASES: return
    t = timing()
    print("[time] " + ", ".join(f"{k} {t['phases'][k]:.0f}s" for k in PHASE_ORDER if k in t["phases"]) +
          f", test {t['test']:.0f}s (setup {t['setup']:.0f}s of {t['total']:.0f}s)", flush=True)
    if os.environ.get("TLD_TIMING_FILE"):   # tools/suite.py reads it
        try: pathlib.Path(os.environ["TLD_TIMING_FILE"]).write_text(json.dumps(t))
        except OSError: pass


atexit.register(_report_timing)


# ---- one test run on the machines at a time: two sessions restarting the same games ruined both runs
LOCK_FILE = tldenv.CACHE / "machines.lock"
_lock = None


def lock_machines(wait=None):
    """Hold the machine lock for the rest of this process (flock: gone when the process ends, however it ends). A test
    run by tools/suite.py shares the suite's lock (TLD_MACHINES_LOCKED, the suite's pid). Waits for another run to
    finish (TLD_LOCK_WAIT seconds, default two hours)."""
    global _lock
    import fcntl
    if _lock is not None: return
    held = os.environ.get("TLD_MACHINES_LOCKED", "")
    if held.isdigit() and int(held) != os.getpid():
        try: os.kill(int(held), 0); return     # the suite that started this test holds it
        except OSError: pass
    wait = float(os.environ.get("TLD_LOCK_WAIT", 7200)) if wait is None else wait
    LOCK_FILE.parent.mkdir(parents=True, exist_ok=True)
    f = open(LOCK_FILE, "a+")
    t0, told = time.time(), False
    while True:
        try: fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB); break
        except BlockingIOError:
            if not told:
                f.seek(0); print("the machines are busy (" + (f.read().strip() or "another test run") + "): waiting", flush=True); told = True
            if time.time() - t0 > wait: sys.exit(f"the machines stayed busy for {wait:.0f}s ({LOCK_FILE})")
            time.sleep(5)
    if told: PHASES["wait"] = PHASES.get("wait", 0.0) + time.time() - t0
    f.seek(0); f.truncate()
    f.write(f"pid {os.getpid()}, {' '.join(pathlib.Path(a).name if i == 0 else a for i, a in enumerate(sys.argv))[:200]}, since {time.strftime('%H:%M:%S')}\n")
    f.flush()
    _lock = f
    os.environ["TLD_MACHINES_LOCKED"] = str(os.getpid())   # for the tests a suite starts


class Agent:
    """Bridge commands on the second machine, inside its own protected session (see mpagent.py)."""
    def __init__(self, remote=REMOTE_HOST):
        remote = remote or tldenv.need_second()
        lock_machines()
        env = dict(os.environ, TLD_REMOTE=remote)
        with phase("agent"):
            self.p = subprocess.Popen([sys.executable, str(ROOT / "tools/mpagent.py")], env=env, stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, text=True, bufsize=1)
            self.read()

    def read(self):
        while True:
            line = self.p.stdout.readline()
            if not line: raise RuntimeError("agent ended")
            line = line.strip()
            if line.startswith("{"): return json.loads(line)
            print("   [laptop]", line)

    def __call__(self, cmd):
        self.p.stdin.write(cmd + "\n"); self.p.stdin.flush()
        r = self.read()
        if "error" in r: raise RuntimeError(f"laptop {cmd}: {r['error']}")
        return r

    def close(self):
        try:
            self.p.stdin.close(); self.p.wait(timeout=120)
            for line in self.p.stdout: print("   [laptop]", line.strip())
        except Exception as e:
            print("agent close:", e)


def local_build_hash():
    dll = game.plugin_dll()
    return hashlib.sha1(dll.read_bytes()).hexdigest()


def running_build(remote=None):
    from bench import Bridge
    try: return Bridge(remote)("ping").get("build")
    except Exception: return None


# ---- skip the build when nothing it reads changed, and the deploy when the games already have these files
STAMP = ROOT / "src/TLDRevamp/bin/.tld-build-stamp.json"
BUILD_INPUTS = ("src", "packaging/approved-mods.txt", "tools/build.sh", "thirdparty/TLDLoaderMods")


def source_fingerprint():
    """sha1 over every file the build reads (src/ without bin/ and obj/, the approved-mods list, build.sh, the
    third-party Loader mods) and the example mods a test asks for."""
    h = hashlib.sha1(("mods:" + ",".join(game.test_mods())).encode())
    for top in BUILD_INPUTS:
        base = ROOT / top
        files = [base] if base.is_file() else sorted(p for p in base.rglob("*") if p.is_file()) if base.is_dir() else []
        for p in files:
            rel = p.relative_to(ROOT)
            if "bin" in rel.parts or "obj" in rel.parts: continue
            h.update(str(rel).encode() + b"\0" + p.read_bytes() + b"\0")
    return h.hexdigest()


def _sha1(p):
    try: return hashlib.sha1(pathlib.Path(p).read_bytes()).hexdigest()
    except OSError: return None


def build_needed():
    """True unless the last build (its stamp) was made from these exact sources and its DLL is still the one in bin/."""
    try: st = json.loads(STAMP.read_text())
    except (OSError, ValueError): return True
    return st.get("src") != source_fingerprint() or st.get("dll") != _sha1(game.plugin_dll())


def build_now():
    game.build()
    try: STAMP.write_text(json.dumps({"src": source_fingerprint(), "dll": _sha1(game.plugin_dll()), "at": time.strftime("%F %T")}))
    except OSError: pass


def _deploy_files():
    """(built file, its name in BepInEx/plugins/TLDRevamp) for everything deploy copies there"""
    stub = ROOT / "src/TLDLoaderShim/bin/Release/netstandard2.1/TLDLoader.dll"
    return [(f, f.name) for f in (game.plugin_dll(), game.MP_API_DLL, game.APPROVED_MODS, stub) if f.exists()]


def _want_deployed():
    """{path under the game folder: sha1, or None for "must not be there"} for a deploy of this build"""
    want = {f"BepInEx/plugins/TLDRevamp/{n}": _sha1(f) for f, n in _deploy_files()}
    for m in game.EXAMPLE_MODS:
        want[f"BepInEx/plugins/{m}/{m}.dll"] = _sha1(game.mod_dll(m)) if m in game.test_mods() else None
    for m in sorted((ROOT / "thirdparty/TLDLoaderMods").glob("*.dll")):
        want[f"BepInEx/TLDLoaderMods/{m.name}"] = _sha1(m)
    return want


def deployed_ok(remote=None):
    """Does this machine's (or the second one's) game already have exactly this build's files, and the bridge on?"""
    want = _want_deployed()
    if remote is None:
        g = tldenv.GAME_DIR
        have = {k: _sha1(g / k) for k in want}
        cfg = g / "BepInEx/config/tldrevamp.core.cfg"
        bridge = cfg.exists() and any(l.strip() == "Bridge = true" for l in cfg.read_text().splitlines())
    else:
        q = tldenv.remote_quote
        cmd = f"cd {q(tldenv.REMOTE_GAME)} && " + " ; ".join(f"sha1sum {q(k)} 2>/dev/null" for k in want) + \
              " ; grep -c '^Bridge = true' BepInEx/config/tldrevamp.core.cfg 2>/dev/null; true"
        r = subprocess.run(tldenv.ssh_args(remote)[:-1] + bench.SSH_MUX + [remote, cmd], capture_output=True, text=True)
        if r.returncode != 0: return False
        have = {k: None for k in want}
        lines = r.stdout.split("\n")
        for line in lines:
            parts = line.split(None, 1)
            if len(parts) == 2 and parts[1] in have: have[parts[1]] = parts[0]
        bridge = any(l.strip().isdigit() and int(l) > 0 for l in lines[-3:])
    return bridge and all(have[k] == v for k, v in want.items())


class _Side(threading.Thread):
    """One machine's restart, run next to the other's; its exception (sys.exit too) re-raised by result()"""
    def __init__(self, fn):
        super().__init__(daemon=True); self.fn, self.exc = fn, None; self.start()
    def run(self):
        try: self.fn()
        except BaseException as e: self.exc = e
    def result(self):
        self.join()
        if self.exc: raise self.exc


# ---- reuse (the owner's rule since 2026-10-17): tests run back to back keep both games when the last test on them ended
# clean. TLD_REUSE=0: always fresh games; TLD_REUSE=1: keep any game already on this build.
REUSE_STATE = tldenv.CACHE / "reuse-state.json"
_dirty = []
_FAIL = __import__("re").compile(r"^\s*FAIL\b")


def _mark_dirty(why):
    if not _dirty: _dirty.append(why)


def _write_state(build):
    try: REUSE_STATE.write_text(json.dumps({"clean": not _dirty, "why": _dirty[:1], "build": build, "at": time.time()}))
    except OSError: pass


class _Watch:
    """stdout as before; a FAIL line marks the run dirty (the next test gets fresh games)"""
    def __init__(self, f): self._f, self._buf = f, ""
    def write(self, s):
        self._buf += s
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            if _FAIL.match(line): _mark_dirty("a FAIL")
        return self._f.write(s)
    def __getattr__(self, k): return getattr(self._f, k)


def _watch_run(build):
    """dirty until the run ends clean: a kill leaves it dirty; a FAIL, an error exit or an exception keep it dirty"""
    if isinstance(sys.stdout, _Watch): return
    _write_state(build); _dirty.append("running")
    sys.stdout = _Watch(sys.stdout)
    real_exit, real_hook = sys.exit, sys.excepthook
    def _exit(code=None):
        if code not in (None, 0): _mark_dirty(f"exit {str(code)[:60]}")
        real_exit(code)
    def _hook(*a): _mark_dirty("an exception"); real_hook(*a)
    sys.exit, sys.excepthook = _exit, _hook
    def _end():
        if _dirty and _dirty[0] == "running": _dirty.pop(0)
        _write_state(build)
    atexit.register(_end)


def reuse_decision(want):
    """(keep the games?, why)"""
    v = os.environ.get("TLD_REUSE", "")
    if v == "0": return False, "TLD_REUSE=0"
    if v == "1": return True, "TLD_REUSE=1"
    try: st = json.loads(REUSE_STATE.read_text())
    except (OSError, ValueError): return False, "no earlier test recorded"
    if not st.get("clean"): return False, f"the last test didn't end clean ({(st.get('why') or ['?'])[0]})"
    if st.get("build") != want: return False, "another build"
    if time.time() - st.get("at", 0) > float(os.environ.get("TLD_REUSE_MAX_AGE", 3600)): return False, "the last test was over an hour ago"
    return True, "the last test on these games ended clean"


def ensure_both(build=True, restart=True):
    """Build (optional), deploy to the laptop, and restart both games, or keep them (the owner's rule, 2026-10-17,
    replacing 2026-09-30's fresh games for every run): tests run back to back keep both games when they run this build
    with these files and the last test on them ended clean — the session stopped, the test's own fresh same-seed world
    follows. A FAIL, an error, an exception, a failed join or a rebuild: fresh games for the next test. restart=False
    keeps a matching game whatever came before; TLD_REUSE=0 always restarts (the full tier of tools/suite.py).
    The two machines restart at the same time; the build is skipped when no source changed since the last one, the
    deploy when a game already has the files."""
    lock_machines()
    t0 = time.time()
    env = dict(os.environ, TLD_REMOTE=REMOTE_HOST)
    game_py = [sys.executable, str(ROOT / "tools/game.py")]
    need_build = build and build_needed()
    if restart and not need_build:
        keep, why = reuse_decision(local_build_hash() if game.plugin_dll().exists() else None)
        print(f"ensure_both: {'keeping the games' if keep else 'fresh games'} ({why})", flush=True)
        if keep: restart = False
    if build and not need_build: print("ensure_both: no source changed since the last build — not building again", flush=True)
    # the laptop's quit waits for nothing here: it runs during the build
    lap_quit = _Side(lambda: subprocess.run(game_py + ["quit"], env=env)) if restart else None
    if (restart or need_build) and game.running(): game.quit_game()   # never copy DLLs under a running game
    tb = time.time()
    if need_build: build_now()
    built = time.time() - tb
    PHASES["build"] = PHASES.get("build", 0.0) + built
    want = local_build_hash()
    _watch_run(want)

    def bring_up(label, launch, alive, getter):
        # a restarted game needs tens of seconds (Proton boot + world load) before its bridge answers. Tests that fired
        # commands into that window hung or errored weirdly all night ("the script stops on the laptop"): gate on the
        # bridge answering ping with the expected build.
        def go():
            try: launch()
            except SystemExit as ex: print(f"ensure_both: the {label}'s launch: {ex}", flush=True)
        go()
        t1 = time.time(); retried = False
        while time.time() - t1 < 180:
            if getter() == want: break
            # a launch that died right after starting (10-08: four times, no error in its log): once more
            if not retried and time.time() - t1 > 10 and not alive():
                print(f"ensure_both: the {label}'s game is gone — launching it again", flush=True); retried = True; go()
            time.sleep(3)
        got = getter()
        if got != want: raise RuntimeError(f"ensure_both: {label} bridge never came up on build {want} (got {got})")
        print(f"ensure_both: {label} bridge up on {got} after {time.time() - t0:.0f}s", flush=True)

    def kept(label, remote):
        from bench import Bridge
        b = Bridge(remote)
        for c in ("mp stop", "net relay off", "timescale 1"):
            try: b(c)
            except Exception: pass
        KEPT.add(label)
        print(f"ensure_both: the {label}'s game kept (build {want}), session stopped", flush=True)

    def desktop():
        if not restart and not need_build and running_build() == want and deployed_ok() and _in_test_prefix(None):
            return kept("desktop", None)
        print("desktop: loading build", want, flush=True)
        if game.running(): game.quit_game()
        if not deployed_ok(): game.deploy_local()
        bring_up("desktop", game.launch, game.running, running_build)

    def laptop():
        if lap_quit: lap_quit.result()
        rb = running_build(REMOTE_HOST)
        if not restart and rb == want and deployed_ok(REMOTE_HOST) and _in_test_prefix(REMOTE_HOST):
            return kept("laptop", REMOTE_HOST)
        print("laptop: loading build", want, "(was", rb, ")", flush=True)
        if not deployed_ok(REMOTE_HOST):
            subprocess.run(game_py + ["quit"], env=env)
            subprocess.run(game_py + ["deploy"], env=env, check=True)
        elif not restart: subprocess.run(game_py + ["quit"], env=env)
        def alive():
            import launchguard
            return launchguard.run(launchguard.specification(True), "status", REMOTE_HOST)["running"]
        bring_up("laptop", lambda: subprocess.run(game_py + ["launch"], env=env), alive, lambda: running_build(REMOTE_HOST))

    sides = [_Side(desktop), _Side(laptop)]
    for s in sides: s.join()
    for s in sides: s.result()
    PHASES["restart"] = PHASES.get("restart", 0.0) + (time.time() - t0) - built
    return time.time() - t0


def seeded_both(L):
    """Fresh seeded test world on both machines at the same time, then the preflight checks (see preflight)."""
    import seeded
    from bench import Bridge
    with phase("world"):
        t = threading.Thread(target=lambda: L("!seeded"))
        t.start()
        seeded.restart_same_seed()
        t.join()
        h = Bridge()
        # both games keep running at full speed without focus: the owner uses the desktop meanwhile. Unfocused (covered),
        # the game ran at 1 fps (carcopies runs 1-2: 999.95 ms frames) with vsync on — presenting waits for a frame the
        # compositor never asks a hidden window for; runInBackground alone didn't help. vsync off: 59 fps there, the
        # game's own 60 fps cap still holding.
        for s_ in (h, L):
            try: s_("bg on"); s_("set UnityEngine.QualitySettings.vSyncCount 0")
            except Exception: pass
        # TLD_TEST_SETS="Net.Entities.InterestRelay=False;...": runtime switches on both games for an A/B of any test
        for kv in filter(None, os.environ.get("TLD_TEST_SETS", "").split(";")):
            k, v = kv.split("=", 1)
            for label, s_ in (("host", h), ("laptop", L)): print(f"  {label}: set TLDRevamp.{k} {v}:", s_(f"set TLDRevamp.{k} {v}"), flush=True)
    with phase("check"):
        preflight(h, L)
    return h


def _game_prefixes(remote=None):
    """Only the exact leased copy on the selected machine; unrelated games are irrelevant."""
    want = tldenv.REMOTE_TEST_COMPAT if remote else tldenv.TEST_COMPAT
    cmd = (f'echo "want={want}"; for p in $(pgrep -f "TheLongDrive[.]exe"); do tr "\\0" "\\n" < /proc/$p/environ 2>/dev/null '
           '| grep -m1 "^STEAM_COMPAT_DATA_PATH="; done | sort -u')
    if remote: r = subprocess.run(tldenv.ssh_args(remote)[:-1] + bench.SSH_MUX + [remote, cmd], capture_output=True, text=True)
    else: r = subprocess.run(["sh", "-c", cmd], capture_output=True, text=True)
    lines = r.stdout.splitlines()
    want = lines[0][5:].rstrip("/") if lines and lines[0].startswith("want=") else ""
    return want, sorted({l.split("=", 1)[1].rstrip("/") for l in lines[1:] if "=" in l})


def _in_test_prefix(remote):
    """the running game is in the tests' own prefix (or none is set): a game in the player's own prefix is never kept"""
    want, have = _game_prefixes(remote)
    return not want or have == [want]


# ---- the facts the preflight checks (each one is a small, read-only question to a machine)

def _prefixes_of(remote=None):
    """the Proton prefixes the running game on that machine actually uses"""
    want, have = _game_prefixes(remote)
    return have


def _build_of(side):
    """the mod build that machine's bridge reports"""
    try:
        p = side("ping")
        return p.get("build") if isinstance(p, dict) else None
    except Exception:
        return None


def _reported_port_of(side):
    """the port the bridge SAYS it listens on.

    This is what makes the preflight's bridge-port check able to fail: the wanted port and the reported
    port used to be the same expression, so they were always equal and the check was vacuous.
    """
    try:
        p = side("ping")
        return p.get("port") if isinstance(p, dict) else None
    except Exception:
        return None


def _reported_data_path_of(side):
    """the game's own UnityEngine.Application.dataPath, read back from the bridge"""
    try:
        p = side("ping")
        return p.get("dataPath") if isinstance(p, dict) else None
    except Exception:
        return None


def _wanted_data_path(game_dir, remote=None):
    """the data folder of the copy this run asked for, as the game itself spells it.

    On the second machine the game directory is a path in THAT machine's shell (`$HOME/...`), so it is
    expanded there: the wanted path has to be the folder the remote game really uses, not the literal
    string with the dollar sign in it.
    """
    if not game_dir:
        return ""
    if remote:
        q = tldenv.remote_quote
        cmd = f"echo {q(str(game_dir).rstrip('/') + '/TheLongDrive_Data')}"
        try:
            r = subprocess.run(tldenv.ssh_args(remote)[:-1] + bench.SSH_MUX + [remote, cmd],
                               capture_output=True, text=True, timeout=30)
        except Exception:
            return ""      # unknown: the preflight then refuses (never assumes)
        out = r.stdout.strip().splitlines()
        return out[-1].strip() if (r.returncode == 0 and out) else ""
    return os.path.join(str(game_dir), "TheLongDrive_Data")


def _steam_of(side):
    """the Steam account that machine's game is signed in with"""
    try:
        return side("net me").get("steamId")
    except Exception:
        return None


def _bg_of(side):
    """is the copy's runInBackground on (it keeps ticking when the window is not focused)?"""
    try:
        return side("bg").get("runInBackground")
    except Exception:
        return None


def _vsync_of(side):
    """the bridge's vsync count on that machine"""
    try:
        return side("get UnityEngine.QualitySettings.vSyncCount").get("value")
    except Exception:
        return None


def _window_of(side):
    """(fullscreen, width, height) of that machine's copy"""
    try:
        full = side("get UnityEngine.Screen.fullScreenMode").get("value")
        w = side("get UnityEngine.Screen.width").get("value")
        h = side("get UnityEngine.Screen.height").get("value")
        return (0 if str(full) in ("0", "FullScreenMode.Windowed", "FullScreenMode.MaximizedWindow") else 1, w, h)
    except Exception:
        return (0, None, None)


# ---- scoped cleanup: this run removes exactly what it created, and nothing else

_CLEANED = set()


def cleanup_run(remote=None, ports=None):
    """Remove this run's SSH tunnel and control socket. Nothing else is touched.

    Scoped on purpose: the other workers' copies, prefixes, sockets and ports are left alone - a machine-wide
    pkill or a shared socket would break a run that is not ours. The game's own processes are the test's business
    (tools/game.py quit, which is prefix-scoped), not this one's.
    """
    remote = remote or REMOTE_HOST
    ports = ports or bench.PORTS
    key = (remote, ports.name)
    if key in _CLEANED:
        return {"tunnel": {}, "sockets": [], "kept": ["already cleaned up"]}
    _CLEANED.add(key)
    out = {"tunnel": {}, "sockets": [], "kept": []}
    if not remote:
        return out
    out["tunnel"] = bench.close_tunnel(remote, ports.tunnel_port)
    out["sockets"] = relaynet.remove_control_socket(ports)
    if not out["tunnel"].get("forward") and not out["tunnel"].get("master"):
        out["kept"].append("no tunnel of this run was open")
    return out


@atexit.register
def _cleanup_at_exit():
    """A run leaves no tunnel and no control socket behind: only its own, never another worker's."""
    try:
        out = cleanup_run()
        if out.get("tunnel", {}).get("forward") or out.get("sockets"):
            print(f"relay cleanup: tunnel removed={out['tunnel'].get('forward')}, "
                  f"control socket files removed={len(out['sockets'])}", flush=True)
    except Exception as e:
        print(f"relay cleanup: {e}", flush=True)


def preflight(h, L):
    """Abort in seconds, with what's wrong, instead of running a test that can't mean anything: both games in the
    tests' own Proton prefix (when one is set), running in the background with vsync off and making frame PROGRESS
    (the floor is 0.9x the 30 FPS cap, so a copy at its deliberate cap passes and one that does not progress fails),
    the game's auto shift on (the autopilot stays in first gear without it), and the laptop on another Steam account
    than the host. TLD_PREFLIGHT=0 skips it.

    The checks themselves live in tools/relaypreflight.py, as pure functions over what the machines said, so they
    can be fake-tested without a game. This function gathers the facts and reports the result."""
    # TLD_PREFLIGHT=0 skips the *scenario* checks, but the SAFETY profile (the hidden window, the resolution,
    # the quality, the limiter, the mute, the prefix) can never be switched off: a test that runs against a
    # copy which is not the profile the launch policy requires does not mean anything.
    skip_scenario = os.environ.get("TLD_PREFLIGHT") == "0"
    bad = []
    sides = (("desktop", h, None), ("laptop", L, REMOTE_HOST))
    in_prefix = {}
    for label, s, remote in sides:
        want, have = _game_prefixes(remote)
        in_prefix[label] = bool(want) and have == [want]
        if not want: print(f"preflight: no test prefix set for the {label} (TLD_{'REMOTE_' if remote else ''}TEST_COMPAT): the game runs in its own")
        elif not in_prefix[label]: bad.append(f"the {label}'s game is not in the test prefix {want} (it runs in {have or 'no Proton prefix'})")
    for label, s, _ in sides:
        try:
            if not s("bg").get("runInBackground"): bad.append(f"the {label}'s game stops when unfocused (bg off)")
            if str(s("get UnityEngine.QualitySettings.vSyncCount").get("value")) != "0": bad.append(f"vsync on on the {label} (1 fps unfocused)")
            if str(s("get settingsscript.s.S.BAutoShift").get("value")) != "True":
                # in the test prefix the game's settings are the tests' own: switch it on; elsewhere they are the player's
                if in_prefix[label]: s("set settingsscript.s.S.BAutoShift True"); print(f"preflight: auto shift switched on on the {label}")
                else: bad.append(f"auto shift off on the {label} (the autopilot stays in first gear)")
        except RuntimeError as ex: bad.append(f"the {label}'s bridge: {ex}")
    if skip_scenario:
        # the scenario checks below are skipped, but the safety profile is not (it is checked above and below)
        pass
    # the runtime profile of each side (the hidden window, the resolution, the quality, the limiter, the mute):
    # the copy must be proven to be the profile the launch policy requires before a test may run.
    import testdisplay
    for label, side, remote in sides:
        try:
            profile = "remote" if remote else "desktop"
            testdisplay.check(testdisplay.verify(side, profile), profile)
        except RuntimeError as ex:
            bad.append(f"{label} profile: {ex}")
    # The frame-progress floor is a PROGRESS check, not a speed check: a copy at its deliberate 30 FPS cap
    # must pass, so the floor is below the cap. 30 * 0.9 = 27: a copy that does not progress still fails.
    floor = float(os.environ.get("TLD_PREFLIGHT_FPS", 30)) * 0.9
    for attempt in range(2):
        fps = _fps_both(sides)
        low = {k: v for k, v in fps.items() if v is None or v < floor}
        if not low: break
        if attempt == 0: time.sleep(3)   # one more look: a hitch while the world finished loading
    print("preflight: " + ", ".join(f"{k} {'?' if v is None else f'{v:.0f}'} fps" for k, v in fps.items()), flush=True)
    for k, v in low.items(): bad.append(f"the {k} runs at {'?' if v is None else f'{v:.0f}'} fps (< {floor:.0f})")
    try:
        if h("net me")["steamId"] == L("net me")["steamId"]: bad.append("the laptop is on the host's Steam account (sign it in to the second one)")
    except (RuntimeError, KeyError) as ex: bad.append(f"Steam ids: {ex}")

    # the ownership checks: the bridge each machine answers on, the build each reports, and the tunnel.
    # These are SCENARIO checks (is this the right build, on the right port, in the right prefix), so they are
    # skipped with TLD_PREFLIGHT=0 - unlike the safety profile above, which is never skipped.
    if skip_scenario:
        if bad: sys.exit("PREFLIGHT FAILED:\n  " + "\n  ".join(bad))
        return
    facts = {"host_prefix": (tldenv.TEST_COMPAT, in_prefix.get("desktop") and _prefixes_of(None) or []),
             "client_prefix": (tldenv.REMOTE_TEST_COMPAT, _prefixes_of(REMOTE_HOST)),
             "host_build": _build_of(h), "client_build": _build_of(L),
             "host_steam_id": _steam_of(h), "client_steam_id": _steam_of(L),
             "host_bridge_port": bench.PORTS.bridge_port, "client_bridge_port": bench.PORTS.remote_bridge_port,
             # the REPORTED port is what each bridge says it listens on: it is observed, never assumed
             "host_bridge_port_reported": _reported_port_of(h),
             "client_bridge_port_reported": _reported_port_of(L),
             # the copy this run asked for, and the game's own dataPath: they have to be the SAME folder
             "host_game_data_path": _wanted_data_path(tldenv.GAME_DIR),
             "client_game_data_path": _wanted_data_path(tldenv.REMOTE_GAME, remote=REMOTE_HOST),
             "host_game_data_path_reported": _reported_data_path_of(h),
             "client_game_data_path_reported": _reported_data_path_of(L),
             "tunnel_port": bench.PORTS.tunnel_port, "remote_bridge_port": bench.PORTS.remote_bridge_port,
             "tunnel_target": relaynet.tunnel_target(bench.PORTS.tunnel_port),
             "host_bg": _bg_of(h), "client_bg": _bg_of(L),
             "host_vsync": _vsync_of(h), "client_vsync": _vsync_of(L),
             "host_window": _window_of(h), "client_window": _window_of(L),
             "host_fps": fps.get("desktop"), "client_fps": fps.get("laptop")}
    report = relaypreflight.run(facts)
    for b in report.bad:
        if b not in bad: bad.append(b)
    if bad: sys.exit("PREFLIGHT FAILED:\n  " + "\n  ".join(bad))


def _fps_both(sides, seconds=2.0):
    def frames(s):
        try: return float(s("get UnityEngine.Time.frameCount")["value"]), time.time()
        except Exception: return None
    a = {label: frames(s) for label, s, _ in sides}
    time.sleep(seconds)
    b = {label: frames(s) for label, s, _ in sides}
    return {k: ((b[k][0] - a[k][0]) / (b[k][1] - a[k][1]) if a[k] and b[k] else None) for k in a}


def relay_both(h, L, relay, tries=4):
    """Both machines onto the relay; a timeout (the laptop's Steam relay network not ready yet) is tried again."""
    with phase("relay"):
        for side in (h, L):
            for k in range(tries):
                try: side(f"net relay {relay}"); break
                except RuntimeError as ex:
                    if k == tries - 1: raise
                    print(f"relay_both: {ex}; again", flush=True); time.sleep(10)


def join_both(h, L, tries=3, wait=60, rehost=True):
    """The laptop joins the host (the relay set by the caller); a connection lost while it is being made is tried again
    (2026-10-14: two of four first joins over iad ended "Lost connection to the host" before the host saw anything).
    God mode a side had before the join is on again after it (a laptop that lost it at the join died mid-test).
    rehost=False: a player joining AGAIN into the host's running session (a rehost is a new session: every object gets
    a new network id, so a test following an object by its id across it follows another object — gluepick E, 2026-10-22)."""
    with phase("join"):
        gods = {}
        for label, s in (("host", h), ("laptop", L)):
            try: gods[label] = str(s("get kaposztaleves.s.settings.god").get("value")) == "True"
            except Exception: pass
        for k in range(tries):
            if rehost or k > 0: h("mp host 27070")
            L(f"mp joinid {h('net me')['steamId']}")
            t0 = time.time()
            while time.time() - t0 < wait:
                try:
                    if L("mp status")["client"]["ready"]:
                        for label, s in (("host", h), ("laptop", L)):
                            if gods.get(label) and str(s("get kaposztaleves.s.settings.god").get("value")) != "True":
                                print(f"join_both: the {label} lost god mode at the join — on again", flush=True)
                                s("set kaposztaleves.s.settings.god True")
                        return True
                except Exception: pass
                time.sleep(1)
            try: last = (L("mp status").get("client") or {}).get("last", "no client")
            except Exception as ex: last = str(ex)[:120]
            print(f"join_both: try {k + 1} failed ({last}), again", flush=True)
            L("mp stop"); h("mp stop"); time.sleep(3)
        _mark_dirty("a failed join")
        return False


def restart_both(build=True):
    if build:
        game.build()
        r = subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "deploy"], env=dict(os.environ, TLD_REMOTE=REMOTE_HOST))
        if r.returncode != 0: sys.exit("deploy failed")
    game.quit_game(); game.launch()
    env = dict(os.environ, TLD_REMOTE=REMOTE_HOST)
    subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "quit"], env=env)
    subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "launch"], env=env)


def compare_items(a, b, tol=0.3):
    """Match two machines' item lists (model id + global position): each item needs a partner of the same model within
    `tol` metres. Returns (matched, only_a, only_b, worst distance of the matched)."""
    import math
    left = list(b)
    matched, only_a, worst = 0, [], 0.0
    for it in a:
        best, bi = None, -1
        for i, o in enumerate(left):
            if o[0] != it[0]: continue
            d = math.dist(it[1:], o[1:])
            if best is None or d < best: best, bi = d, i
        if best is not None and best <= tol:
            matched += 1; worst = max(worst, best); left.pop(bi)
        else:
            only_a.append((it[0], round(it[1], 1), round(it[3], 1), None if best is None else round(best, 2)))
    return matched, only_a, [(o[0], round(o[1], 1), round(o[3], 1)) for o in left], worst
