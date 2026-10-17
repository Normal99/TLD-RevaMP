#!/usr/bin/env python3
"""Car signals on copies (v0.64.10, Entities.CarSignals). Two machines over the relay: desktop hosts, laptop joins.
The host sits in the driver's seat of its car; the laptop stands next to it. A copy showed none of the driver's
signals: no horn, no brake or reverse light, and with ignition off not even the lamps or dashboard.
Each step works the driver's controls on the host (test hooks where the keyboard's input lands) and compares the
laptop's copy with the host's car:
  1 ignition on        copy ignition + localIgnition like the host's
  2 gears              braking at a standstill the automatic shifts into reverse: the copy in the same gear
  3 brake lights       rolling at 8 m/s, then the brake: copy brake lights lit like the host's (run 1: braking at a
                       standstill lit nothing on the host either - the automatic reverses instead)
  4 horn held / let go copy horn sounding / silent like the host's
  5 handbrake on / off copy lever like the host's
  tools/carsigtest.py [--no-build] [--relay iad|off] [--car Car06Full]"""
import sys, os, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
VERDICTS = []
T = "TLDRevamp.Net.Entities"

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}

def seat_idx(side, want_driver):
    """A collider seatscript that redirects (mainseat) to the driver's seat - what an E-press hits (coreverify)."""
    seats = side("seatraw")["seats"]
    target = next(s["i"] for s in seats if s["main"] == "null" and (s["d0"] if want_driver else (s["d1"] and not s["d0"])))
    return next(s["i"] for s in seats if s["main"] == f"#{target}")

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
    for name, s_ in (("host", h), ("laptop", L)):
        gods[name] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        hp = h("gpos")
        h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)   # out of the start building (pushtest run 2)
        before = set(ents(h))
        print("  host spawns:", h(f"devspawn {CAR}"), flush=True)
        until(lambda: any(e["car"] and not e["proxy"] for n, e in ents(h).items() if n not in before), 20)
        net = [n for n, e in ents(h).items() if n not in before and e["car"] and not e["proxy"]][0]
        if not until(lambda: net in ents(L), 30): sys.exit("laptop never got the car")
        time.sleep(6)
        car = ents(h)[net]["pos"]
        L(f"tpground {car[0] + 6} {car[2] + 6}"); time.sleep(3)
        drv = seat_idx(h, True)
        print("  host sits:", h(f"sitseat {drv} redirect"), flush=True); time.sleep(2)
        print("  host seat:", h("seatlook"), flush=True)

        def both(tag):
            time.sleep(1.5)
            o, c = h(f"mp carsig {net}"), L(f"mp carsig {net}")
            print(f"  {tag}: host {o}\n  {' ' * len(tag)}  copy {c}", flush=True)
            return o, c

        both("0 nothing pressed")
        h(f"set {T}.TestIgnition 1")
        o, c = both("1 ignition on")
        verdict("1 IGNITION", o["localIgnition"] and c["localIgnition"] == o["localIgnition"] and c["ignition"],
                f"host {o['localIgnition']} copy {c['localIgnition']}")
        # 2 gears: braking at a standstill, the automatic gearbox goes into reverse by itself (run 1) - the copy too
        h(f"set {T}.TestBrake 1")
        o, c = both("2 brake held at a standstill")
        g1 = o["gear"] == c["gear"]; rev = o["gear"]
        h(f"set {T}.TestBrake 0")
        o, c = both("2b brake let go")
        verdict("2 GEAR (THE AUTOMATIC'S OWN SHIFTS)", g1 and o["gear"] == c["gear"], f"held: host {rev} copy same {g1}; let go: host {o['gear']} copy {c['gear']}")
        h(f"set {T}.TestHorn True")
        o, c = both("4 horn held")
        hon = o["hornPlaying"] and c["hornPlaying"]
        h(f"set {T}.TestHorn False")
        o, c = both("4b horn let go")
        verdict("4 HORN", hon and not o["hornPlaying"] and not c["hornPlaying"], f"held: both sounding {hon}; let go: host {o['hornPlaying']} copy {c['hornPlaying']}")
        want = 0 if o["handbrake"] >= 0.5 else 1
        h(f"set {T}.TestHandbrake {want}")
        o, c = both(f"5 handbrake {'on' if want else 'off'}")
        ok1 = (o["handbrake"] >= 0.5) == bool(want) and (c["handbrake"] >= 0.5) == bool(want)
        h(f"set {T}.TestHandbrake {1 - want}")
        o, c = both(f"5b handbrake {'off' if want else 'on'}")
        ok2 = (o["handbrake"] >= 0.5) == bool(1 - want) and (c["handbrake"] >= 0.5) == bool(1 - want)
        verdict("5 HANDBRAKE", ok1 and ok2, f"both ways {ok1} {ok2}")
        # 3 brake lights: rolling forward at 8 m/s, then the brake (at a standstill the automatic reverses instead)
        h(f"set {T}.TestHandbrake 0"); time.sleep(1)
        h(f"set {T}.TestRoll 8"); time.sleep(0.4)
        h(f"set {T}.TestBrake 1"); time.sleep(0.5)
        o, c = h(f"mp carsig {net}"), L(f"mp carsig {net}")
        print(f"  3 braking while rolling: host {o}\n                            copy {c}", flush=True)
        verdict("3 BRAKE LIGHTS", o["brakeLights"][0] > 0 and c["brakeLights"][0] == o["brakeLights"][0],
                f"lit host {o['brakeLights']} copy {c['brakeLights']}")
        h(f"set {T}.TestBrake 0")
        o, c = both("3b brake let go")
        verdict("3b BRAKE LIGHTS OFF AGAIN", c["brakeLights"][0] == o["brakeLights"][0], f"lit host {o['brakeLights']} copy {c['brakeLights']}")
    finally:
        for k, v in (("TestBrake", 0), ("TestRoll", 0), ("TestHorn", False), ("TestHandbrake", -1), ("TestIgnition", -1)):
            try: h(f"set {T}.{k} {v}")
            except Exception as ex: print("restore", k, ex)
        try: h("driveout")
        except Exception: pass
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
