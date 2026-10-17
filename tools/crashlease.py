#!/usr/bin/env python3
"""A crash lease when the hit car's owner leaves (Entities.CrashAuthority): the machine whose car hits another player's
takes the hit car for up to 4 s (CrashReturnTo = its owner), then the server hands it back. If that owner left in
between, the car went back to a player who was gone: nobody simulated it again, frozen on every screen.
The host spawns a bus, the laptop takes the wheel (owns it); the host starts a crash lease on it (`mp crashlease`,
the server's claim without staging a crash); the laptop leaves at once. 6 s later (lease 4 s): who owns the bus on the
host, and is it simulated there.
  round 1: the old behaviour (CrashLeaveKeep False) - expected: owned by the departed player, a frozen copy
  round 2: the fix - the host keeps it and simulates it
  tools/crashlease.py [--no-build] [--relay iad|off]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
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

def ent(x, net): return next((e for e in x("mp entities")["entities"] if e["net"] == net), None)

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    sid = h("net me")["steamId"]
    h("mp host 27070")
    gods = {}

    def join():
        L(f"mp joinid {sid}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        gods["laptop"] = gods.get("laptop") or L("get kaposztaleves.s.settings.god").get("value"); L("set kaposztaleves.s.settings.god True")
        return L("mp status")["client"].get("id")

    try:
        gods["host"] = h("get kaposztaleves.s.settings.god").get("value"); h("set kaposztaleves.s.settings.god True")
        lid = join()
        rp = h("roadpoint"); h(f"tpground {rp['gx']} {rp['gz']}"); time.sleep(3)
        before = {e["net"] for e in h("mp entities")["entities"]}
        print("bus:", h("devspawn 106"), flush=True)
        if not until(lambda: any(e["car"] and e["net"] not in before for e in h("mp entities")["entities"]), 20): sys.exit("NO BUS")
        NET = next(e["net"] for e in h("mp entities")["entities"] if e["car"] and e["net"] not in before)
        for rnd, keep in ((1, False), (2, True)):
            p = ent(h, NET)["pos"]; L(f"tpground {p[0] + 5} {p[2]}"); time.sleep(3)
            if not until(lambda: ent(L, NET) is not None, 30): sys.exit("THE LAPTOP NEVER GOT THE BUS")
            for _ in range(6):
                r = L(f"mp sitdriver {NET}")
                if "seat" in r and not r.get("stepped"): break
                time.sleep(1.5)
            ok = until(lambda: ent(h, NET)["owner"] == lid, 15)
            print(f"  round {rnd}: laptop (id {lid}) at the wheel: {r}; host sees owner {ent(h, NET)['owner']}", flush=True)
            if not ok: verdict(f"ROUND {rnd} SETUP: THE LAPTOP OWNS THE BUS", False, "INVALID"); continue
            h(f"set TLDRevamp.Net.Entities.CrashLeaveKeep {keep}")
            print("  host's crash lease:", h(f"mp crashlease {NET}"), flush=True)
            L("mp stop")
            time.sleep(6)
            e = ent(h, NET)
            tag = "OLD BEHAVIOUR (expected FAIL)" if not keep else "FIX"
            print(f"  round {rnd}: 6 s after the laptop left - host sees owner {e['owner']} proxy {e['proxy']}; kept {h('get TLDRevamp.Net.Entities.CrashLeasesKept')['value']}", flush=True)
            verdict(f"ROUND {rnd} {tag}: THE BUS IS SIMULATED BY SOMEONE STILL HERE", e["owner"] == 0 and not e["proxy"], f"owner {e['owner']} (the laptop was {lid}), proxy {e['proxy']}")
            if rnd == 1:
                lid = join()
    finally:
        h("set TLDRevamp.Net.Entities.CrashLeaveKeep True")
        for nm, s_ in (("host", h), ("laptop", L)):
            try: s_(f"set kaposztaleves.s.settings.god {gods.get(nm) if gods.get(nm) is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
