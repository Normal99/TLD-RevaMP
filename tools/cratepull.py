#!/usr/bin/env python3
"""A crate's copy pulled a stranger in (crates.py diagnostics, 2026-10-09: when the host spawned a crate, the laptop's
copy of a plank lying 14 m away on a building's shelf moved next to the crate; it dropped when the crate broke — the
host's plank never moved, no state was sent for it). Two machines over the relay: the laptop joins next to the host;
every laptop copy's place recorded; the host spawns one crate; the laptop's copies polled for 10 s — which moved, when,
and which container holds it then (`mp physlocks`: container id/name, the entity it belongs to).
  tools/cratepull.py [--no-build] [--relay iad|off] [--old: copies run their start freeze, before v0.66.3]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD, BOX = arg("--relay", "iad"), "--old" in sys.argv, arg("--box", 62, int)
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

def ents(s): return {e["net"]: e for e in s("mp entities")["entities"] if not e["dormant"]}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_(f"set TLDRevamp.Net.Entities.CopyStartFreezeOff {not OLD}")
        print(f"  {'OLD (copies run their start freeze)' if OLD else 'NEW'}", flush=True)
        time.sleep(5)
        hp = h("gpos"); L(f"tpground {hp['gx'] + 4} {hp['gz'] - 6}"); time.sleep(6)
        box = BOX or next(i["id"] for i in h("items Box")["items"] if i["name"] == "Box")
        L("set TLDRevamp.Net.Entities.StoreTrace 20")
        l0 = ents(L)
        me = h("mp entities")["me"]; before = set(ents(h))
        u = h("gpos"); sx, sz = u["ux"] + 6, u["uz"] + 6
        gy = h(f"ground {sx} {sz}").get("y")
        print(f"  crate (item {box}):", h(f"spawnpile {box} 1 {sx} {gy + 1.0} {sz} 1 1 0 1"), flush=True)
        t0 = time.time(); moved = {}
        while time.time() - t0 < 10:
            for n, e in ents(L).items():
                if n in l0 and n not in moved and math.dist(e["pos"], l0[n]["pos"]) > 0.5:
                    moved[n] = (round(time.time() - t0, 2), e["name"], [round(v, 1) for v in l0[n]["pos"]], [round(v, 1) for v in e["pos"]])
            time.sleep(0.25)
        crate = [n for n, e in ents(h).items() if n not in before and e["owner"] == me]
        print(f"  crate net {crate}; laptop copies that moved: {moved}", flush=True)
        g = L("gpos")
        rows = L(f"mp physlocks {g['gx']} {g['gz']} 40")["items"]
        for n, m in moved.items():
            for r in rows:
                if r[8] == n: print(f"    net {n} {m[1]}: lock {r[5]!r}, container #{r[6]} {r[7]!r}, body {r[11]}", flush=True)
        hm = ents(h)
        strangers = {n: m for n, m in moved.items() if n in hm and math.dist(hm[n]["pos"], ents(L)[n]["pos"]) > 1.0}
        verdict("NO COPY PULLED AWAY FROM WHERE ITS OWNER HAS IT", not strangers, str(strangers))
        print("  start freezes skipped (laptop):", L("get TLDRevamp.Net.Entities.CopyStartFreezesSkipped")["value"], "snapshot drops:", L("get TLDRevamp.Net.ItemSnapshot.ForeignRefsDropped")["value"], "laptop errors:", L("errors")["errors"], flush=True)
    finally:
        for s_ in (h, L):
            try: s_("set TLDRevamp.Net.Entities.CopyStartFreezeOff True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
