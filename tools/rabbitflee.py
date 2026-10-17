#!/usr/bin/env python3
"""Rabbits notice every player, not only from the one whose game simulates them (two machines, relay iad). The
host's rabbit (Nyul, aiscript; the host simulates it). A: the host stands 3 m in front of it — it notices (a threat).
B: the host 60 m off, the laptop 3 m in front of it — it must notice the laptop too (v0.65.41). C: nobody near. The laptop's copy must be
where the host has it. Each phase on a fresh rabbit, with 'mp stimuli' (what animals sense of each player).
  tools/rabbitflee.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        R = (math.cos(math.radians(rp["yaw"])), -math.sin(math.radians(rp["yaw"])))
        base = (x + R[0] * 25, z + R[1] * 25)   # off the road, on the grass
        h(f"tpground {base[0]} {base[1]}"); L(f"tpground {base[0] + 60} {base[1]}"); time.sleep(3)
        print("stimuli host:", h("mp stimuli"), flush=True)
        print("stimuli laptop:", L("mp stimuli"), flush=True)
        for phase, near, far in (("A", h, L), ("B", L, h), ("C", None, None)):
            if far: far(f"tpground {base[0] + 60} {base[1] + 60}")
            h(f"tpground {base[0]} {base[1]}"); time.sleep(2)
            before = {e["net"] for e in h("mp entities")["entities"]}
            print(f"{phase}: rabbit", h("devspawn 103"), flush=True)
            if not until(lambda: any(e["net"] not in before and not e["car"] for e in h("mp entities")["entities"]), 15): verdict(f"{phase} RABBIT", False); continue
            net = next(e["net"] for e in h("mp entities")["entities"] if e["net"] not in before and not e["car"])
            if phase != "A": h(f"tpground {base[0] + 60} {base[1] + 60}")
            if phase == "C": L(f"tpground {base[0] - 60} {base[1] - 60}")
            time.sleep(3)
            # calm first: spawned in front of the host, it may already be running from the host (B run 1 was contaminated)
            calm, t0 = 0, time.monotonic()
            while time.monotonic() - t0 < 40 and calm < 10:
                calm = calm + 1 if not h(f"mp rabbit {net}")["fleeing"] else 0
                time.sleep(0.3)
            print(f"  calm for {calm * 0.3:.0f} s after {time.monotonic() - t0:.0f} s", flush=True)
            if calm < 10: verdict(f"{phase} RABBIT CALM BEFORE THE APPROACH", False); continue
            r = h(f"mp rabbit {net}"); print("  rabbit:", r, flush=True)
            p0 = r["pos"]; spot = (p0[0] + r["fwd"][0] * 3, p0[2] + r["fwd"][1] * 3)
            if near: near(f"tpground {spot[0]} {spot[1]}"); near(f"mp lookat {net}")
            fled, t0, offs, log, ffmax = False, time.monotonic(), [], [], 0.0
            while time.monotonic() - t0 < 8:
                r = h(f"mp rabbit {net}")
                fled = fled or r["fleeing"]; ffmax = max(ffmax, abs(r["ff"])); log.append(f"{time.monotonic() - t0:.1f}:{'F' if r['fleeing'] else '-'}{r['stimuli']}/{r['ff']}")
                try:
                    c = L(f"mp rabbit {net}"); offs.append(math.dist(c["pos"], r["pos"]))
                except Exception: pass
                time.sleep(0.3)
            moved = math.dist(r["pos"], p0)
            print("  samples (t:fleeing stimuli/flightFight):", " ".join(log), flush=True)
            # what counts is that it notices the player (the game's own reaction then varies: it flees, or — the
            # threat inverted to curiosity — it comes closer); a player is a threat of 10, background noise ~0.01
            if phase == "C": verdict("C CONTROL, NOBODY NEAR: IT SENSES NOTHING", ffmax < 1, f"max |flightFight| {ffmax}, fled {fled}, moved {moved:.1f} m")
            else: verdict(f"{phase} IT NOTICES {'THE HOST' if phase == 'A' else 'THE LAPTOP (not its simulator)'}", ffmax >= 5, f"max |flightFight| {ffmax}, fled {fled}, moved {moved:.1f} m")
            if offs: print(f"  copy vs host: median {sorted(offs)[len(offs) // 2]:.2f} m, max {max(offs):.2f} m ({len(offs)} samples)", flush=True)
            h(f"shot rabbit{phase}_host"); L(f"shot rabbit{phase}_laptop")
            try: h(f"deleteshare {net}")
            except Exception as e: print("  delete:", e)
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
