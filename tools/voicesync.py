#!/usr/bin/env python3
"""Proximity voice between the two machines (relay iad by default), with the test tone (`mp voice tone <s>`: PCM16 through
the real send -> server relay -> playback path, no microphone needed).
  1 the laptop talks 5 m from the host: the host's speaker for it fills, has level, plays, and the body talks
  2 the host talks: the same on the laptop
  3 the laptop 150 m away (beyond Voice.RangeM): nothing reaches the host
  4 back near: it works again
  5 bursty Wi-Fi: gaps in the host's voice on the laptop (--ab: the fixed 80 ms buffer first)
  tools/voicesync.py [--no-build] [--relay iad|off] [--ab]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both
from roadkit import until, g

RELAY = sys.argv[sys.argv.index("--relay") + 1] if "--relay" in sys.argv else "iad"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def speaker(x, pid):
    st = x("mp voice"); sp = next((s for s in st["speakers"] if s["id"] == pid), None)
    return st, sp

def talk(talker, listener, pid, secs=3.0):
    st0, sp0 = speaker(listener, pid)
    talker(f"mp voice tone {secs}")
    best, talking = None, False
    t0 = time.monotonic()
    while time.monotonic() - t0 < secs + 1.0:
        st, sp = speaker(listener, pid)
        if sp: best = sp if best is None or sp["level"] > best["level"] else best; talking = talking or sp["talking"]
        time.sleep(0.4)
    st1, sp1 = speaker(listener, pid)
    pin = st1["in"] - st0["in"]; played = st1["played"] - st0["played"]
    return pin, played, best, talking

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
        h(f"tpground {x} {z}"); L(f"tpground {x + 5} {z}"); time.sleep(4)
        pin, played, best, talking = talk(L, h, 1)
        print("  host hears the laptop:", pin, "packets in,", played, "played, best", best, flush=True)
        verdict("1 THE HOST HEARS THE LAPTOP", pin > 10 and played > 10 and best and best["level"] > 0.001 and best["playing"] and talking, f"in {pin}, played {played}, level {best and best['level']}, talking {talking}")
        pin, played, best, talking = talk(h, L, 0)
        print("  laptop hears the host:", pin, "packets in,", played, "played, best", best, flush=True)
        verdict("2 THE LAPTOP HEARS THE HOST", pin > 10 and played > 10 and best and best["level"] > 0.001 and best["playing"] and talking, f"in {pin}, played {played}, level {best and best['level']}, talking {talking}")
        L(f"tpground {x + 150} {z}"); time.sleep(5)
        pin, played, best, talking = talk(L, h, 1)
        verdict("3 OUT OF EARSHOT (150 m): NOTHING REACHES THE HOST", pin == 0, f"in {pin}, played {played}")
        L(f"tpground {x + 5} {z}"); time.sleep(5)
        pin, played, best, talking = talk(L, h, 1)
        verdict("4 BACK NEAR: HEARD AGAIN", pin > 10 and played > 10 and best and best["level"] > 0.001, f"in {pin}, played {played}, level {best and best['level']}")
        # 5 the laptop's bursty Wi-Fi (power saving, ~300 ms bursts): mid-speech gaps in the host's voice on the laptop
        #   (underruns beyond the one at the end of each utterance), adaptive buffer off (A) then on (B)
        for adaptive in ([False, True] if "--ab" in sys.argv else [True]):
            L(f"set TLDRevamp.Net.Voice.AdaptiveJitter {adaptive}")
            gaps, primes = 0, []
            for k in range(3):
                u0 = (speaker(L, 0)[1] or {}).get("underruns", 0)
                pin, played, best, talking = talk(h, L, 0, 6.0)
                sp = speaker(L, 0)[1] or {}
                gaps += max(0, sp.get("underruns", 0) - u0 - 1); primes.append(sp.get("primeMs"))
                time.sleep(1.5)
            tag = "B ADAPTIVE BUFFER" if adaptive else "A FIXED 80 MS (before v0.65.31)"
            verdict(f"5 {tag}: THE HOST'S VOICE ON THE LAPTOP WITHOUT GAPS (one while it learns the link)", gaps <= 1 if adaptive else True, f"{gaps} gaps in 3 x 6 s; buffer {primes} ms")
        L("set TLDRevamp.Net.Voice.AdaptiveJitter True")
        er = [s_("errors") for s_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
