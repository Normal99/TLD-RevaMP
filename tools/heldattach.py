#!/usr/bin/env python3
"""A part bolted on straight from a player's hands (playtest 2026-10-09: "some parts couldn't be touched by me when he
touched them, or they were just locked for both players"). The owner sends no states for a part that rides its parent;
before v0.66.2 the last one said "in my hands", and the server kept that: it refused every claim on the part from then
on, and every copy of it collided with nothing. Two machines over the relay.
  1 the host takes a hubcap off its car; the laptop picks it up (its own now, held) and bolts it back on the host's car
    in the same frame it lets go (the attach key's Craft)
  2 after 3 s: the server's record (`mp sent`) — owner the car's owner, not held; the laptop's copy not held; the
    host's own part not held
  3 the host takes it off again, the laptop picks it up: a claim, not refused
  tools/heldattach.py [--no-build] [--relay iad|off] [--old]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
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

def ent(s, net): return next((e for e in s("mp entities")["entities"] if e["net"] == net), None)
def refused(s): return s("mp entities").get("claimsRefused")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    try:
        h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.ClearHeldOnAttach {not OLD}")
        print(f"  {'OLD (fix off)' if OLD else 'NEW (fix on)'}", flush=True)
        time.sleep(5)
        hp = h("gpos"); me = h("mp entities")["me"]; lme = L("mp entities")["me"]
        cx, cz = hp["gx"] + 8, hp["gz"] + 8
        print("  host car:", h(f"spawncarg {CAR01} 0 {cx} {cz} 0"), flush=True)
        before0 = set()
        car = []
        until(lambda: bool(car.extend(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["owner"] == me and "Car01" in e["name"]
                                      and math.hypot(e["pos"][0] - cx, e["pos"][2] - cz) < 15) or car), 20)
        if not car: sys.exit("no car")
        car = max(car)   # the newest: the save has its own Car01 and a bike nearby
        until(lambda: (ent(L, car) or {}).get("dormant") is False, 20); time.sleep(4)
        L(f"tpground {cx - 3} {cz - 3}"); time.sleep(3)

        def take_hubcap():
            parts = h(f"mp attachedparts {car}")["parts"]
            hub = next((p for p in parts if "Disztarcsa" in p["name"]), None)
            if hub is None: return None
            before = {e["net"] for e in h("mp entities")["entities"]}
            print("  host takes a hubcap off:", h(f"mp falloff {car} {hub['i']}"), flush=True)
            got = []
            until(lambda: bool(got.extend(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and (e["parent"] == car or "Disztarcsa" in e["name"])) or got), 20)
            if not got: print("  new entities after the falloff:", [(e["net"], e["name"], e["parent"]) for e in h("mp entities")["entities"] if e["net"] not in before], flush=True)
            return got[0] if got else None

        hub = take_hubcap()
        if hub is None: sys.exit("no hubcap came off")
        until(lambda: ent(L, hub) is not None, 10); time.sleep(3)
        r0 = refused(h)
        print("  laptop picks it up:", L(f"mp lookpick {hub} pick"), flush=True)
        print("  and carries it (mouse held down):", L(f"mp forceheld {hub}"), flush=True)
        ok1 = until(lambda: (ent(h, hub) or {}).get("owner") == lme and (ent(L, hub) or {}).get("held") is True, 10)
        print(f"  server while carried: {h(f'mp sent {hub}')}", flush=True)
        verdict("1 THE LAPTOP HOLDS THE HUBCAP (its own)", ok1, f"owner {(ent(h, hub) or {}).get('owner')}, held here {(ent(L, hub) or {}).get('held')}")
        time.sleep(1.5)
        print("  laptop bolts it on the host's car from its hands:", L(f"mp attachto {hub} {car} -1"), flush=True)
        time.sleep(3)
        se = h(f"mp sent {hub}"); lh = ent(L, hub) or {}; hh = ent(h, hub) or {}
        print(f"  server: {se}; laptop copy: held {lh.get('held')} owner {lh.get('owner')}; host: held {hh.get('held')} owner {hh.get('owner')}", flush=True)
        verdict("2 BOLTED ON: THE SERVER'S RECORD — THE CAR OWNER'S, NOT HELD", se.get("owner") == me and se.get("held") is False, str(se))
        verdict("2 BOLTED ON: THE LAPTOP'S COPY NOT HELD (it collides)", lh.get("held") is False, f"held {lh.get('held')}")

        # 3: off again, and the laptop's claim
        print("  host takes that hubcap off again:", h(f"mp falloff {hub} 0"), flush=True)
        time.sleep(4)
        nets = [e["net"] for e in h("mp entities")["entities"] if "Disztarcsa" in e["name"] and not (e.get("attached"))]
        r1 = refused(h)
        target = hub if hub in nets else (nets[-1] if nets else hub)
        until(lambda: (ent(L, target) or {}).get("dormant") is False, 15)
        try: r = L(f"mp lookpick {target} pick")
        except RuntimeError as ex: r = {"error": str(ex)}
        print("  laptop picks it up again:", r, flush=True)
        ok3 = "error" not in r and until(lambda: (ent(h, target) or {}).get("owner") == lme, 10)
        verdict("3 OFF AGAIN: THE LAPTOP'S CLAIM IS NOT REFUSED", ok3 and refused(h) == r1, f"owner {(ent(h, target) or {}).get('owner')}, refusals +{refused(h) - r1} (since start +{refused(h) - r0})")
        print("  refusal reasons:", h("mp entities").get("refusedWhy"), flush=True)
    finally:
        for s_ in (h, L):
            try: s_("set TLDRevamp.Net.Entities.ClearHeldOnAttach True"); s_("mp forceheld 0")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
