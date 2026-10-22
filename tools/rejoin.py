#!/usr/bin/env python3
"""Per-player saves on the host (Net/PlayerSave.cs, v0.67.0). Two machines over the relay:
  the laptop spawns two lamps, puts one away (stored in another slot) and holds the other, gets hungry and thirsty,
  walks 50 m, leaves, and joins again.
  A  on leave: both lamps leave the world with it (gone on the host, nothing on the ground), the host keeps 2 items
  B  back: placed where it left (< 3 m), food and water as they were, both lamps back (one in the hand, one stored),
     shared again and the laptop's
  tools/rejoin.py [--no-build] [--relay iad|off] [--old: PlayerSave off — fresh at the join spot, lamps left behind]"""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import until, g

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
LAMP = arg("--item", 320, int)
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def ents(s): return s("mp entities")["entities"]
def row(s, n): return next((e for e in ents(s) if e["net"] == n), None)
def pos(s):
    q = s("gpos"); return q["gx"], q.get("gy", q.get("y")), q["gz"]
def num(v):
    try: return float(v)
    except Exception: return float("nan")

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    if RELAY != "off": relay_both(h, L, RELAY)
    try:
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        for s_ in (h, L): s_(f"set TLDRevamp.Net.PlayerSave.Enabled {not OLD}")
        h("set gamemodesettingsscript.s.S.survival 2")   # the host plays with hunger; joiners take its mode (GameMode)
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        print(f"  {'OLD (no per-player saves)' if OLD else 'CURRENT BUILD'}", flush=True)
        rp = h("roadpoint"); x, z = rp["gx"], rp["gz"]
        h(f"tpground {x} {z}"); L(f"tpground {x + 3} {z}"); time.sleep(4)

        def lamp():
            before = {e["net"] for e in ents(L)}
            L(f"devspawn {LAMP}")
            if not until(lambda: any(e["net"] not in before and not e["car"] and not e["proxy"] for e in ents(L)), 15): sys.exit("NO LAMP")
            n = next(e["net"] for e in ents(L) if e["net"] not in before and not e["car"] and not e["proxy"])
            time.sleep(1.5); print("  laptop picks up", n, L(f"mp pickupnet {n}"), flush=True); time.sleep(2.5)
            return n
        a = lamp()
        n_inv = int(g(L, "mainscript.s.player.inventory.Count")); s0 = int(g(L, "mainscript.s.player.selectedInv")); s1 = (s0 + 1) % n_inv
        L(f"call mainscript.s.player.InvSwitchTo {s1} False"); time.sleep(2)   # lamp a stored in slot s0, slot s1 selected
        lx, ly, lz = pos(L)
        L(f"tpground {lx + 50} {lz}"); time.sleep(5)   # (a teleport lets go of what the hands hold: lamp b after it)
        b = lamp()                                                               # lamp b in the hand (slot s1)
        # hunger only counts in survival mode (survivalscript: food and water refilled every frame otherwise)
        print("  survival mode:", L("set gamemodesettingsscript.s.S.survival 2"), g(L, "gamemodesettingsscript.s.S.survival"), flush=True)
        mf, mw = num(g(L, "mainscript.s.player.survival.maxFood")), num(g(L, "mainscript.s.player.survival.maxWater"))
        print("  hunger:", L(f"set mainscript.s.player.survival.food {mf * 0.41:.3f}"), L(f"set mainscript.s.player.survival.water {mw * 0.33:.3f}"), flush=True)
        print("  in the hands:", g(L, "mainscript.s.player.pickedUp.name"), g(L, "mainscript.s.player.inHandP.name"), flush=True)
        time.sleep(2)
        left_at = pos(L)
        food0, water0 = num(g(L, "mainscript.s.player.survival.food")), num(g(L, "mainscript.s.player.survival.water"))
        ra, rb = row(L, a), row(L, b)
        print(f"  before leaving: at {left_at}, food {food0:.2f}, water {water0:.2f}, lamp a stored {ra and ra['stored']}, lamp b held/shown {rb and rb.get('shown')}", flush=True)
        print("  record:", L("mp playersave record"), flush=True); time.sleep(2)

        # ---- leave
        print("  laptop leaves:", L("mp stop"), flush=True); time.sleep(5)
        ps = h("mp playersave"); print("  host:", ps, flush=True)
        on_host = [n for n in (a, b) if row(h, n) is not None]
        mine = next((p for p in ps.get("players", [])), None)
        if OLD: verdict("A OLD: THE LAMPS STAY IN THE HOST'S WORLD", len(on_host) == 2, f"on the host: {on_host}")
        else:
            verdict("A ON LEAVE: BOTH LAMPS LEAVE THE WORLD", not on_host, f"still on the host: {on_host}")
            verdict("A THE HOST KEEPS THEM", mine is not None and mine["items"] == 2 and not mine["present"], str(mine))

        # ---- back
        L("set gamemodesettingsscript.s.S.survival 2")   # (does the setting survive the world reload?)
        # the host keeps hosting (as in a real session): only the laptop joins again
        sid = h("net me")["steamId"]
        trace = []   # where the laptop stands while it joins again (a rejoin landed 43 m away, 2026-10-22)
        def ready_traced():
            st = L("mp status")["client"]
            try: q = L("gpos"); trace.append((round(time.time() - t0, 1), round(q["gx"], 1), round(q["gz"], 1), st["ready"], L("tpground status")["waiting"]))
            except Exception: pass
            return st["ready"]
        for _try in range(3):
            t0 = time.time(); L(f"mp joinid {sid}")
            if until(ready_traced, 150): break
            L("mp stop"); time.sleep(3)
        else: sys.exit("LAPTOP NEVER REJOINED")
        for _ in range(16): ready_traced(); time.sleep(0.5)
        moves = [t for i, t in enumerate(trace) if i == 0 or math.dist(t[1:3], trace[i - 1][1:3]) > 1 or t[3:] != trace[i - 1][3:]]
        print("  rejoin trace (s, gx, gz, ready, placing):", moves, flush=True)
        print("  join spot:", g(L, "TLDRevamp.Net.Mp.JoinSpotLast"), "placed", g(L, "TLDRevamp.Net.Mp.JoinPlaced"), L("tpground status"), flush=True)
        for s_ in (h, L): s_(f"set TLDRevamp.Net.PlayerSave.Enabled {not OLD}")
        surv = g(L, "gamemodesettingsscript.s.S.survival")
        print("  survival mode after the rejoin:", surv, "food now", g(L, "mainscript.s.player.survival.food"), flush=True)
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        L("set gamemodesettingsscript.s.S.survival 2")
        time.sleep(8)
        back = pos(L); d = math.hypot(back[0] - left_at[0], back[2] - left_at[2])
        food1, water1 = num(g(L, "mainscript.s.player.survival.food")), num(g(L, "mainscript.s.player.survival.water"))
        lps = L("mp playersave"); print("  laptop:", lps, flush=True)
        held = g(L, "mainscript.s.player.pickedUp.name"); inhand = g(L, "mainscript.s.player.inHandP.name")
        hand = held if isinstance(held, str) and held not in ("", "null") else inhand
        me = L("mp status")["client"]["id"]
        mine_now = [e for e in ents(h) if e["owner"] == me and not e["car"]]
        print(f"  back: {d:.1f} m from where it left, food {food1:.2f}, water {water1:.2f}, hand {hand}, laptop's objects on the host {[(e['net'], e['name'], e['stored']) for e in mine_now]}", flush=True)
        if OLD:
            verdict("B OLD: FRESH AT THE JOIN SPOT", d > 20, f"{d:.1f} m")
        else:
            verdict("B PLACED WHERE IT LEFT", d < 3, f"{d:.1f} m")
            if surv != 2: print(f"INVALID  B FOOD AND WATER: the laptop's world has survival mode {surv} after the reload (food and water are refilled every frame; the joiner's game mode is its own)", flush=True)
            else: verdict("B FOOD AND WATER AS THEY WERE", abs(food1 - food0) < 0.05 * mf + 0.01 and abs(water1 - water0) < 0.05 * mw + 0.01 and food0 < 0.6 * mf, f"food {food0:.1f} → {food1:.1f}, water {water0:.1f} → {water1:.1f}")
            verdict("B BOTH LAMPS BACK, ONE IN THE HAND", lps.get("itemsRestored", 0) >= 2, lps.get("last", ""))
            verdict("B IN THE HAND", isinstance(hand, str) and "(Clone)" in hand, f"picked {held}, right hand {inhand}")
            verdict("B SHARED AGAIN, THE LAPTOP'S, ONE STORED", len(mine_now) >= 2 and any(e["stored"] for e in mine_now), str([(e['net'], e['stored']) for e in mine_now]))
        er = [x_("errors") for x_ in (h, L)]
        print("  errors:", [(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er], flush=True)
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er))
    finally:
        for s_ in (h, L):
            try: s_("set TLDRevamp.Net.PlayerSave.Enabled True")
            except Exception: pass
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"REJOIN: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
