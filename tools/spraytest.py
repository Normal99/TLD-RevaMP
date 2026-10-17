#!/usr/bin/env python3
"""Spraying paint, seen by the other player (two machines, relay iad by default). The laptop's player picks up a spray
can, stands at the host's car and sprays it (the game's own Fire per frame, as holding the left button). On the host the
copy of the can must show the spray: valve open, mist emitting, hiss playing — then stop when the laptop stops. The
paint itself is paintsync.py's.
  A (--ab) the effect off (before v0.65.23): the copy stays silent
  B        the effect on
  tools/spraytest.py [--no-build] [--relay iad|off] [--ab]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def ents(x): return {e["net"]: e for e in x("mp entities")["entities"]}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value")); s_("set kaposztaleves.s.settings.god True")
        cans = h("prefabswith sprayscript")["items"]; print("spray cans:", cans, flush=True)
        if not cans: sys.exit("NO SPRAY CAN PREFAB")
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = set(ents(h))
        print("car:", h("devspawn Car01Full"), flush=True)
        until(lambda: any(e["car"] and n not in before for n, e in ents(h).items()), 20)
        car = next(n for n, e in ents(h).items() if e["car"] and n not in before)
        time.sleep(2)
        print("  laptop at the car:", L(f"mount front {car} 1.6"), flush=True); time.sleep(2)
        h(f"mount front {car} 7"); time.sleep(2)   # the host watches from in front of the car, facing it
        lb = set(ents(L))
        print("  laptop spawns a can:", L(f"devspawn {cans[0][0]}"), flush=True)
        until(lambda: any(n not in lb and not e["car"] for n, e in ents(L).items()), 20)
        can = next(n for n, e in ents(L).items() if n not in lb and not e["car"])
        until(lambda: can in ents(h), 20); time.sleep(1)
        print("  laptop picks it up:", L(f"mp pickupnet {can}"), flush=True); time.sleep(2)
        print("  laptop's can:", L(f"mp sprayfx {can}"), flush=True)
        runs = [False, True] if "--ab" in sys.argv else [True]
        for fx in runs:
            h(f"set TLDRevamp.Net.Entities.SprayFx {fx}")
            tag = "B THE EFFECT ON" if fx else "A (EFFECT OFF, the old behaviour)"
            print(f"=== {tag}", flush=True)
            print("  laptop sprays:", L("mp sprayhold 4"), flush=True)
            seen = []
            for k in range(6):
                time.sleep(0.5)
                q = h(f"mp sprayfx {can}"); seen.append(q)
                print(f"    t{(k+1)*0.5:.1f}s host's copy: {q} | laptop: {L(f'mp sprayfx {can}')}", flush=True)
                if k == 2:
                    try: print("    host looks at the can:", h(f"mp lookat {can}"), flush=True)   # the laptop's player spraying the car
                    except RuntimeError as ex: print("    look:", ex, flush=True)
                if k == 3: h(f"shot spray_{'on' if fx else 'off'}")
            time.sleep(3.5)
            after = h(f"mp sprayfx {can}"); print(f"  after the laptop stopped: host's copy {after} | laptop's own {L(f'mp sprayfx {can}')}", flush=True)
            showing = sum(1 for q in seen[1:] if q.get("mist") and q.get("hiss") and q.get("valve", 0) > 0.3 and q.get("particles", 0) > 0)
            verdict(f"{tag}: THE HOST SEES AND HEARS THE SPRAY", showing >= 3, f"{showing} of {len(seen) - 1} samples with mist particles + hiss")
            # the game eases the valve towards 0 and never quite gets there: the emitter stays "on" at ~0 rate, the hiss
            # "playing" at ~0 volume (rate and volume = valve x max) — on the holder's own machine the same
            verdict(f"{tag}: AND IT STOPS", after.get("valve", 1) < 0.05, f"valve {after.get('valve')} (mist rate and hiss volume follow it)")
            time.sleep(2)
    finally:
        try: h("set TLDRevamp.Net.Entities.SprayFx True")
        except Exception: pass
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
