#!/usr/bin/env python3
"""Radio stations follow the host (v0.64.9, Entities.RadioSync). Two machines over the relay: desktop hosts, laptop joins.
Every station is a simulated broadcast (clip + time); each machine ran its own, so two players heard different songs.
  0 before the session: what each machine plays on its own (shows whether they differ at all)
  1 after joining: every station (not the players' own custom-music station) plays the same clip, < 0.5 s apart
  2 the host's station 0 moves to its next clip (as when a clip ends): the laptop follows
  3 20 s later: still the same clips, still < 0.5 s apart (no clock drift)
  tools/radiotest.py [--no-build] [--relay iad|off]"""
import sys, os, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
TOL = 0.5   # radioscript re-seeks its audio only beyond 0.5 s off the station

def until(fn, timeout=30, step=0.5):
    t = time.time()
    while time.time() - t < timeout:
        try:
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def compare(h, L, tag):
    """Both machines' stations at (nearly) the same moment; the laptop's query is timed and its times corrected for
    the seconds between the two reads."""
    t0 = time.time(); hr = h("mp radio"); t1 = time.time(); lr = L("mp radio"); t2 = time.time()
    lag = (t1 + t2) / 2 - (t0 + t1) / 2   # the laptop's read, this much later than the host's
    bad, n = [], 0
    for (hn, hc, hclip, ht), (ln, lc, lclip, lt) in zip(hr["stations"], lr["stations"]):
        if hc or lc: continue
        n += 1
        dt = (lt - lag) - ht
        if hclip != lclip or abs(dt) > TOL: bad.append((hn, hclip, ht, lclip, lt, round(dt, 2)))
    print(f"  {tag}: {n} stations, {len(bad)} differ (read lag {lag:.2f} s; host sent {hr['sent']}, laptop applied {lr['applied']})", flush=True)
    for b in bad[:8]: print(f"    {b[0]}: host {b[1]} @{b[2]} | laptop {b[3]} @{b[4]} (off {b[5]} s)", flush=True)
    return n, bad, hr

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    time.sleep(5)
    compare(h, L, "0 before the session (each machine its own broadcast)")
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    try:
        time.sleep(8)
        n, bad, _ = compare(h, L, "1 after joining")
        verdict("1 EVERY STATION PLAYS THE HOST'S CLIP", n > 0 and not bad, f"{n} stations, {len(bad)} differ")
        adv = h("mp radio advance 0"); print("  host station 0 ->", adv, flush=True)
        time.sleep(2)
        n, bad, hr = compare(h, L, "2 after the host's station 0 changed")
        lr0 = L("mp radio")["stations"][0]
        verdict("2 THE LAPTOP FOLLOWS A CLIP CHANGE", lr0[2] == adv.get("clip") and not bad, f"laptop station 0 plays {lr0[2]}")
        time.sleep(20)
        n, bad, _ = compare(h, L, "3 20 s later")
        verdict("3 STILL TOGETHER 20 S LATER", n > 0 and not bad, f"{len(bad)} differ")
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
