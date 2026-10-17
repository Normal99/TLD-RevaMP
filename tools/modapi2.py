#!/usr/bin/env python3
"""The multiplayer API's second step (Net/ModObjects.cs, docs/MODDING-MULTIPLAYER.md §4–6) with the test mod
src/Mods/ApiProbe on both machines, over the relay. Beacons: a label in the record, a needle in state records (state
range 300 m), a far store range of 800 m (a copy goes beyond 1000 m), one session-wide setting ("rule").
  A  a host beacon reaches the laptop; the host moves its needle: the laptop sees it
  B  the laptop 450 m away (in view, out of state range): the needle's change waits; back near, it arrives
  C  the laptop 1.5 km away: its copy is stored; the host changes the needle; back, the copy is built again (placed)
     with the latest needle
  D  a laptop beacon, the laptop goes far: the host (nearest) owns it now, the laptop's copy is stored
  E  both far: nobody has the beacons; the laptop comes first and owns them (with the latest needle); the host back
     gets them with the laptop's needle
  F  settings: the joiner's own "medium" becomes the host's "easy" at join, "hard" when the host changes it, and
     "medium" again after leaving
  G  no errors, no mod drops
  TLD_TEST_MODS=ApiProbe tools/modapi2.py [--no-build] [--relay iad|off]"""
import os
os.environ["TLD_TEST_MODS"] = "ApiProbe"
import sys, time, json, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, tp

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY = arg("--relay", "iad")
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def probe(s):
    v = s("call ApiProbe.Probe.Status")
    v = v.get("value", v) if isinstance(v, dict) else v
    return json.loads(v) if isinstance(v, str) else v
def call(s, what): return s(f"call ApiProbe.Probe.{what}")
def beacon(s, label): return next((b for b in probe(s)["beacons"] if b["label"] == label), None)
def needle(s, label): b = beacon(s, label); return None if b is None else b["needle"]
def mods(s): return s("mp mods")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    print("  laptop's own rule:", call(L, "SetRule medium"), flush=True)
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        lid = L("mp status")["client"]["id"]
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        tp(h, x, z); tp(L, x + 3, z); time.sleep(3)
        p0 = probe(L)
        verdict("F AT JOIN THE HOST'S VALUE", p0["rule"] == "easy" and p0["ruleFromHost"], f"laptop rule {p0['rule']}, from host {p0['ruleFromHost']}")

        # ---- A
        call(h, "Spawn a 0 6")
        ok = until(lambda: beacon(L, "a") is not None, 10)
        b = beacon(L, "a")
        call(h, "SetNeedle a 0.5")
        ok2 = until(lambda: needle(L, "a") == 0.5, 5)
        verdict("A THE BEACON AND ITS NEEDLE REACH THE LAPTOP", ok and ok2 and b["owner"] == 0, f"laptop {beacon(L, 'a')}")

        # ---- B: out of state range, still in view
        tp(L, x + 450, z); time.sleep(3)
        held0 = mods(h)["statesHeld"]
        call(h, "SetNeedle a 0.9"); time.sleep(3)
        far_needle, held = needle(L, "a"), mods(h)["statesHeld"] - held0
        tp(L, x + 3, z)
        ok = until(lambda: needle(L, "a") == 0.9, 6)
        verdict("B STATE WAITS OUT OF RANGE, ARRIVES NEAR", far_needle == 0.5 and held >= 1 and ok, f"at 450 m {far_needle} (held {held}), back near {needle(L, 'a')}")

        # ---- C: far store
        st0 = probe(L)["stored"]; pl0 = probe(L)["placed"]
        tp(L, x + 1500, z)
        gone = until(lambda: beacon(L, "a") is None, 8)
        call(h, "SetNeedle a 0.3"); time.sleep(2)
        st1 = probe(L)["stored"]
        tp(L, x + 3, z)
        back = until(lambda: beacon(L, "a") is not None, 10)
        pc = probe(L)
        verdict("C STORED FAR AWAY, PLACED AGAIN WITH THE LATEST STATE", gone and st1 > st0 and back and pc["placed"] > pl0 and needle(L, "a") == 0.3,
                f"gone {gone}, stored {st0}->{st1}, back {back}, placed {pl0}->{pc['placed']}, needle {needle(L, 'a')}")

        # ---- D: the owner goes far
        call(L, "Spawn b 0 -6")
        until(lambda: beacon(h, "b") is not None, 10)
        call(L, "SetNeedle b 0.7"); until(lambda: needle(h, "b") == 0.7, 5)
        ob = beacon(h, "b")
        tp(L, x + 1500, z)
        handed = until(lambda: (beacon(h, "b") or {}).get("owner") == 0, 8)
        lgone = until(lambda: beacon(L, "b") is None, 8)
        verdict("D THE OWNER FAR: THE HOST OWNS IT, THE LAPTOP'S COPY STORED", ob and ob["owner"] == lid and handed and lgone and needle(h, "b") == 0.7,
                f"before {ob}, host now {beacon(h, 'b')}, laptop has it {beacon(L, 'b') is not None}")
        call(h, "SetNeedle b 0.2"); time.sleep(1)

        # ---- E: nobody near; the first who comes owns them
        tp(h, x - 1500, z); time.sleep(3)
        hgone = until(lambda: beacon(h, "a") is None and beacon(h, "b") is None, 8)
        tp(L, x + 3, z)
        lb = until(lambda: beacon(L, "a") is not None and beacon(L, "b") is not None, 10)
        la, lbb = beacon(L, "a"), beacon(L, "b")
        mine = la and lbb and la["mine"] and lbb["mine"]
        latest = la and lbb and la["needle"] == 0.3 and lbb["needle"] == 0.2
        call(L, "SetNeedle a 0.1"); time.sleep(1)
        tp(h, x, z)
        hb = until(lambda: needle(h, "a") == 0.1 and beacon(h, "b") is not None, 10)
        verdict("E NOBODY NEAR: STORED; THE FIRST TO COME OWNS THEM, WITH THE LATEST STATE", hgone and lb and mine and latest and hb,
                f"host stored {hgone}, laptop {la} {lbb}, host back {beacon(h, 'a')}")

        # ---- F: the host changes the setting
        call(h, "SetRule hard")
        ok = until(lambda: probe(L)["rule"] == "hard", 6)
        verdict("F THE HOST'S CHANGE REACHES THE LAPTOP", ok, f"laptop {probe(L)['rule']}")
        er = [x_("errors") for x_ in (h, L)]
        md = [mods(x_)["dropped"] for x_ in (h, L)]
        verdict("G NO ERRORS, NO MOD DROPS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er) and md == [0, 0],
                f"{[(e['errors'], e['exceptions'], (e.get('kinds') or [])[:3]) for e in er]}, drops {md}, last {[mods(x_)['lastDrop'] for x_ in (h, L)]}")
    finally:
        L("mp stop"); time.sleep(2)
        try:
            pe = probe(L)
            verdict("F AFTER LEAVING, THE LAPTOP'S OWN VALUE", pe["rule"] == "medium" and not pe["ruleFromHost"], f"laptop {pe['rule']}, from host {pe['ruleFromHost']}")
        except Exception as e: verdict("F AFTER LEAVING, THE LAPTOP'S OWN VALUE", False, str(e))
        h("mp stop")
        call(h, "SetRule easy")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"MODAPI2: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
