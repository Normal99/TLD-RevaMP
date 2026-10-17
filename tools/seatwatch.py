#!/usr/bin/env python3
"""Two machines: the laptop sits in the desktop's car (shotgun, then driver) and its seat state is sampled every
0.5 s for 20 s, with every GetOut logged with its stack (RemoteTrace). Prints the laptop's SeatTrace lines."""
import sys, time, math, pathlib, subprocess
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded, tldenv
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=15, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def seat_idx(side, want_driver):
    seats = side("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and (s["d0"] if want_driver else (s["d1"] and not s["d0"])))
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

LOG = tldenv.remote_quote(tldenv.REMOTE_LOG)
role = sys.argv[1] if len(sys.argv) > 1 else "shotgun"
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        for side in (b, L): side("set kaposztaleves.s.settings.god True"); side("set TLDRevamp.Net.Mp.RemoteTrace True")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        cx, cz = rp["x"] + math.sin(math.radians(yaw)) * 20, rp["z"] + math.cos(math.radians(yaw)) * 20
        g = b(f"ground {cx} {cz}")
        b(f"teleportyaw {cx + 5} {g['y'] + 1.5} {cz} 270"); time.sleep(1)
        b(f"spawnpile 2 1 {cx} {g['y']} {cz} 1 1 1.0 4242"); time.sleep(4); b(f"carface {cx} {g['y']} {cz} {yaw}"); time.sleep(2)
        net = min((e for e in b("mp entities")["entities"] if e["part"] < 0 and e["car"]), key=lambda e: math.dist(e["pos"], (cx, g["y"], cz)))["net"]
        until(lambda: any(e["net"] == net for e in L("mp entities")["entities"]), 20)
        if role == "shotgun": print("host sits:", b(f"sitseat {seat_idx(b, True)} redirect")); b("handbrake on")
        pos = {e["net"]: e for e in b("mp entities")["entities"]}[net]["pos"]
        print("before tpg:", L("seatlook"))
        L(f"tpg {pos[0] + 3} {pos[2] + 3}")
        for i in range(14): time.sleep(0.5); print(f"  fall t={i*0.5+0.5}s", L("seatlook"), flush=True)
        print("laptop sits:", L(f"sitseat {seat_idx(L, role == 'driver')} redirect"), flush=True)
        t0 = time.time()
        while time.time() - t0 < 20:
            st = L("seatlook"); own = {e["net"]: e for e in L("mp entities")["entities"]}[net]
            print(f"  t={time.time() - t0:4.1f}s died {st.get('died')} god {st.get('god')} sitting {st.get('sitting')} seat {st.get('seat')!r} | laptop's car owner {own['owner']} proxy {own['proxy']} upy {own.get('upy')}", flush=True)
            time.sleep(0.5)
        for side in (b, L): side("set TLDRevamp.Net.Mp.RemoteTrace False"); side("set kaposztaleves.s.settings.god False")
        L("mp stop"); b("mp stop")
finally:
    L.close()
r = subprocess.run(tldenv.ssh_args() + [f"grep -n -A14 'SeatTrace' {LOG} | tail -80"], capture_output=True, text=True)
print("---- laptop SeatTrace:\n" + (r.stdout or r.stderr))
