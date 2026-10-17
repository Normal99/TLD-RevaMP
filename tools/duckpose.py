#!/usr/bin/env python3
"""Crouching and lying down seen by the other player (two machines, relay iad by default). The laptop's player goes
standing → half crouch → prone (the game's duckValue, as the scroll wheel sets it) → standing; at each, the host's
view of the laptop's body must hold its head where the laptop's own animation has it (head target above the body
origin, within 0.1 m). Screenshots of the laptop's body from the host at each pose.
  tools/duckpose.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

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
        h(f"tpground {x} {z}"); L(f"tpground {x + 4} {z}"); time.sleep(4)
        until(lambda: h("mp headh")["remote"], 20)
        lid = next(iter(h("mp headh")["remote"]))
        for tag, v in (("standing", 1.0), ("half crouch", 0.5), ("prone", 0.0), ("standing again", 1.0)):
            L("set mainscript.s.player.getup False"); L("set mainscript.s.player.lastDucked False")
            L(f"set mainscript.s.player.duckValue {v}"); time.sleep(2.5)
            a = L("mp headh"); b = h("mp headh")
            mine, seen = a["me"], b["remote"].get(lid)
            print(f"  {tag}: laptop duck {a['duck']} head {mine} | host sees head {seen}", flush=True)
            h(f"mp lookplayer {lid}"); time.sleep(0.5); h(f"shot duck_{tag.replace(' ', '_')}")
            verdict(f"{tag.upper()}: THE HOST SEES THE LAPTOP'S HEAD WHERE IT IS", seen is not None and abs(mine - seen) < 0.1 and abs(a["duck"] - v) < 0.05, f"{mine} vs {seen}")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: L("set mainscript.s.player.duckValue 1")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
