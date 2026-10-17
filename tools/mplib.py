"""Shared helpers for two-machine scenarios: the remote machine's protected helper (Agent) and restarting both games."""
import sys, os, json, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, bench, tldenv

ROOT = pathlib.Path(__file__).resolve().parent.parent
REMOTE_HOST = tldenv.SECOND       # the second machine ("the laptop" in test output), user@host
DESKTOP_IP = tldenv.lan_ip()      # this machine ("the desktop", the host), as the second one reaches it


class Agent:
    """Bridge commands on the second machine, inside its own protected session (see mpagent.py)."""
    def __init__(self, remote=REMOTE_HOST):
        remote = remote or tldenv.need_second()
        env = dict(os.environ, TLD_REMOTE=remote)
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
    import hashlib
    dll = game.plugin_dll()
    return hashlib.sha1(dll.read_bytes()).hexdigest()[:12]


def running_build(remote=None):
    from bench import Bridge
    try: return Bridge(remote)("ping").get("build")
    except Exception: return None


def ensure_both(build=True, restart=True):
    """Build (optional), deploy to the laptop, and restart both games (the user's rule, 2026-09-30: every test run on
    freshly started games — no state carried over from an earlier run). restart=False keeps a game whose build already
    matches (quick probes only). Deploy always: the autopilot port / TLDLoader stub can change without TLDRevamp.dll."""
    import time
    t0 = time.time()
    if restart and game.running(): game.quit_game()   # never copy DLLs under a running game
    if build: game.build()
    else: game.deploy_local()   # the built DLL on THIS machine too (package.sh rebuilds bin/ without deploying:
                                # the laptop got the new build, the desktop kept the old one, the bridge check hung)
    want = local_build_hash()
    env = dict(os.environ, TLD_REMOTE=REMOTE_HOST)
    if restart or running_build() != want:
        print("desktop: loading build", want)
        if game.running(): game.quit_game()
        game.launch()
    rb = running_build(REMOTE_HOST)
    if restart or rb != want:
        print("laptop: loading build", want, "(was", rb, ")")
        subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "deploy"], env=env, check=True)
        subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "quit"], env=env)
        subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "launch"], env=env)
    # a restarted game needs tens of seconds (Proton boot + world load) before its bridge answers.
    # Tests that fired commands into that window hung or errored weirdly all night ("the script
    # stops on the laptop"): gate on BOTH bridges answering ping with the expected build.
    import time as _t
    def relaunch(label):
        if label == "desktop": game.launch()
        else: subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "launch"], env=env)
    def alive(label):
        if label == "desktop": return game.running()
        return subprocess.run(tldenv.ssh_args(REMOTE_HOST)[:-1] + bench.SSH_MUX + [REMOTE_HOST,
                               "pgrep -f 'TheLongDrive[.]exe'"], capture_output=True).returncode == 0
    for label, getter in (("desktop", lambda: running_build()), ("laptop", lambda: running_build(REMOTE_HOST))):
        t0 = _t.time(); retried = False
        while _t.time() - t0 < 180:
            try:
                if getter() == want: break
            except Exception: pass
            # a launch that died right after starting (10-08: four times, no error in its log): once more
            if not retried and _t.time() - t0 > 10 and not alive(label):
                print(f"ensure_both: the {label}'s game is gone — launching it again"); retried = True; relaunch(label)
            _t.sleep(3)
        got = getter()
        if got != want: raise RuntimeError(f"ensure_both: {label} bridge never came up on build {want} (got {got})")
        print(f"ensure_both: {label} bridge up on {got} after {_t.time()-t0:.0f}s")
    return time.time() - t0


def seeded_both(L):
    """Fresh seeded test world on both machines at the same time."""
    import threading, seeded
    from bench import Bridge
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
    return h


def relay_both(h, L, relay, tries=4):
    """Both machines onto the relay; a timeout (the laptop's Steam relay network not ready yet) is tried again."""
    import time as _t
    for side in (h, L):
        for k in range(tries):
            try: side(f"net relay {relay}"); break
            except RuntimeError as ex:
                if k == tries - 1: raise
                print(f"relay_both: {ex}; again", flush=True); _t.sleep(10)


def join_both(h, L, tries=3, wait=60):
    """The laptop joins the host (the relay set by the caller); a connection lost while it is being made is tried again
    (2026-10-14: two of four first joins over iad ended "Lost connection to the host" before the host saw anything)."""
    import time as _t
    for k in range(tries):
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        t0 = _t.time()
        while _t.time() - t0 < wait:
            try:
                if L("mp status")["client"]["ready"]: return True
            except Exception: pass
            _t.sleep(1)
        try: last = (L("mp status").get("client") or {}).get("last", "no client")
        except Exception as ex: last = str(ex)[:120]
        print(f"join_both: try {k + 1} failed ({last}), again", flush=True)
        L("mp stop"); h("mp stop"); _t.sleep(3)
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
