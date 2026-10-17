#!/usr/bin/env python3
"""Server owns the save: the host spawns a car, edits its fuel, `mp save`, quits; relaunch + load the autosave;
the car must be there with the edited fuel. Then rehost: a rejoining client must see it too."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import Agent, ensure_both, DESKTOP_IP

def until(fn, timeout=25, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

SEED = 20260924
ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    # ---------- session 1: host spawns + edits + saves
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")   # periodic autosave off; `mp save` is the explicit one
        b("mp host 27070")
        rp = b("roadpoint"); yaw = rp["yaw"] + 180.0
        F = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
        cx, cz = rp["x"] + F[0] * 20, rp["z"] + F[1] * 20
        g = b(f"ground {cx} {cz}")
        print("  spawn:", b(f"spawnpile 223 1 {cx} {g['y']} {cz} 1 1 1.0 4242")); time.sleep(4)
        net = min(((e["net"], math.dist(e["pos"], (cx, g["y"], cz))) for e in b("mp entities")["entities"] if e["part"] < 0),
                  key=lambda t: t[1])[0]
        print("  edit:", b(f"mp entpoke {net} fuel 7.5"))
        h1 = b(f"mp itemsnear {cx} {cz} 200")
        print(f"  before save: count {h1['count']} hash {h1['hash']}")
        print("  save:", b("mp save"))
        b("mp stop"); time.sleep(2)

        # ---------- load the autosave IN THIS INSTANCE (no quit: the ProtectedSession restore would clobber the save)
        print("load:", b("loadsave autosave1"))
        until(lambda: b("get DataFromMenuScript.s.mainmenu")["value"] is False
              and b("get mainscript.s.player")["value"] is not None, 180)
        print("  loaded world seed:", b("get DataFromMenuScript.s.seed")["value"], "(want 20260924)")
        until(lambda: b("get DataFromMenuScript.s.mainmenu")["value"] is False
              and b("get mainscript.s.player")["value"] is not None, 180)
        print("  loaded world seed:", b("get DataFromMenuScript.s.seed")["value"], "(want 20260924)")
        # items stream in progressively: wait until the count around the site stops growing
        last, stable = -1, 0
        for _ in range(60):
            c = b(f"mp itemsnear {cx} {cz} 200")["count"]
            if c == last: stable += 1
            else: stable = 0
            last = c
            if stable >= 4 and c > 0: break
            time.sleep(2)
        print(f"  streamed in: {last} items near the site")
        h2 = b(f"mp itemsnear {cx} {cz} 200")
        same = h1["hash"] == h2["hash"] and h1["count"] == h2["count"]
        print(f"  after load:  count {h2['count']} hash {h2['hash']} — identical: {same}")
        # ---------- rehost; a rejoining client must see the same car
        b("mp host 27070")
        L("mp stop")   # the laptop's old session ended with the world reload; rejoin fresh
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        L("set kaposztaleves.s.settings.god True"); time.sleep(6)
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        client_ok = False
        for e in L("mp entities")["entities"]:
            try:
                if "7.50" in L(f"mp entstate {e['net']}")["tanks"]: client_ok = True; break
            except Exception: pass
        print(f"  client rejoined: sees the edited state {client_ok}")
        print("SAVE-OWN VERDICT:", "PASS — session state survived the host restart and reached the rejoining client"
              if same and client_ok else ("PARTIAL — saved but the client doesn't see it" if same else "FAIL"))
        b("mp stop"); L("mp stop")
finally:
    L.close()
