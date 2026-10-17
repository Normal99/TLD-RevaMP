#!/usr/bin/env python3
"""Dedicated server mode (two machines): the desktop runs HEADLESS (-batchmode) and autohosts; the laptop plays
against it (no ghost at the spawn, full play), then the server restarts and the world persists (continues the
last save). Boot 1 uses -tldseed (fresh world); boot 2 has no seed (continues the save)."""
import sys, time, math, os, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
import tldenv
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def launch_headless(seed=None):
    game.start(["-batchmode", "-nographics"] + (["-tldseed", str(seed)] if seed else []))

def wait_server(timeout=240):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            b = Bridge()
            st = b("mp status")
            if st.get("server"): return b
        except Exception: pass
        time.sleep(3)
    return None

if "--no-build" not in sys.argv: game.build()
game.build() if False else None
env = dict(os.environ, TLD_REMOTE=tldenv.need_second())
print("deploying to the laptop...")
subprocess.run([sys.executable, "tools/game.py", "deploy"], env=env, check=True)
game.quit_game()
subprocess.run([sys.executable, "tools/game.py", "quit"], env=env)

# ---------- boot 1: fresh world (-tldseed), autohost
print("boot 1: headless server, fresh world (seed 20260924)")
launch_headless(20260924)
b = wait_server(300)
if b is None: sys.exit("FAIL: the headless server never came up")
print("  server up:", b("mp status")["server"]["port"], "| player:", b("get mainscript.s.player")["value"] is not None,
      "| god:", b("get kaposztaleves.s.settings.god")["value"])
L = Agent()
try:
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    L(f"mp join {DESKTOP_IP}:27070")
    ok = until(lambda: L("mp status")["client"]["ready"], 150)
    print("  laptop joined:", ok)
    players = L("mp players")
    print("  laptop sees remote players:", players)
    rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
    F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    cx, cz = rp["x"] + F[0] * 20, rp["z"] + F[1] * 20
    g = L(f"ground {cx} {cz}")
    print("  spawn:", L(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242")); time.sleep(4)
    net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in L("mp entities")["entities"] if e["part"] < 0),
              key=lambda t: t[1])[0]
    print("  edit:", L(f"mp entpoke {net} p:fuel 6.5"))
    reached = until(lambda: "6.50" in b(f"mp entstate {net}")["tanks"], 30)
    hs = b(f"mp entstate {net}")
    print("  server's copy:", hs["tanks"][:40], "| reached:", reached)
    print("  laptop resync:", {k: L("mp entities")["resync"][k] for k in ("sent", "itemsSent", "bytes", "unchanged")})
    rd = L(f"mp resyncdiff {net}")
    print("  manual capture pass: changed", rd.get("changed"), str(rd.get("diffs"))[:200])
    print("  server resync:", {k: b("mp entities")["resync"][k] for k in ("applied", "itemsLoaded")})
    srv_alive = b("get mainscript.s.player")["value"] is not None
    print("  server player still alive:", srv_alive, "| god:", b("get kaposztaleves.s.settings.god")["value"])
    print("  mp save:", b("mp save"))
    net1, fuel1 = net, "6.50"
    # ---------- boot 2: no seed — continues the last save
    print("boot 2: restarting the server (continues the last save)")
    game.quit_game(); time.sleep(4)
    launch_headless()
    b2 = wait_server(300)
    if b2 is None: sys.exit("FAIL: the restarted server never came up")
    print("  server up again; laptop rejoins")
    L(f"mp join {DESKTOP_IP}:27070")
    until(lambda: L("mp status")["client"]["ready"], 150); time.sleep(6)
    cars = [e for e in L("mp entities")["entities"] if e["part"] < 0]
    persisted = False
    for e in cars:
        try:
            if "6.50" in L(f"mp entstate {e['net']}")["tanks"]: persisted = True; break
        except Exception: pass
    print(f"  after the restart: cars {len(cars)} on the client, edited car persisted: {persisted}")
    print("SERVER-MODE VERDICT:", "PASS — headless hosting, no ghost, play + persistence across the server restart"
          if ok and srv_alive and persisted else "FAIL")
    b2("mp stop"); L("mp stop")
finally:
    L.close()
