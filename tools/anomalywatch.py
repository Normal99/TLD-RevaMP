#!/usr/bin/env python3
"""The anomaly watch (v0.66.6, Entities.Anomaly: log-only "[anomaly]" lines for real playtests) — it must stay silent
through everything we do with dev tools, and catch the glitches it is for. Two machines over the relay; the host's
Car05Full, the laptop next to it.
  1 DEV ACTIONS (expected: no anomaly on either machine): the laptop teleports 600 m away and back twice (its copy of
    the host's car streamed out and placed again); the host takes the hubcap and a wheel off, deletes the loose wheel
    (deleteshare), takes the FR door off and bolts it back, moves the car 30 m; the host spawns a crate and deletes it;
    the laptop teleports away and back once more (the census after placing: the same parts) — 20 s to settle.
  2 GLITCHES MADE ON PURPOSE (expected: caught): a the laptop's copy displaced 500 m and the game's removal pass run →
    "copy stored out displaced"; b a mirror of the laptop's copy gone there only → "copy's parts differ" after the
    census asked twice.
  tools/anomalywatch.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY = arg("--relay", "iad")
CAR05 = 154; BOX = 62
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"]}
def ent(s, net): return ents(s).get(net)

def travel(s, gx, gz, who, settle=4):
    """ground-safe teleport to a GLOBAL point (meetagain.travel)"""
    s("set mainscript.s.player.godModeTillGrounded True")
    s(f"tpg {gx} {gz} hold")
    def grounded():
        q = s("gpos"); return "y" in s(f"ground {q['ux']} {q['uz']}")
    if not until(grounded, 90, 1.0): s("tprelease"); raise RuntimeError(f"{who}: no ground at {gx:.0f},{gz:.0f}")
    r = s(f"tpground {gx} {gz}")
    if r.get("tpground") == "hop": until(grounded, 30, 1.0); s(f"tpground {gx} {gz}")
    time.sleep(settle)
    q = s("gpos"); print(f"  {who} at {q['gx']:.0f},{q['gz']:.0f}", flush=True)

def anomalies(s): return s("mp anomalies")

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
        time.sleep(5)
        hid = h("mp entities")["me"]
        hp = h("gpos")
        before = set(ents(h))
        print("  host car:", h(f"spawncarg {CAR05} 0 {hp['gx'] + 12} {hp['gz'] + 12} 0"), flush=True)
        car = until(lambda: next((n for n, e in ents(h).items() if n not in before and e["car"] and e["owner"] == hid), None), 20)
        if not car: sys.exit("the host's car was not shared")
        cp = ent(h, car)["pos"]
        L(f"tpground {cp[0] + 4} {cp[2] - 4}")
        until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(10)
        a0 = (anomalies(h)["count"], anomalies(L)["count"])
        print(f"  anomalies at the start: host {a0[0]}, laptop {a0[1]}", flush=True)

        # ---- 1 dev actions
        for i in (1, 2):
            travel(L, cp[0] - 600, cp[2], f"[1] laptop away ({i})")
            until(lambda: (ent(L, car) or {}).get("dormant") is True, 30)
            travel(L, cp[0] + 4, cp[2] - 4, f"[1] laptop back ({i})")
            until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(3)
        parts = h(f"mp attachedparts {car}")["parts"]
        hub = next((p for p in parts if "Disztarcsa" in p["name"]), None)
        if hub: print("  [1] hubcap off:", h(f"mp falloff {car} {hub['i']}"), flush=True); time.sleep(2)
        known = set(ents(h))
        wheel = next((p for p in h(f"mp attachedparts {car}")["parts"] if "Felni" in p["name"] or p["name"].startswith("Wheel")), None)
        if wheel:
            print("  [1] wheel off:", h(f"mp falloff {car} {wheel['i']}"), flush=True); time.sleep(3)
            wn = next((n for n, e in ents(h).items() if n not in known and ("Wheel" in e["name"] or "Felni" in e["name"])), None)
            if wn: print("  [1] the loose wheel deleted:", h(f"deleteshare {wn}"), flush=True); time.sleep(2)
        door = next((p for p in h(f"mp attachedparts {car}")["parts"] if "FRDoor" in p["name"]), None)
        if door:
            known = set(ents(h))
            print("  [1] door off:", h(f"mp falloff {car} {door['i']}"), flush=True); time.sleep(3)
            dn = next((n for n, e in ents(h).items() if n not in known and "Door" in e["name"]), None)
            if dn: print("  [1] door back on:", h(f"mp attachto {dn} {car} -1"), flush=True); time.sleep(3)
        print("  [1] car moved 30 m:", h(f"mp moveitem {car} 30 0"), flush=True); time.sleep(4)
        known = set(ents(h)); u = h("gpos"); gy = h(f"ground {u['ux'] + 5} {u['uz'] - 5}").get("y")
        if gy is not None:
            h(f"spawnpile {BOX} 1 {u['ux'] + 5} {gy + 1} {u['uz'] - 5} 1 1 0 7")
            bx = until(lambda: next((n for n, e in ents(h).items() if n not in known and "Box" in e["name"]), None), 15)
            if bx: until(lambda: bx in ents(L), 15); time.sleep(2); print("  [1] crate spawned and deleted:", h(f"deleteshare {bx}"), flush=True)
        cp = ent(h, car)["pos"]
        travel(L, cp[0] - 600, cp[2], "[1] laptop away (3)")
        until(lambda: (ent(L, car) or {}).get("dormant") is True, 30)
        travel(L, cp[0] + 4, cp[2] - 4, "[1] laptop back (3)")
        until(lambda: (ent(L, car) or {}).get("dormant") is False, 30)
        time.sleep(20)   # the census: 8 s after placing, again 6 s later if it differed
        ah, al = anomalies(h), anomalies(L)
        print(f"  census asks: host {ah['censusAsks']}, laptop {al['censusAsks']}; differed once: host {ah['censusDiffers']}, laptop {al['censusDiffers']}", flush=True)
        print(f"  store-outs seen: host {ah['storeOutsSeen']} (after a jump {ah['storeOutsAfterJump']}), laptop {al['storeOutsSeen']} (after a jump {al['storeOutsAfterJump']}); host census asked {ah['censusAsked']}", flush=True)
        verdict("1 DEV ACTIONS: NO ANOMALY ON EITHER MACHINE", ah["count"] == a0[0] and al["count"] == a0[1],
                f"host {ah['count'] - a0[0]} {ah['lines'][-3:]}, laptop {al['count'] - a0[1]} {al['lines'][-3:]}")
        verdict("1 THE LAPTOP'S CENSUS WAS ASKED (the watch ran)", al["censusAsks"] > 0, f"{al['censusAsks']} asks")

        # ---- 2 glitches on purpose
        n0 = anomalies(L)["count"]
        print("  [2a] the laptop's copy displaced 500 m:", L(f"mp displace {car} 500"), flush=True)
        time.sleep(2)
        al = anomalies(L)
        print(f"  [2a] laptop: store-outs seen {al['storeOutsSeen']} (after a jump {al['storeOutsAfterJump']}); farrec {L(f'mp farrec {car}')}", flush=True)
        verdict("2a A DISPLACED COPY IS CAUGHT", al["count"] > n0 and any("displaced" in l for l in al["lines"]), str(al["lines"][-2:]))
        # back: the owner's next state moves its record — the host nudges the car
        h(f"mp moveitem {car} 0.5 0")
        placed = until(lambda: (ent(L, car) or {}).get("dormant") is False, 30); time.sleep(10)
        print(f"  [2a] placed again after the owner's state: {bool(placed)}", flush=True)
        n1 = anomalies(L)["count"]
        print("  [2b] a mirror of the laptop's copy gone there only:", L(f"mp copypartgone {car} Mirror"), flush=True)
        time.sleep(12)
        al = anomalies(L)
        ah = anomalies(h)
        print(f"  [2b] laptop asks {al['censusAsks']}, replies in {al['censusRepliesIn']}, differed {al['censusDiffers']}; host asked {ah['censusAsked']}", flush=True)
        verdict("2b A PART MISSING ON A COPY IS CAUGHT", al["count"] > n1 and any("parts differ" in l and "Mirror" in l for l in al["lines"]), str(al["lines"][-1:]))
        print("  errors:", [s_("errors")["errors"] for s_ in (h, L)], flush=True)
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
