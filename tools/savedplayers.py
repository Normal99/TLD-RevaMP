#!/usr/bin/env python3
"""The host's list of saved players (MpScreen, PlayerSave.AwayPlayers / Forget). Two machines over the relay:
  the laptop picks up a lamp and leaves.
  A  the host's Multiplayer screen lists it after the players ("— away"), its card says what waits for it (1 thing,
     where it left), and Forget asks first ("Forget NAME?")
  B  No keeps it; Yes forgets it: gone from the list and from the records ("forgotten" counted)
  C  the laptop joins again: fresh (nothing restored, not placed where it left), the lamp nowhere on either machine
  tools/savedplayers.py [--no-build] [--relay iad|off]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, LAMP = arg("--relay", "iad"), arg("--item", 320, int)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return s("mp entities")["entities"]
def lamps(s): return [e for e in ents(s) if "regizseblampa" in e["name"].lower()]
def pos(s): q = s("gpos"); return q["gx"], q["gz"]

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        for s_ in (h, L): s_("set kaposztaleves.s.settings.god True")
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(4)
        before = {e["net"] for e in ents(L)}
        L(f"devspawn {LAMP}")
        new = lambda: next((e["net"] for e in ents(L) if e["net"] not in before and not e["car"] and not e["proxy"]), None)
        lamp = new() if until(new, 15) else None
        if not lamp: sys.exit("NO LAMP")
        time.sleep(1.5); print("  laptop picks up", lamp, L(f"mp pickupnet {lamp}"), flush=True); time.sleep(2.5)
        # into the pocket: another slot selected (a teleport lets go of what the hands hold)
        g = lambda path: L(f"get {path}")["value"]
        n_inv, s0 = int(g("mainscript.s.player.inventory.Count")), int(g("mainscript.s.player.selectedInv"))
        L(f"call mainscript.s.player.InvSwitchTo {(s0 + 1) % n_inv} False"); time.sleep(2)
        L(f"tpground {x + 60} {z}"); time.sleep(6)
        left_at = pos(L)
        print("  record:", L("mp playersave record"), flush=True); time.sleep(2)
        print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(5)

        # ---- A: the host's screen
        h("mp screen close"); sc = h("mp screen open")
        away = [i for i, t in enumerate(sc["tiles"]) if t.endswith("— away")]
        verdict("A THE SAVED PLAYER IS LISTED, AWAY", len(away) == 1, f"tiles {sc['tiles']}, count {sc['count']}")
        if away:
            sc = h(f"mp screen tile {away[0]}")
            lines = " | ".join(l for l in sc["lines"] if l)
            verdict("A ITS CARD: AWAY, CARRIES 1 THING, FORGET OFFERED", sc["cardRight"] == "Saved" and "Carries 1 thing" in lines and "Comes back where they left" in lines and sc["kick"],
                    f"{sc['card']} [{sc['cardRight']}]: {lines}; forget button {sc['kick']}")
            sc = h("mp screen kick")
            verdict("A FORGET ASKS FIRST", (sc["confirm"] or "").startswith("Forget "), f"confirm {sc['confirm']}")
            # ---- B
            h("mp screen no"); kept = h("mp playersave")
            verdict("B NO KEEPS IT", len(kept["players"]) == 1 and kept["forgotten"] == 0, f"records {kept['players']}")
            h(f"mp screen tile {away[0]}"); h("mp screen kick"); sc = h("mp screen yes")
            ps = h("mp playersave")
            verdict("B YES FORGETS IT", not any(t.endswith("— away") for t in sc["tiles"]) and len(ps["players"]) == 0 and ps["forgotten"] == 1,
                    f"tiles {sc['tiles']}, message {sc['message']}, records {ps['players']}, forgotten {ps['forgotten']}")
        h("mp screen close")

        # ---- C: the laptop comes back as new
        sid = h("net me")["steamId"]
        for _try in range(3):
            L(f"mp joinid {sid}")
            if until(lambda: L("mp status")["client"]["ready"], 150): break
            L("mp stop"); time.sleep(3)
        else: sys.exit("LAPTOP NEVER REJOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True"); L("set kaposztaleves.s.settings.god True")
        time.sleep(8)
        back = pos(L); d = math.hypot(back[0] - left_at[0], back[1] - left_at[1])
        lps = L("mp playersave")
        verdict("C FRESH: NOTHING RESTORED, NOT WHERE IT LEFT", lps["restored"] == 0 and d > 20, f"restored {lps['restored']}, {d:.0f} m from where it left")
        verdict("C THE LAMP IS NOWHERE", all(e["net"] != lamp for e in lamps(h) + lamps(L)), f"host {[e['net'] for e in lamps(h)]}, laptop {[e['net'] for e in lamps(L)]} (the world's own lamps stay)")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try: h("mp screen close")
        except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
print(f"SAVEDPLAYERS: {sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} PASS")
