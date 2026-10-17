#!/usr/bin/env python3
"""Tumbleweeds are shared (v0.64.17, Entities.Tumbleweeds). Two machines over the relay, both on sand 10 m apart.
Each machine spawned its own around its own player, unshared. Rows: [net, owner, gx, gy, gz, speed, wind, proxy, active]
  1 the host's spawner makes one: the laptop gets it
  2 the wind blows it on the host (it moves); the laptop's copy goes with it (< 3 m)
  3 the laptop's spawner makes one: the host gets it, owned by the laptop
  4 the limit counts the other player's tumbleweeds too
  5 the host goes 3 km off: no copy on the laptop of a tumbleweed its owner no longer has
  tools/tumbletest.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            v = fn()
            if v: return v
        except Exception: pass
        time.sleep(step)
    return None

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def rows(s): return {r[0]: r for r in s("mp tumble")["tumbleweeds"] if r[0]}

def spawn(s):
    for _ in range(20):
        r = s("mp tumble spawn")
        if r["did"] == "spawned": return r
        time.sleep(0.3)
    return None

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    sand = h("mp storms sand"); print("  sand:", sand, flush=True)
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    lid = L("mp status")["client"].get("id")
    gods = {}
    for nm, s_ in (("host", h), ("laptop", L)):
        gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        # to the sand after joining: a joining player starts where the host's world puts them (runs 1-5: before
        # joining, the laptop was put back at the start, 7 km off - beyond interest)
        h(f"tpground {sand['gx']} {sand['gz']}"); L(f"tpground {sand['gx'] + 10} {sand['gz']}"); time.sleep(8)
        print("  on sand: host", h("gpos"), "laptop", L("gpos"), flush=True)
        h0 = set(rows(h))
        r = spawn(h); print("  1 host spawn:", r and r["did"], flush=True)
        new = [n for n in rows(h) if n not in h0]
        net = new[0] if new else None
        got = net and until(lambda: net in rows(L), 8, 0.3)
        st = L("mp entities")
        print("  1 laptop:", L("mp tumble")["tumbleweeds"], "\n    laptop entity:", [e for e in st["entities"] if e["net"] == net],
              "\n    laptop add rejects:", st.get("addRejects"), st.get("addRejectLast"), flush=True)
        print("    host server view:", h("mp tumble")["server"], flush=True)
        verdict("1 THE HOST'S TUMBLEWEED ON THE LAPTOP", bool(got) and rows(L)[net][8], f"net {net}, active {got and rows(L)[net][8]}")
        if net:
            a = rows(h)[net]; time.sleep(10)
            hr, lr = h("mp tumble"), L("mp tumble")
            b = {x[0]: x for x in hr["tumbleweeds"]}[net]; c = {x[0]: x for x in lr["tumbleweeds"]}.get(net)
            moved = math.hypot(b[2] - a[2], b[4] - a[4]); off = c and math.hypot(b[2] - c[2], b[4] - c[4])
            print(f"  2: host {b}\n     laptop {c}\n     moved {moved:.1f} m; laptop copy {off} m off", flush=True)
            verdict("2 BLOWN ON THE HOST, THE COPY GOES WITH IT", moved > 1 and b[6] and not b[7] and c and c[7] and not c[6] and off < 3,
                    f"moved {moved:.1f} m, copy {off and round(off, 2)} m off")
        l0 = set(rows(L))
        r = spawn(L); print("  3 laptop spawn:", r and r["did"], flush=True)
        until(lambda: [n for n in rows(L) if n not in l0], 5, 0.2)   # its id comes back from the host
        new = [n for n in rows(L) if n not in l0]
        net2 = new[0] if new else None
        got = net2 and until(lambda: net2 in rows(h), 8, 0.3)
        hr = rows(h)
        verdict("3 THE LAPTOP'S TUMBLEWEED ON THE HOST", bool(got) and hr[net2][1] == lid and hr[net2][7], f"net {net2}, owner {got and hr[net2][1]} (laptop {lid})")
        # 4 the limit
        lt = L("mp tumble"); print(f"  4 laptop: listed {lt['listed']} near {lt['near']} limit {lt['limit']} dist {lt['dist']}", flush=True)
        for _ in range(lt["limit"] + 3): L("mp tumble spawn"); time.sleep(0.2)
        time.sleep(2)
        lt = L("mp tumble"); hcount = sum(1 for x in lt["tumbleweeds"] if x[7]); own = sum(1 for x in lt["tumbleweeds"] if not x[7])
        print(f"     after: listed {lt['listed']} near {lt['near']} limited {lt['limited']}; own {own}, the host's here {hcount}", flush=True)
        verdict("4 THE LIMIT COUNTS THE OTHER'S", lt["limited"] > 0 and hcount >= 1 and own + hcount == lt["limit"] == lt["near"],
                f"own {own} + the host's {hcount} = {own + hcount} (limit {lt['limit']})")
        # 5 the host leaves
        hp = h("gpos"); h(f"tpground {hp['gx'] + 3000} {hp['gz']}"); time.sleep(15)
        hr, lr = rows(h), rows(L)
        orphans = [n for n, x in lr.items() if x[1] == 0 and n not in hr]
        print(f"  5: host has {sorted(hr)}; laptop {[(n, x[1]) for n, x in lr.items()]}", flush=True)
        verdict("5 NO ORPHANED COPIES", not orphans, f"laptop copies the host no longer has: {orphans}")
    finally:
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
