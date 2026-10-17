#!/usr/bin/env python3
"""Players' own sounds (v0.64.16, Entities.PlayerSound). Two machines over the relay: desktop hosts, laptop joins,
3 m apart. Footsteps, burps, farts and shits played only for the player who made them.
  0 the remote player's model: its own step scripts (muted in a session: steps come from the player)
  1 the host walks 4 s: the laptop plays its steps (unreliable: >= 90 %), at its feet, a real clip, playing
  2 burp, 3 fart, 4 shit: the laptop plays the same clip as the host, where the host is
  5 the laptop's steps reach the host - and are not played back to the laptop
  6 the laptop sees the host's turd (shit makes an item, outside mainscript.Spawn: Entities.PlayerMade)
  tools/soundtest.py [--no-build] [--relay iad|off]"""
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
            if fn(): return True
        except Exception: pass
        time.sleep(step)
    return False

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

def near(last, p, m):
    return last and math.hypot(last["gx"] - p["gx"], last["gz"] - p["gz"]) < m

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    hp = h("gpos")
    h(f"tpground {hp['gx'] + 15} {hp['gz']}"); time.sleep(3)   # out of the start building, on open ground
    hp = h("gpos"); L(f"tpground {hp['gx']} {hp['gz'] + 3}"); time.sleep(3)
    if RELAY != "off": h(f"net relay {RELAY}"); L(f"net relay {RELAY}")
    h("mp host 27070"); L(f"mp joinid {h('net me')['steamId']}")
    if not until(lambda: L("mp status")["client"]["ready"], 150): sys.exit("LAPTOP NEVER JOINED")
    L("set TLDRevamp.TestSafety.BlockAutoSave True")
    gods = {}
    for nm, s_ in (("host", h), ("laptop", L)):
        gods[nm] = (s_, s_("get kaposztaleves.s.settings.god").get("value"))
        s_("set kaposztaleves.s.settings.god True")
    try:
        time.sleep(5)
        ls = L("mp sounds"); print("  0 laptop:", ls, flush=True)
        verdict("0 THE REMOTE MODEL", ls["models"] >= 1, f"models {ls['models']}, step scripts {ls['modelStepScripts']}, step events {ls['modelStepEvents']}, muted {ls['modelStepsMuted']}")
        # 1 walking
        h0, l0 = h("mp sounds"), L("mp sounds")
        h("mp sounds walk 4"); time.sleep(6)
        h1, l1 = h("mp sounds"), L("mp sounds")
        sent = h1["sentBy"][0] - h0["sentBy"][0]; got = l1["playedBy"][0] - l0["playedBy"][0]
        hp = h("gpos")
        print(f"  1: host sent {sent} steps, laptop played {got}; last {l1['last']}; host now {hp}", flush=True)
        verdict("1 THE HOST'S STEPS", sent >= 4 and got >= 0.9 * sent and l1["last"]["kind"] == 0 and l1["last"]["clip"] != "" and near(l1["last"], hp, 4),
                f"{got}/{sent}, clip {l1['last']['clip']!r}")
        # 2-4 burp, fart, shit
        ents0 = {e["net"] for e in L("mp entities")["entities"]}; hents0 = {e["net"] for e in h("mp entities")["entities"]}
        for i, (what, src) in enumerate((("burp", "SBurp"), ("fart", "SFart"), ("shit", "SFart")), start=1):
            time.sleep(4)   # the last one has stopped (each is not played while its source still plays)
            l0 = L("mp sounds")
            r = h(f"mp sounds {what}")
            clip = h(f"get mainscript.s.player.{src}.clip.name").get("value")
            until(lambda: L("mp sounds")["playedBy"][i] > l0["playedBy"][i], 5, 0.2)
            l1 = L("mp sounds"); hp = h("gpos")
            print(f"  {i + 1} {what}: host clip {clip!r} (sent {r['sentBy'][i]}); laptop {l1['last']}", flush=True)
            verdict(f"{i + 1} {what.upper()}", l1["playedBy"][i] == l0["playedBy"][i] + 1 and l1["last"]["clip"] == clip and l1["last"]["playing"] and near(l1["last"], hp, 3),
                    f"clip {l1['last']['clip']!r} = host's {clip!r}")
        # 5 the laptop's steps
        h0, l0 = h("mp sounds"), L("mp sounds")
        L("mp sounds walk 4"); time.sleep(6)
        h1, l1 = h("mp sounds"), L("mp sounds"); lp = L("gpos")
        sent = l1["sentBy"][0] - l0["sentBy"][0]; got = h1["playedBy"][0] - h0["playedBy"][0]
        print(f"  5: laptop sent {sent}, host played {got}, laptop played its own {l1['playedBy'][0] - l0['playedBy'][0]}; host last {h1['last']}", flush=True)
        verdict("5 THE LAPTOP'S STEPS ON THE HOST, NOT BACK", sent >= 4 and got >= 0.9 * sent and l1["playedBy"][0] == l0["playedBy"][0] and near(h1["last"], lp, 4),
                f"{got}/{sent}")
        # 6 the turd
        time.sleep(2)
        hnew = [e for e in h("mp entities")["entities"] if e["net"] not in hents0]
        new = [e for e in L("mp entities")["entities"] if e["net"] not in ents0]
        print("  6 new on the host:", [(e["net"], e.get("name")) for e in hnew][:8], "laptop:", [(e["net"], e.get("name")) for e in new][:8],
              "made shared:", h("mp sounds")["playerMadeShared"], flush=True)
        verdict("6 THE TURD", len(hnew) == 1 and {e["net"] for e in new} == {e["net"] for e in hnew}, f"host {len(hnew)} new, laptop {len(new)}")
    finally:
        for nm, (s_, v) in gods.items():
            try: s_(f"set kaposztaleves.s.settings.god {v if v is not None else False}")
            except Exception as ex: print("god restore", nm, ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
L.close()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
