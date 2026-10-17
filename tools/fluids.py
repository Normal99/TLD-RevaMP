#!/usr/bin/env python3
"""Fluids in MP (two machines, relay iad by default), changed the way players change them.
  1 NON-OWNER POURS : the laptop fills the host's car from its own jerry can as a player does (can in hand, the
                      car's cap opened, looking at it, left mouse held: fpscontroller.DrinkFill) → the fuel that left the can must arrive
                      in the owner's tank, and both machines must show the same can and the same tank
  2 OWNER POURS     : the host pours from its own can into its own car — the same checks
  3 BURN            : the host drives 20 s; the laptop's copy of the car must follow the owner's fuel
After each pour: nothing still pouring anywhere (a container copy pouring on its own would make fuel twice).
  tools/fluids.py [--no-build] [--relay iad|off] [--secs 10]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, DESKTOP_IP
from roadkit import until, g, car_ready, seat_idx

def arg(name, default, conv=str):
    return conv(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default
RELAY, SECS = arg("--relay", "iad"), arg("--secs", 10.0, float)
if RELAY == "off": RELAY = None
JERRYCAN = 72        # itemdatabase: benzineskanna
CAR_START = 10.0     # litres in the car before pouring (a full tank takes nothing)
CAN_START = 15.0

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def unit(yaw): return (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))

def amt(s, net, ti):
    t = s(f"mp fluids {net}")["tanks"]
    return t[ti]["amount"] if ti < len(t) and "amount" in t[ti] else None

def pouring(s, net):
    return [t["ti"] for t in s(f"mp fluids {net}")["tanks"] if "pouring" in t.get("caps", "")]

def new_can(side, who):
    me = side("mp entities")["me"]
    before = {e["net"] for e in side("mp entities")["entities"]}
    print(f"  {who} jerry can:", side(f"devspawn {JERRYCAN}"), flush=True)
    got = []
    until(lambda: got.extend(e["net"] for e in side("mp entities")["entities"]
                             if e["net"] not in before and e["owner"] == me and not e["car"]) or got, 10)
    return got[0] if got else None

def pour_phase(tag, pourer, other, can, car, ti, fluid="gas", start=None):
    """pourer owns `can`; car's owner is the host (b). Checks conservation on the owner + agreement of copies."""
    owner = b
    if not until(lambda: amt(other, can, 0) is not None, 15): verdict(f"{tag}: CAN ON BOTH", False, "the other machine has no copy of the can"); return
    print(f"  {tag}: fill the can:", pourer(f"mp fill {can} 0 {fluid} {CAN_START}"), flush=True)
    if start is not None: print(f"  {tag}: car tank {ti} set to {start}:", owner(f"mp fill {car} {ti} {fluid} {start}"), flush=True)
    time.sleep(5)
    c0, h0 = amt(pourer, can, 0), amt(owner, car, ti)
    print(f"  {tag}: before — can {c0} (other copy {amt(other, can, 0)}), car on owner {h0} / laptop copy {amt(L, car, ti)}", flush=True)
    print(f"  {tag}: deaths so far — host {g(b, 'TLDRevamp.Diagnostics.DeathLog.Deaths')}, laptop {g(L, 'TLDRevamp.Diagnostics.DeathLog.Deaths')}; "
          f"pourer dead now: {g(pourer, 'mainscript.s.player.died')}", flush=True)
    print(f"  {tag}: hold-fill:", pourer(f"mp holdfill {can} {car} {ti}"), flush=True)
    time.sleep(0.5)
    st = pourer("mp holdfill status")
    print(f"  {tag}: {st} | let go: {g(pourer, 'TLDRevamp.Diagnostics.DropLog.Drops')} {g(pourer, 'TLDRevamp.Diagnostics.DropLog.Last')}", flush=True)
    if not st.get("held"): verdict(f"{tag}: CAN IN HAND", False); pourer("mp holdfill stop"); return
    for k in range(int(SECS)):
        time.sleep(1)
        print(f"    t{k + 1:>3}s  can {amt(pourer, can, 0):.3f} / copy {amt(other, can, 0):.3f}   car owner {amt(owner, car, ti):.3f} / laptop {amt(L, car, ti):.3f}"
              f"   pouring: {pouring(b, can) or '-'} {pouring(L, can) or '-'}", flush=True)
        if k in (0, 2, int(SECS) - 1): print("      ", pourer("mp holdfill status"), "| let go:", g(pourer, "TLDRevamp.Diagnostics.DropLog.Drops"), g(pourer, "TLDRevamp.Diagnostics.DropLog.Last"), flush=True)
    print(f"  {tag}: stop:", pourer("mp holdfill stop"), flush=True)
    if g(pourer, "mainscript.s.player.pickedUp") not in (None, "null"): pourer("dropheld")
    time.sleep(6)
    c1, h1 = amt(pourer, can, 0), amt(owner, car, ti)
    oc1, l1 = amt(other, can, 0), amt(L, car, ti)
    left, came = c0 - c1, h1 - h0
    print(f"  {tag}: after — can {c1} (copy {oc1}), car owner {h1} / laptop {l1}; left the can {left:.3f}, arrived in the owner's tank {came:.3f}", flush=True)
    verdict(f"{tag}: POURED", left > 0.5, f"{left:.2f} L left the can")
    verdict(f"{tag}: IT ARRIVED ON THE OWNER (no loss, no doubling)", left > 0.5 and abs(came - left) <= 0.1 + 0.05 * left,
            f"left the can {left:.3f}, arrived {came:.3f}")
    verdict(f"{tag}: BOTH SEE THE SAME CAR TANK", h1 is not None and l1 is not None and abs(h1 - l1) < 0.05, f"owner {h1} / laptop {l1}")
    verdict(f"{tag}: BOTH SEE THE SAME CAN", oc1 is not None and abs(c1 - oc1) < 0.05, f"owner of the can {c1} / copy {oc1}")
    still = (pouring(b, can), pouring(L, can))
    verdict(f"{tag}: NOTHING STILL POURING", not still[0] and not still[1], f"host {still[0]} laptop {still[1]}")

ensure_both("--no-build" not in sys.argv)
L = Agent()
try:
    with game.ProtectedSession():
        b = seeded_both(L)
        for s in (b, L): s("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("relay only via", RELAY, b(f"net relay {RELAY}"), L(f"net relay {RELAY}"), flush=True)
        b("mp host 27070")
        if RELAY: L(f"mp joinid {b('net me')['steamId']}")
        else: L(f"mp join {DESKTOP_IP}:27070")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        if RELAY: print("route:", L("net route"), flush=True)
        time.sleep(4)
        rp = b("roadpoint"); yaw = rp["yaw"]; D = unit(yaw); R = (D[1], -D[0])
        cx, cz = rp["gx"], rp["gz"]
        b(f"tpground {cx - R[0] * 10} {cz - R[1] * 10}"); time.sleep(2)   # nobody on the spawn point
        if not car_ready(b, cx, cz, yaw, "host (its car)"): sys.exit("host car not ready")
        me = b("mp entities")["me"]; gp = b("gpos")
        cars = [e for e in b("mp entities")["entities"] if e["car"] and e["owner"] == me and not e["dormant"]]
        car = min(cars, key=lambda e: math.hypot(e["pos"][0] - gp["gx"], e["pos"][2] - gp["gz"]))["net"]
        b("brakecar"); b("handbrake on"); time.sleep(1)
        tanks = b(f"mp fluids {car}")["tanks"]
        print("car tanks (owner):", tanks, flush=True)
        ti = max((t for t in tanks if "amount" in t), key=lambda t: t["max"])["ti"]   # the fuel tank: the biggest
        print("car fuel tank: ti", ti, "set to", CAR_START, b(f"mp fill {car} {ti} gas {CAR_START}"), flush=True)
        b("driveout"); time.sleep(1)
        b(f"tpground {cx - R[0] * 5} {cz - R[1] * 5}"); time.sleep(2)
        L(f"tpground {cx + R[0] * 5} {cz + R[1] * 5}"); time.sleep(2)
        if not until(lambda: amt(L, car, ti) is not None and abs(amt(L, car, ti) - CAR_START) < 0.05, 15):
            print("  laptop's copy never showed the owner's fill:", amt(L, car, ti), flush=True)
        print("laptop copy tanks:", L(f"mp fluids {car}")["tanks"], flush=True)

        can = new_can(L, "laptop")
        if can is None: sys.exit("no laptop can")
        pour_phase("1 NON-OWNER POURS", L, b, can, car, ti)

        hcan = new_can(b, "host")
        if hcan is None: sys.exit("no host can")
        pour_phase("2 OWNER POURS", b, L, hcan, car, ti)

        # ---- 3: burn
        b(f"tpground {cx + R[0] * 3} {cz + R[1] * 3}"); time.sleep(1)
        print("host sits:", b(f"sitseat {seat_idx(b)} redirect"), flush=True); time.sleep(1)
        b("handbrake off"); print("host drives:", str(b("autodrive on 8"))[:80], flush=True)
        worst = 0.0
        for k in range(20):
            time.sleep(1)
            h, l = amt(b, car, ti), amt(L, car, ti)
            worst = max(worst, abs(h - l))
            if k % 4 == 3: print(f"    t{k + 1:>3}s  fuel owner {h:.3f} / laptop {l:.3f}", flush=True)
        b("autodrive off")
        if g(b, "mainscript.s.player.Bsitting") is True: b("brakecar")
        time.sleep(6)
        h, l = amt(b, car, ti), amt(L, car, ti)
        verdict("3 BURN: THE COPY FOLLOWS THE OWNER", abs(h - l) < 0.05, f"after: owner {h} / laptop {l}; worst gap while driving {worst:.3f}")
        L("mp stop"); b("mp stop")
        if RELAY: b("net relay off"); L("net relay off")
finally:
    try: L("mp holdfill stop"); b("mp holdfill stop")
    except Exception: pass
    L.close()
print("\nSUMMARY")
for n, ok, d in VERDICTS: print(f"  {'PASS' if ok else 'FAIL'}  {n}")
