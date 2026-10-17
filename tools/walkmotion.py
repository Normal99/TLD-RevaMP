#!/usr/bin/env python3
"""How smoothly a walking player moves on the other machine (relay iad by default). Each player in turn walks towards a
car 40 m off for 10 s (FillLab walk: forward held); the other machine's body of them is measured in-game, every frame:
speed mean / p10 / p50 / p90 over the last 240 frames (`mp players` motion). Smooth walking: p10 near the mean; bursts
(states arriving in clumps, then a stop) show as p10 ~0 and a high p90.
  tools/walkmotion.py [--no-build] [--relay iad|off] [--tag <label>] [--old: before v0.65.32] [--old-poses: before v0.65.33]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
L = Agent()
ensure_both("--no-build" not in sys.argv)
res = {}
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    if "--old-poses" in sys.argv:   # poses handed over as they arrive (before v0.65.33)
        for s_ in (h, L): s_("set TLDRevamp.Net.RemotePlayers.PacePoses False")
    if "--old" in sys.argv:   # A: bodies ease towards the newest state (before v0.65.32)
        for s_ in (h, L): s_("set TLDRevamp.Net.RemotePlayers.InterpBodies False")
    gods = {}
    try:
        for name, s_ in (("host", h), ("laptop", L)):
            gods[name] = (s_, g(s_, "kaposztaleves.s.settings.god")); s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); time.sleep(2)
        hb = {e["net"] for e in h("mp entities")["entities"]}
        print("target car:", h(f"spawncarg 2 0 {x + 45} {z} 0"), flush=True); time.sleep(3)
        car = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in hb)
        until(lambda: any(e["net"] == car for e in L("mp entities")["entities"]), 20)
        for walker, watcher, wid, wname in ((L, h, 1, "laptop walks, host watches"), (h, L, 0, "host walks, laptop watches")):
            walker(f"tpground {x} {z + 3}") if walker is L else walker(f"tpground {x} {z - 3}")
            time.sleep(3)
            p0 = walker("mp players")["me"]
            walker(f"mp walkat {car} 9"); time.sleep(7.5)
            prow = next((p for p in watcher("mp players")["players"] if p["id"] == wid), {})
            m = prow.get("motion"); pg = prow.get("poseGapMs")
            if m is not None: m = dict(m, poseGapMs=pg)
            p1 = walker("mp players")["me"]
            own = math.dist(p0[::2], p1[::2]) / 7.5
            time.sleep(2)
            print(f"  {wname}: own speed ~{own:.2f} m/s; shown there {m}", flush=True)
            res[wname] = (own, m)
        print("ping:", L("net route").get("ping"), flush=True)
    finally:
        for name, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", name, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nRESULT", "--tag" in sys.argv and sys.argv[sys.argv.index("--tag") + 1] or "")
for k, (own, m) in res.items():
    if m: print(f"  {k}: own {own:.2f} m/s; shown mean {m['mean']} p10 {m['p10']} p50 {m['p50']} p90 {m['p90']} ({m['n']} frames); pose gaps ms {m.get('poseGapMs')}")
