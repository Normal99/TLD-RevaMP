#!/usr/bin/env python3
"""Death/respawn sync (B8): the laptop player dies; the desktop's view of the laptop body must track the fall
and then the respawn. The pose stream carries IK targets, so the fall should roughly replicate."""
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

def body(who, pid):
    try:
        for p in who("mp players")["players"]:
            if p["id"] == pid: return p
    except Exception: pass
    return None

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        seeded.restart_same_seed(); b = Bridge()
        RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"   # real latency by default
        if RELAY != "off": b(f"net relay {RELAY}"); L(f"net relay {RELAY}")
        b("mp host 27070")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY != "off": L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        until(lambda: L("mp status")["client"]["ready"], 150)
        time.sleep(5)
        before = body(b, 1)
        print("  desktop sees the laptop body:", before)
        # kill the laptop (god off first — Death() is guarded)
        L("set kaposztaleves.s.settings.god False")
        print("  kill:", L("call mainscript.s.player.Death"))
        samples = []
        for _ in range(10):
            time.sleep(0.7)
            bb = body(b, 1)
            if bb: samples.append((round(bb["shown"][0], 1), round(bb["shown"][1], 1), round(bb["shown"][2], 1), bb["seated"]))
        moved = len(set(samples)) > 2
        print("  body during death (shown positions):", samples[:6])
        print("  remote body tracked the fall:", moved)
        # respawn: in a session the death screen's automatic "back to the menu" respawns the player in the world
        # instead (Net.SessionRules, the official MP's rule: 40-60 m away) - nobody leaves the session. (The old
        # PressedRestart here reloaded the laptop's world and dropped it from the session.)
        dead_at = samples[-1][:3] if samples else None
        r0 = L("get TLDRevamp.Net.SessionRules.Respawns")["value"]
        respawned = until(lambda: L("get TLDRevamp.Net.SessionRules.Respawns")["value"] > r0, 90)
        time.sleep(4)
        alive = not L("get mainscript.s.died")["value"]
        after = body(b, 1)
        far = after and dead_at and ((after["shown"][0] - dead_at[0]) ** 2 + (after["shown"][2] - dead_at[2]) ** 2) ** 0.5
        print(f"  respawned in the world: {respawned}, alive {alive}, still in the session {L('mp status').get('client', {}).get('ready')}", flush=True)
        print(f"  after respawn, desktop sees: {after}  ({far and round(far, 1)} m from where they died)", flush=True)
        ok = moved and respawned and alive and after is not None and far is not None and far > 20
        print("B8 VERDICT:", "PASS - the fall replicated; respawned in the world, seen there by the other machine" if ok else "FAIL - inspect samples")
        L("set kaposztaleves.s.settings.god True")
        b("mp stop"); L("mp stop")
finally:
    L.close()
