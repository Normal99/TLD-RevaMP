#!/usr/bin/env python3
"""TLD Dedicated Server launcher: prepares a headless dedicated server FROM the local game install (nothing is
shipped or redistributed — the mod drives the normal game headless) and runs it.

  tools/devserver.py [--seed N] [--port 27070] [--watch]

--seed N   force a fresh world with this seed; without it the server CONTINUES its last save (persistence).
The server auto-hosts on the LAN; players connect with the mod's `mp join <this-ip>:<port>`.
"""
import sys, time, socket, argparse, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

p = argparse.ArgumentParser()
p.add_argument("--seed", type=int, default=0)
p.add_argument("--port", type=int, default=27070)
p.add_argument("--watch", action="store_true", help="stay attached and print the server status every 30 s")
a = p.parse_args()

from tldenv import lan_ip

print(f"TLD dedicated server — from the local install, headless (nothing is redistributed)")
print(f"  players connect with:  mp join {lan_ip()}:{a.port}")
game.quit_game()
args = ["steam", "-applaunch", "1017180", "-batchmode", "-nographics"]
if a.seed: args += ["-tldseed", str(a.seed)]
if a.port != 27070: args += ["-tldport", str(a.port)]
subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)

t0 = time.time()
b = None
while time.time() - t0 < 300:
    try:
        c = Bridge(); st = c("mp status")
        if st.get("server"): b = c; break
        time.sleep(3)
    except Exception:
        time.sleep(3)
if b is None:
    print("server did not come up in 300 s — check BepInEx/LogOutput.log"); sys.exit(1)
st = b("mp status")["server"]
print(f"server up in {time.time()-t0:.0f} s — port {st['port']}, peers {st['peers']}, world seed {b('get DataFromMenuScript.s.seed')['value']}")
print("running. Ctrl-C to stop (the world is saved by autosave and `mp save`).")
try:
    while True:
        time.sleep(30)
        st = b("mp status")["server"]
        print(f"  peers {st['peers']} | entities {b('mp entities')['count']} | relayed {st['statesRelayed']}")
except KeyboardInterrupt:
    print("saving and stopping:", b("mp save"), b("mp stop"))
    game.quit_game()
