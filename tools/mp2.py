#!/usr/bin/env python3
"""First two-machine multiplayer check: the laptop (TLD_REMOTE machine) hosts, this desktop joins over the LAN.
Both in the seeded test world, saves protected on both. Checks handshake, seeds, and that each side shows the other
player where that player really is (global coordinates), static and after a move; then again under simulated lag.
  tools/mp2.py [--host-ip <laptop LAN IP>] [--remote user@host]   (defaults: TLD_SECOND_IP, TLD_SECOND)
"""
import sys, os, json, time, math, subprocess, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded, tldenv
from bench import Bridge
from determinism import go_global, glob

HOST_IP, REMOTE_HOST, DESKTOP_HOSTS, DESKTOP_IP = tldenv.SECOND_IP, tldenv.SECOND, False, tldenv.lan_ip()
it = iter(sys.argv[1:])
for a in it:
    if a == "--host-ip": HOST_IP = next(it)
    elif a == "--desktop-hosts": DESKTOP_HOSTS = True
    elif a == "--remote": REMOTE_HOST = next(it)
ROOT = pathlib.Path(__file__).resolve().parent.parent
if not REMOTE_HOST: tldenv.need_second()

class Agent:
    def __init__(self):
        env = dict(os.environ, TLD_REMOTE=REMOTE_HOST)
        self.p = subprocess.Popen([sys.executable, str(ROOT / "tools/mpagent.py")], env=env, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, text=True, bufsize=1)
        self.read()  # {"ready": true}
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
        self.p.stdin.close(); self.p.wait(timeout=120)
        for line in self.p.stdout: print("   [laptop]", line.strip())

def dist(a, b): return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))

def check(label, b, L):
    d = b("mp players"); l = L("mp players")
    dp = {p["id"]: p for p in d["players"]}; lp = {p["id"]: p for p in l["players"]}
    if DESKTOP_HOSTS:
        host_on_desktop = dp.get(L("mp status")["client"]["id"]); desk_on_laptop = lp.get(0)
    else:
        host_on_desktop = dp.get(0); desk_on_laptop = lp.get(b("mp status")["client"]["id"])
    e1 = dist(host_on_desktop["target"], l["me"]) if host_on_desktop else None
    e2 = dist(desk_on_laptop["target"], d["me"]) if desk_on_laptop else None
    print(f"  {label}: desktop shows laptop-player {e1 if e1 is None else round(e1, 2)} m from its true position; "
          f"laptop shows desktop-player {e2 if e2 is None else round(e2, 2)} m from its true position")
    return e1, e2

print("restarting both games with the same mod build")
game.quit_game(); game.launch()
subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "quit"], env=dict(os.environ, TLD_REMOTE=REMOTE_HOST))
subprocess.run([sys.executable, str(ROOT / "tools/game.py"), "launch"], env=dict(os.environ, TLD_REMOTE=REMOTE_HOST))
L = Agent()
try:
    with game.ProtectedSession():
        L("!seeded"); seeded.restart_same_seed()
        b = Bridge(); b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
        print("versions:", b("ping")["version"], L("ping")["version"])
        if DESKTOP_HOSTS:
            print("desktop hosts:", b("mp host 27070")["server"]["port"])
            L(f"mp join {DESKTOP_IP}:27070"); time.sleep(6)
            st = L("mp status")["client"]; ls = b("mp status")["server"]
            print(f"  handshake: laptop id {st['id']}, seeds host {st['serverSeed']} / laptop {st['mySeed']}, reject {st['reject']}, "
                  f"desktop sees {ls['ready']} ready peer(s)")
        else:
            print("laptop hosts:", L("mp host 27070")["server"]["port"])
            b(f"mp join {HOST_IP}:27070"); time.sleep(6)
            st = b("mp status")["client"]; ls = L("mp status")["server"]
            print(f"  handshake: desktop id {st['id']}, seeds host {st['serverSeed']} / desktop {st['mySeed']}, reject {st['reject']}, "
                  f"laptop sees {ls['ready']} ready peer(s)")
        # both players outside, 6 m apart, facing each other (start area: no floating-origin shift, global = Unity)
        h = glob(b); go_global(b, h[0] + 80, h[2] + 40); time.sleep(3)
        me = glob(b)
        gd = b(f"ground {me[0]} {me[2]}"); b(f"teleportyaw {me[0]} {gd['y'] + 1.5} {me[2]} 90")          # desktop faces +x
        g = L(f"ground {me[0] + 6} {me[2]}"); L(f"teleportyaw {me[0] + 6} {g['y'] + 1.5} {me[2]} 270")  # laptop faces -x
        L("set mainscript.s.player.godModeTillGrounded True")
        time.sleep(5)
        check("static", b, L)
        pl = (b if DESKTOP_HOSTS else L)("mp status")
        print("  poses/outfits:", {k: v for k, v in pl.get("server", {}).items() if k in ("posesRelayed",)},
              "desktop shows", b("get TLDRevamp.Net.RemotePlayers.Poses")["value"], "poses,", b("get TLDRevamp.Net.RemotePlayers.Outfits")["value"], "outfits;",
              "laptop shows", L("get TLDRevamp.Net.RemotePlayers.Poses")["value"], "poses,", L("get TLDRevamp.Net.RemotePlayers.Outfits")["value"], "outfits")
        print("  screenshots:", b("screenshot")["path"], "|", L("screenshot")["path"])
        g = L(f"ground {me[0] + 6} {me[2] + 15}"); L(f"teleport {me[0] + 6} {g['y'] + 1.5} {me[2] + 15}"); time.sleep(2)
        check("after the laptop player moved 15 m", b, L)
        for side in (b, L): side("net lag 180 2 10 40")
        time.sleep(3)
        g = L(f"ground {me[0] + 8} {me[2]}"); L(f"teleport {me[0] + 8} {g['y'] + 1.5} {me[2]}"); time.sleep(3)
        check("under 180 ms / 2 % loss / jitter (both sides), after another move", b, L)
        srv = (b if DESKTOP_HOSTS else L)("mp status")["server"]
        print("  server:", {k: v for k, v in srv.items() if k in ("ready", "statesRelayed", "sendErrors", "bytesOut")})
        for side in (b, L): side("net lag 0")
        b("mp stop"); L("mp stop"); b("set kaposztaleves.s.settings.god False")
finally:
    L.close()
