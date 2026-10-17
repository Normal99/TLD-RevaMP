#!/usr/bin/env python3
"""Hosting again in the same game (no restart): does a joiner get everything?
Player and network ids start at 1 again every session; the server's per-player "already sent" sets (Entities.Known)
must not carry over — ServerOnJoin starts each joiner with an empty one (checked 2026-10-08: a suspected bug that
wasn't). Three sessions in a row in the same two games, ended both ways (the laptop leaving first; the host ending it
with the laptop still in): the laptop has everything the host shares each time.
  tools/rehost.py [--no-build] [--relay iad|off] [--wait 20]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
WAIT = int(sys.argv[sys.argv.index("--wait") + 1]) if "--wait" in sys.argv else 20
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

def nets(x): return {e["net"]: e for e in x("mp entities")["entities"]}

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    sid = h("net me")["steamId"]

    def session(tag):
        h("mp host 27070"); L(f"mp joinid {sid}")
        if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit(f"{tag}: LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        lid = L("mp status")["client"].get("id")
        time.sleep(WAIT)   # interest ticks twice a second, 150 objects a tick
        hn, ln = nets(h), nets(L)
        missing = sorted(set(hn) - set(ln))
        print(f"  {tag}: laptop id {lid}; host shares {len(hn)}, laptop has {len(ln)}, missing {len(missing)}: "
              f"{[(n, hn[n].get('name')) for n in missing[:12]]}{'...' if len(missing) > 12 else ''}", flush=True)
        return missing

    try:
        m1 = session("SESSION 1 (fresh)")
        L("mp stop"); time.sleep(1); h("mp stop"); time.sleep(4)          # the laptop leaves first
        m2 = session("SESSION 2 (hosted again; the laptop had left first)")
        h("mp stop"); time.sleep(2); L("mp stop"); time.sleep(3)          # the host ends it with the laptop in
        m3 = session("SESSION 3 (hosted again; the host had ended it)")
        for tag, m in (("SESSION 2", m2), ("SESSION 3", m3)):
            extra = sorted(set(m) - set(m1))
            verdict(f"{tag}: HOSTED AGAIN, THE JOINER GETS EVERYTHING SESSION 1 GOT", len(extra) == 0, f"{len(extra)} missing beyond session 1's {len(m1)}")
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
