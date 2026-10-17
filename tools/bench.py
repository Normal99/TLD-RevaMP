#!/usr/bin/env python3
"""Run benchmark scenarios against the running game via the debug bridge.

  tools/bench.py [label]            run all scenarios, save bench/<timestamp>-<label>.json
  tools/bench.py --profile [label]  same, with the per-script profiler on (time + allocations)
  tools/bench.py --methods "T.M T2.M2" [label]  profile only these methods (all overloads)
  tools/bench.py --set Path=Value [label]      set runtime values for the run, restored afterwards (repeatable)
  tools/bench.py --scenarios idle,drive [label]  choose scenarios (idle, stream, drive)
  tools/bench.py --toggle-idle Path.To.Bool [label]  in-place A/B of a switch while standing still
  tools/bench.py --restore          put back player settings from an interrupted run
  tools/bench.py --compare A B      compare two result files

Scenarios:
  idle    stand still for 20s (baseline render/sim cost)
  stream  step the player forward 400m every 2s for 40s (burst-loads new areas; worst case)
  drive   glide along the ground at 30 m/s for 60s (gradual loading, like real driving)
"""
import json, socket, sys, time, datetime, pathlib, os, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import tldenv

ROOT = pathlib.Path(__file__).resolve().parent.parent
BENCH = ROOT / "bench"


# Another machine (TLD_REMOTE=user@host, tools/README.md): the bridge is reached through an SSH tunnel; the bridge
# itself stays on the remote's 127.0.0.1 (it can run anything in the game — never expose it to the network).
REMOTE = os.environ.get("TLD_REMOTE")
SSH_KEY = tldenv.SSH_KEY
PORT = int(os.environ.get("TLD_BRIDGE_PORT", "27051" if REMOTE else "27050"))
RESTORE_FILE = BENCH / (".restore-pending.json" if not REMOTE else f".restore-pending-{REMOTE.split('@')[-1]}.json")


# One SSH connection reused for every command (ControlMaster): a test opens dozens; over a slow link (a phone hotspot)
# the second machine's sshd reset new connections during the handshake.
SSH_MUX = ["-o", "ControlMaster=auto", "-o", "ControlPath=~/.ssh/tld-mux-%C", "-o", "ControlPersist=120"]


def ssh_base():
    return ["ssh"] + (["-i", SSH_KEY] if SSH_KEY else []) + ["-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15"] + SSH_MUX + [REMOTE]


def ensure_tunnel(remote=None, port=None):
    """Local PORT → remote 127.0.0.1:27050, started once (a free local port means no tunnel yet)."""
    remote = remote or REMOTE; port = port or PORT
    probe = socket.socket()
    try:
        probe.bind(("127.0.0.1", port))
    except OSError:
        return  # already listening: the tunnel is up
    finally:
        probe.close()
    base = ["ssh"] + (["-i", SSH_KEY] if SSH_KEY else []) + ["-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15"]
    subprocess.run(base + ["-f", "-N", "-o", "ExitOnForwardFailure=yes", "-L", f"127.0.0.1:{port}:127.0.0.1:27050", remote], check=True)
    time.sleep(0.5)
GOD = "kaposztaleves.s.settings.god"
SURVIVAL = "gamemodesettingsscript.s.S.survival"   # 2 = hunger/thirst on, 1 = off
FOOD = "mainscript.s.player.survival.food"
WATER = "mainscript.s.player.survival.water"
HP = "mainscript.s.player.survival.hp"
HP_KEY = "__hp"  # not a plain set: restored by healing


def apply_changes(b, changes):
    """Record the player's current values to disk FIRST, then change them. Returns the restore dict."""
    if RESTORE_FILE.exists():
        sys.exit(f"{RESTORE_FILE} exists: an earlier run never restored the player's settings.\n"
                 f"Run `tools/bench.py --restore` first.")
    restore = {}
    for path, _ in changes:
        restore.setdefault(path, b(f"get {path}")["value"])
    # Character state: God mode blocks Death() but not damage, so starvation during a long bench left HP at 0 and
    # killed the character the moment god mode was restored (2026-09-24). Keep the pre-run values to put back.
    for path in (FOOD, WATER):
        restore.setdefault(path, b(f"get {path}")["value"])
    restore[HP_KEY] = b(f"get {HP}")["value"]
    RESTORE_FILE.write_text(json.dumps(restore, indent=2))
    for path, val in changes:
        b(f"set {path} {val}")
    return restore


def restore_settings(restore=None):
    """Put the player's values back over a FRESH connection, each independently. Deletes the journal only if all succeed."""
    if restore is None:
        if not RESTORE_FILE.exists():
            print("nothing to restore"); return True
        restore = json.loads(RESTORE_FILE.read_text())
    ok = True
    try:
        b = Bridge()
    except OSError as e:
        print("!! cannot reach the game to restore settings:", e, f"(kept {RESTORE_FILE})"); return False
    try:
        if b("get mainscript.s.died")["value"] is True:
            print("!! the character is dead; settings will still be restored")
    except Exception:
        pass
    for path, val in restore.items():
        if path in (GOD, HP_KEY):
            continue
        try:
            b(f"set {path} {val}")
        except Exception as e:
            ok = False
            print(f"!! restore failed for {path}={val}: {e}")
    if HP_KEY in restore:
        try:
            lost = float(restore[HP_KEY]) - float(b(f"get {HP}")["value"])
            if lost > 0:
                b(f"call mainscript.s.player.survival.HealInstant {lost}")
        except Exception as e:
            ok = False
            print("!! could not restore health:", e)
    if GOD in restore:  # last, once the character is safe again
        try:
            b(f"set {GOD} {restore[GOD]}")
        except Exception as e:
            ok = False
            print(f"!! restore failed for {GOD}: {e}")
    if ok:
        RESTORE_FILE.unlink(missing_ok=True)
    else:
        print(f"!! some settings not restored; journal kept at {RESTORE_FILE}")
    return ok


class Bridge:
    """Bridge to this target (TLD_REMOTE or local); Bridge(remote="user@host") reaches another machine directly."""
    def __init__(self, remote=None):
        port = PORT
        if remote:
            port = 27051; ensure_tunnel(remote, port)
        elif REMOTE:
            ensure_tunnel()
        self.s = socket.create_connection(("127.0.0.1", port), timeout=35)
        self.f = self.s.makefile("rw", encoding="utf-8", newline="\n")

    def __call__(self, cmd):
        self.f.write(cmd + "\n"); self.f.flush()
        line = self.f.readline()
        if not line:
            raise ConnectionError(f"{cmd}: bridge closed the connection")
        r = json.loads(line)
        if "error" in r:
            raise RuntimeError(f"{cmd}: {r['error']}" + (f" {r['group']}" if "group" in r else ""))
        return r


def vec(s):
    return [float(x) for x in s.split(",")]


PLAYER_LOG = tldenv.PLAYER_LOG
_log_pos = None


def game_errors():
    """New exception lines the game wrote to Player.log since the last call (first call just sets the baseline)."""
    global _log_pos
    try:
        size = PLAYER_LOG.stat().st_size
    except OSError:
        return []
    if _log_pos is None or size < _log_pos:
        _log_pos = size
        return []
    with open(PLAYER_LOG, "rb") as f:
        f.seek(_log_pos)
        new = f.read().decode("utf-8", "replace")
    _log_pos = size
    return [l for l in new.splitlines() if "Exception" in l and not l.startswith(" ")]


def in_world(b):
    # Cheap flags only: the `scene` command walks every object (~12 ms) and caused slow frames when polled.
    # The menu scene has a stray rigidbody, so use the game's own menu flag plus a live player object.
    if b("get DataFromMenuScript.s.mainmenu")["value"] is not False:
        return False
    try:
        return b("get mainscript.s.player")["value"] is not None
    except RuntimeError:
        return False


def check_alive(b):
    errs = game_errors()
    if errs:
        # A game exception during world generation can stall the whole generator (no ground → falling). Stop at once.
        raise RuntimeError("game threw during the run: " + errs[0][:200])
    if not in_world(b):
        raise RuntimeError("left the game world (main menu?)")
    if b("get mainscript.s.died")["value"] is True:
        raise RuntimeError("player died")


def scenario_idle(b):
    b("reset"); time.sleep(20)
    return b("stats")


def place_on_ground(b, x, z, hover_y, timeout=15.0):
    """Park the player high above (x,z) until the terrain there exists, then set them on it.
    Only lands on the terrain itself: static props like Bigrock meshes let the player drop through the world,
    so if something else is on top the spot shifts sideways and we try again."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        b(f"teleport {x} {hover_y} {z}")  # re-park each poll so gravity doesn't carry them off
        b("set mainscript.s.player.godModeTillGrounded True")
        errs = game_errors()
        if errs:
            raise RuntimeError("game threw while waiting for terrain: " + errs[0][:200])
        g = b(f"ground {x} {z}")
        if g["hit"] and g["collider"] != "Terrain":
            x += 30
            continue
        if g["hit"]:
            b(f"teleport {x} {g['y'] + 1.5} {z}")
            b("set mainscript.s.player.godModeTillGrounded True")
            return g["y"], time.time() - t0, f"{g['type']}:{g['collider']}"
        time.sleep(0.1)
    raise RuntimeError(f"terrain never appeared under ({x:.0f},{z:.0f}) within {timeout}s")


def scenario_stream(b):
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    b("reset")
    waits, invalid, landed = [], [], []
    for i in range(20):
        x, y, z = vec(b("get mainscript.s.player.transform.position")["value"])
        z += 400
        ground_y, waited, what = place_on_ground(b, x, z, y + 300)
        waits.append(round(waited, 2))
        landed.append(what)
        time.sleep(2)
        if b("get mainscript.s.died")["value"] is True:
            raise RuntimeError(f"player died at step {i + 1}")
        # The game has a floating origin (mainscript.visszarakva) that shifts all Unity coords when the
        # player gets far out, so re-measure the ground under the player *now* instead of reusing ground_y.
        px, py, pz = vec(b("get mainscript.s.player.transform.position")["value"])
        g = b(f"ground {px} {pz}")
        if g["hit"] and py < g["y"] - 3:
            invalid.append(f"step {i + 1}: player y {py:.1f} below ground {g['y']:.1f}")
    stats = b("stats")
    stats["groundWaitSec"] = waits
    stats["landedOn"] = sorted(set(landed))
    stats["valid"] = not invalid
    if invalid:
        stats["invalid"] = invalid
        print("   !! RUN INVALID:", invalid)
    place_on_ground(b, home[0], home[2], home[1] + 300)
    return stats


def scenario_drive(b, speed=30.0, seconds=60.0, step_hz=20.0, height=1.5, heading_deg=0.0):
    """Move the player smoothly along +z at car speed, hugging the ground: world gen loads gradually like real
    driving, instead of the 400 m teleport bursts of `stream`."""
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    x, y, z = home
    b("reset")
    dt = 1.0 / step_hz
    t_end = time.time() + seconds
    import math
    hx, hz = math.sin(math.radians(heading_deg)), math.cos(math.radians(heading_deg))
    no_ground, steps = 0, 0
    next_t = time.time()
    while time.time() < t_end:
        steps += 1
        if steps % int(step_hz) == 0:
            check_alive(b)
        x += speed * dt * hx
        z += speed * dt * hz
        g = b(f"ground {x} {z}")
        if g["hit"]:
            y = g["y"] + height  # terrain, road or other static ground
        else:
            no_ground += 1  # nothing generated there yet; keep last height
        b(f"teleport {x} {y} {z}")
        b("set mainscript.s.player.godModeTillGrounded True")
        next_t += dt
        time.sleep(max(0.0, next_t - time.time()))
    stats = b("stats")
    stats["distanceM"] = round(((x - home[0]) ** 2 + (z - home[2]) ** 2) ** 0.5)
    stats["stepsWithoutGround"] = no_ground
    place_on_ground(b, home[0], home[2], home[1] + 300)
    return stats


def scenario_fly_fresh(b):
    """Flying like dev fly mode: 60 m/s, 80 m above the ground, through a never-visited area."""
    return scenario_drive_fresh(b, drive=lambda b_: scenario_drive(b_, speed=60.0, height=80.0))


def scenario_drive_fresh(b, drive=None):
    """Like `drive`, but in a never-visited area: the game keeps building/object layouts of visited areas, so
    repeating the same route only measures generation on the first run. Jumps 20–60 km in a random direction
    (recorded), lets the area load, then drives."""
    import math, random
    home = vec(b("get mainscript.s.player.transform.position")["value"])
    # Deterministic sequence of areas (no wall-clock randomness): run n uses the n-th fixed offset
    global _fresh_n
    _fresh_n = globals().get("_fresh_n", -1) + 1
    rng = random.Random(1000 + _fresh_n)
    dist, ang = rng.uniform(20000, 60000), rng.uniform(0, 2 * math.pi)
    x, z = home[0] + dist * math.cos(ang), home[2] + dist * math.sin(ang)
    place_on_ground(b, x, z, home[1] + 300, timeout=30)
    time.sleep(8)  # let the surroundings finish loading before measuring
    stats = (drive or scenario_drive)(b)
    stats["freshOffsetM"] = [round(x - home[0]), round(z - home[2])]
    place_on_ground(b, home[0], home[2], home[1] + 300, timeout=30)
    return stats


SCENARIOS = {"idle": scenario_idle, "stream": scenario_stream, "drive": scenario_drive, "drivefresh": scenario_drive_fresh, "flyfresh": scenario_fly_fresh}


def run(label, profile=False, sets=(), scenarios=("idle", "stream"), loopprof=False):
    b = Bridge()
    b("bg on")
    if not in_world(b):
        sys.exit("Not in a game world (main menu?). Start a new game first.")
    # Unfocused windows on Wayland get no vsync callbacks -> 1 FPS. Benchmark uncapped.
    # Also lift the game's own FPS limit (re-applied every frame from settings) and enable dev god mode.
    game_errors()  # baseline: only errors from here on count
    restore = apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"),
                                ("settingsscript.s.S.BEnableFpsLimit", "False"),
                                (GOD, "True"),
                                (SURVIVAL, "1"),  # no hunger/thirst drain or damage during the run
                                # Test worlds must never overwrite the player's autosave slots (they did, 2026-09-23/24)
                                ("TLDRevamp.TestSafety.BlockAutoSave", "True"),
                                # Debug modes must never leak into measurements; a --set later in the list overrides
                                ("TLDRevamp.Fixes.RoadPlaceCache.Verify", "False"),
                                ("TLDRevamp.Fixes.AsyncTerrain.Verify", "False"),
                                ("TLDRevamp.Fixes.RoadNearestPrune.Verify", "False"),
                                ("TLDRevamp.Fixes.ParallelRoadGen.Verify", "False"),
                                ("TLDRevamp.Fixes.DotChunkMemo.Verify", "False"),
                                ("TLDRevamp.Fixes.ParallelObjGen.Verify", "False"),
                                ("TLDRevamp.Fixes.SleepingWheelSkip.Verify", "False"), *sets])
    result = {"label": label, "time": datetime.datetime.now().isoformat(timespec="seconds"),
              "version": b("ping")["version"], "sets": dict(sets), "scene_before": b("scene"), "scenarios": {}}
    if loopprof:
        print("loopprof:", b("loopprof start"))
    if profile:
        # Probes add overhead: frame stats from a profiled run aren't comparable to normal runs
        print("profiler:", b("profile start" + (" " + profile if isinstance(profile, str) else "")))
    try:
        for name, fn in [(n, SCENARIOS[n]) if isinstance(n, str) else n for n in scenarios]:
            print(f"-> {name} ...", flush=True)
            result["scenarios"][name] = fn(b)
            result["scenarios"][name]["breakdown"] = b("breakdown")
            print("   ", {k: v for k, v in result["scenarios"][name].items() if k != "breakdown"}, flush=True)
        result["scene_after"] = b("scene")
        if loopprof:
            result["loopprof"] = b("loopprof stop 40")
            lp = result["loopprof"]
            print(f"\nframe phases: {lp['frames']} frames, {lp['slowFrames']} slow (>{lp['slowThresholdMs']} ms main-thread)")
            print(f"  {'phase':58}{'avg all':>9}{'avg slow':>10}{'max':>9}")
            for ph in lp["phases"][:25]:
                print(f"  {ph['p'][:58]:58}{ph['avgMsAll']:>9}{ph['avgMsSlow']:>10}{ph['maxMs']:>9}")
        if profile:
            result["profile"] = b("profile stop 30")
            print_profile(result["profile"])
    finally:
        if loopprof and "loopprof" not in result:
            try: b("loopprof stop")
            except Exception as e: print("!! loopprof stop failed:", e)
        if profile and "profile" not in result:
            try: b("profile stop")
            except Exception as e: print("!! profile stop failed:", e)
        restore_settings(restore)
    BENCH.mkdir(exist_ok=True)
    out = BENCH / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    out.write_text(json.dumps(result, indent=2))
    print("saved", out.relative_to(ROOT))
    return result


def toggle_idle(path, label, rounds=4, seconds=10):
    """In-place A/B: standing still, alternate a boolean switch off/on `rounds` times, `seconds` each."""
    b = Bridge()
    b("bg on")
    cur = b(f"get {path}")["value"]
    game_errors()  # baseline: only errors from here on count
    restore = apply_changes(b, [("UnityEngine.QualitySettings.vSyncCount", "0"), ("settingsscript.s.S.BEnableFpsLimit", "False"),
                                (GOD, "True"), (SURVIVAL, "1"), ("TLDRevamp.TestSafety.BlockAutoSave", "True"), (path, cur)])
    res = {"False": [], "True": []}
    try:
        time.sleep(3)  # settle
        for r in range(rounds):
            for val in ("False", "True") if r % 2 == 0 else ("True", "False"):
                b(f"set {path} {val}")
                time.sleep(1)
                b("reset"); time.sleep(seconds)
                res[val].append(b("stats"))
    finally:
        restore_settings(restore)
    print(f"{path}: {rounds} alternating rounds x {seconds}s each, same spot")
    for val in ("False", "True"):
        fps = [s["fps"] for s in res[val]]; p99 = [s["p99ms"] for s in res[val]]
        print(f"  {val:5}  fps {fps}  mean {sum(fps)/len(fps):.1f}   p99 {p99}")
    out = BENCH / f"{datetime.datetime.now():%Y%m%d-%H%M%S}-{label}.json"
    out.write_text(json.dumps({"label": label, "path": path, "results": res}, indent=2))
    print("saved", out.relative_to(ROOT))


def print_profile(p):
    print(f"\nscripts total: {p['totalMsPerSec']} ms/s CPU, {p['totalKbPerSec']} KB/s allocated over {p['seconds']}s")
    for key, unit in (("byTime", "msPerSec"), ("byAlloc", "kbPerSec"), ("bySpike", "maxMs")):
        if key not in p: continue
        print(f"\ntop by {key[2:].lower()}:")
        for r in p[key][:15]:
            extra = f"  >5ms x{r['over5ms']}" if key == "bySpike" else ""
            print(f"  {r[unit]:>10} {unit:9} {r['callsPerSec']:>6} calls/s  {r['m']}{extra}")
    if p.get("bySlowFrames"):
        print(f"\nslow frames (> {p['slowFrameMs']} ms): {p['slowFrames']} of {p['frames']}; ms per slow frame vs per frame overall:")
        for r in p["bySlowFrames"][:15]:
            print(f"  {r['msPerSlowFrame']:>8} slow  {r['msPerFrameAll']:>8} all  {r['m']}")


def compare(a, b):
    A, B = (json.loads(pathlib.Path(p).read_text()) for p in (a, b))
    keys = ["fps", "p50ms", "p99ms", "maxms", "hitches50ms", "gc0", "managedMB", "bridgeMsTotal"]
    print(f"{'scenario/metric':24}{A['label']:>14}{B['label']:>14}{'change':>10}")
    for sc in A["scenarios"]:
        for k in keys:
            va, vb = A["scenarios"][sc].get(k), B["scenarios"][sc].get(k)
            if va is None or vb is None:
                continue
            ch = f"{(vb - va) / va * 100:+.0f}%" if va else ""
            print(f"{sc + '/' + k:24}{va:>14}{vb:>14}{ch:>10}")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--restore":
        sys.exit(0 if restore_settings() else 1)
    if len(sys.argv) > 1 and sys.argv[1] == "--compare":
        compare(sys.argv[2], sys.argv[3])
    else:
        args, profile, sets, scen, loopprof = [], False, [], ("idle", "stream"), False
        it = iter(sys.argv[1:])
        for a in it:
            if a == "--profile": profile = True
            elif a == "--loopprof": loopprof = True
            elif a == "--methods": profile = next(it)  # e.g. --methods "roadGenScript.Upd terrainGenScript.Upd"
            elif a == "--set": sets.append(tuple(next(it).split("=", 1)))  # e.g. --set TLDRevamp.Fixes.RoadPlaceCache.Enabled=False
            elif a == "--scenarios": scen = tuple(next(it).split(","))  # e.g. --scenarios idle,drive
            elif a == "--toggle-idle": scen = None; toggle_path = next(it)
            else: args.append(a)
        if scen is None:
            toggle_idle(toggle_path, args[0] if args else "toggle")
        else:
            run(args[0] if args else "run", profile=profile, sets=sets, scenarios=scen, loopprof=loopprof)
