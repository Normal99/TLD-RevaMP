"""How each machine stands the other player's model (Mp RemotePlayers.PoseJson, bridge `mp rpose`). Two machines over
the relay, both on foot on the ground 6 m apart. The creature code (Entities.Ai) treats the model's root as the feet and
its THead as the head: handoff run 2026-10-08 read the host's body on the laptop as 0.45 m tall, and a creature's eye ray
that should have met it missed (remoteSeen 0 all run).
Then the host shoots the laptop's player at heights over the ground (bridge mp shootat <id> <h>): every one a hit.
  tools/rposetest.py [--no-build] [--relay iad|off]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from bench import Bridge

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    hp = h("gpos")
    h(f"tpground {hp['gx'] - 40} {hp['gz']}"); time.sleep(2)
    L(f"tpground {hp['gx'] - 34} {hp['gz']}"); time.sleep(4)
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        print(f"  host sees the laptop's player {h('mp rpose')}", flush=True)
        print(f"  laptop sees the host's player {L('mp rpose')}", flush=True)
        print("  magnum:", h("devspawn 64"), flush=True); time.sleep(1.5)
        for old in (True, False):   # A/B in one session: the body segment before v0.65.12, then now
            h(f"set TLDRevamp.Net.RemotePlayers.OldBody {old}")
            res = []
            for hgt in (0.3, 0.6, 1.0, 1.4):
                r = h(f"mp shootat 1 {hgt}"); time.sleep(0.6)
                res.append((hgt, r.get("hits"))); print(f"  {'old' if old else 'new'} body, shot at {hgt} m over the ground: {r}", flush=True)
            ok = all(x[1] == 1 for x in res)
            print(f"{'PASS' if ok else 'FAIL'}  {'(OLD BODY) ' if old else ''}SHOTS AT EVERY HEIGHT OF A STANDING PLAYER HIT  {res}", flush=True)
        h("set TLDRevamp.Net.RemotePlayers.OldBody False")
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as e: print("restore god", name, e)
