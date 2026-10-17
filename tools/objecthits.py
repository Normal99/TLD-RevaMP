#!/usr/bin/env python3
"""Hits on objects someone else owns (HitCheck.AllowOnObject), two machines over the relay. The host owns a lamp (no
breakable: only the object's position is known, a shot's reach for anything), a creature (its breakables, the flesh
hitboxes: the host's own copy says exactly where) and a car (a car01 has no breakables). The laptop sends what a hit
would (Entities.TestObjectHit), from near and from far.
  A  near: a shot and a blow on the creature's breakable, a set-off of the lamp, the passenger's controls of the car:
     allowed
  B  2.5 km away: the same four refused, logged as [anomaly]; a shot at the creature from 600 m allowed, a blow from
     there refused
  C  --old (HitCheck.ObjectsEnabled off): from 2.5 km everything passes, as before
  D  no errors
  tools/objecthits.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp, CAR01

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY = arg("--relay", "iad")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return s("mp entities")["entities"]
def hc(s): return s("mp hitcheck")
def ais(s): return s("mp ai")["ai"]
def ainet(a): w = a["who"]; return int(w.split()[1]) if w.startswith("net ") else None

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); x, z, yaw = rp["gx"], rp["gz"], rp["yaw"]
        tp(h, x, z); tp(L, x + 4, z); time.sleep(3)
        before = {e["net"] for e in ents(h)}
        print("  host spawns a car:", h(f"spawncarg {CAR01} 0 {x + 8} {z + 8} {yaw}"), flush=True)
        h("devspawn 320"); time.sleep(5)
        new = [e for e in ents(h) if e["net"] not in before and not e["proxy"]]
        car = next((e["net"] for e in new if e["car"]), None)
        lamp = next((e["net"] for e in new if not e["car"]), None)
        if not car or not lamp: sys.exit(f"no car or lamp: {new}")
        aid = h("mp aiids")["ai"][0][0]
        a0 = {ainet(a) for a in ais(h)}
        print("  host spawns a creature:", h(f"devspawn {aid}"), flush=True)
        if not until(lambda: [a for a in ais(h) if ainet(a) not in a0], 15): sys.exit("no creature")
        cre = next(ainet(a) for a in ais(h) if ainet(a) not in a0)
        until(lambda: {car, lamp, cre} <= {e["net"] for e in ents(L)}, 15)
        def send(what, net, bi=0, kind=1): return L(f"call TLDRevamp.Net.Entities.TestObjectHit {what} {net} {bi} {kind}")["value"]
        def cpos(): return next(e["pos"] for e in ents(h) if e["net"] == cre)
        print("  the creature's breakables on the laptop:", send("break", cre, 0, 1), flush=True); time.sleep(1)
        four = lambda: [send("break", cre, 0, 1), send("break", cre, 0, 2), send("explode", lamp, 0, 0), send("shotgun", car, 0, 0)]

        def round_(label):
            c0 = hc(h); four(); time.sleep(2); c1 = hc(h)
            return c1["objChecked"] - c0["objChecked"], c1["objAllowed"] - c0["objAllowed"], c1["objRefused"] - c0["objRefused"], c1["lastObj"]
        # ---- A (the near round minus the probe above)
        chk, alw, ref, last = round_("near")
        verdict("A NEAR: ALL FOUR ALLOWED", chk == 4 and alw == 4 and ref == 0, f"checked {chk}, allowed {alw}, refused {ref}")
        # ---- B
        tp(L, x + 2500, z); time.sleep(4)
        chk, alw, ref, last = round_("far")
        verdict("B 2.5 KM AWAY: ALL FOUR REFUSED", chk == 4 and ref == 4, f"checked {chk}, refused {ref}, last {last}")
        cp = cpos(); tp(L, cp[0] + 600, cp[2]); time.sleep(4)
        c0 = hc(h); send("break", cre, 0, 1); time.sleep(1.5); c1 = hc(h)
        shot_ok = c1["objAllowed"] - c0["objAllowed"] == 1
        send("break", cre, 0, 2); time.sleep(1.5); c2 = hc(h)
        blow_refused = c2["objRefused"] - c1["objRefused"] == 1
        verdict("B 600 M: A SHOT ALLOWED, A BLOW REFUSED", shot_ok and blow_refused, f"shot allowed {shot_ok}, blow refused {blow_refused}, last {c2['lastObj']}")
        # ---- C
        h("set TLDRevamp.Net.HitCheck.ObjectsEnabled False")
        tp(L, x + 2500, z); time.sleep(4)
        chk, alw, ref, last = round_("old")
        verdict("C --old: NOTHING CHECKED", chk == 0 and ref == 0, f"checked {chk}, refused {ref}")
        h("set TLDRevamp.Net.HitCheck.ObjectsEnabled True")
        er = [x_("errors") for x_ in (h, L)]
        verdict("D NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        h("set TLDRevamp.Net.HitCheck.ObjectsEnabled True")
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"OBJECTHITS: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
