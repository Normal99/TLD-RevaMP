#!/usr/bin/env python3
"""The host's game mode for joiners (Net/GameMode.cs, v0.67.0). Two machines over the relay. The host plays with
survival on and peaceful enemies; the laptop's own mode is survival off, enemies hostile.
  A  before joining: the laptop sees the host's mode in its rich presence (the multiplayer menu's card)
  B  joined: the laptop plays by the host's mode (survival on: food it loses stays lost; enemies peaceful)
  C  its own settings file is never written meanwhile (the game's SaveSettings is held back)
  D  after leaving: its own mode again
  tools/gamemode.py [--no-build] [--relay iad|off] [--old: Sync off — the joiner keeps its own mode]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from mplib import Agent, ensure_both, seeded_both, join_both, relay_both
from roadkit import g

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
RELAY, OLD = arg("--relay", "iad"), "--old" in sys.argv
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def num(v):
    try: return float(v)
    except Exception: return float("nan")
S = "gamemodesettingsscript.s.S"

L = Agent()
ensure_both("--no-build" not in sys.argv)
with game.ProtectedSession():
    h = seeded_both(L)
    for s_ in (h, L): s_("set TLDRevamp.TestSafety.BlockAutoSave True")
    own = {k: g(L, f"{S}.{k}") for k in ("survival", "peacefulEnemies")}
    print("  the laptop's own mode:", own, flush=True)
    try:
        h(f"set {S}.survival 2"); h(f"set {S}.peacefulEnemies 2")
        L(f"set {S}.survival 1"); L(f"set {S}.peacefulEnemies 1")
        L(f"set TLDRevamp.Net.GameMode.Sync {not OLD}")
        if RELAY != "off": relay_both(h, L, RELAY)
        if not join_both(h, L): sys.exit("LAPTOP NEVER JOINED")
        L("set TLDRevamp.TestSafety.BlockAutoSave True")
        print(f"  {'OLD (the joiner keeps its own mode)' if OLD else 'CURRENT BUILD'}", flush=True)
        sid = h("net me")["steamId"]
        time.sleep(4)
        fh = L(f"mp friendhost {sid}"); print("  the host as the laptop's menu sees it:", fh, flush=True)
        verdict("A THE MENU SHOWS THE HOST'S MODE", fh.get("hosting") and "Survival on" in fh.get("mode", "") and "peaceful" in fh.get("mode", ""), str(fh))
        surv, peace = g(L, f"{S}.survival"), g(L, f"{S}.peacefulEnemies")
        mf = num(g(L, "mainscript.s.player.survival.maxFood"))
        L(f"set mainscript.s.player.survival.food {mf * 0.5:.3f}"); time.sleep(3)
        food = num(g(L, "mainscript.s.player.survival.food"))
        print(f"  joined: survival {surv}, peaceful {peace}, food after 3 s {food:.2f} of {mf:.2f}; {L('mp gamemode')}", flush=True)
        if OLD: verdict("B OLD: THE LAPTOP KEEPS ITS OWN MODE", surv == 1 and food > 0.9 * mf, f"survival {surv}, food {food:.2f}")
        else: verdict("B THE LAPTOP PLAYS BY THE HOST'S MODE", surv == 2 and peace == 2 and food < 0.6 * mf, f"survival {surv}, peaceful {peace}, food {food:.2f}")
        if not OLD:
            b0 = L("mp gamemode")["savesBlocked"]
            L("call gamemodesettingsscript.s.SaveSettings")
            verdict("C ITS OWN SETTINGS FILE IS NOT WRITTEN", L("mp gamemode")["savesBlocked"] == b0 + 1, str(L("mp gamemode")))
        L("mp stop"); time.sleep(2)
        surv, peace = g(L, f"{S}.survival"), g(L, f"{S}.peacefulEnemies")
        verdict("D AFTER LEAVING: ITS OWN MODE", surv == 1 and peace == 1, f"survival {surv}, peaceful {peace}")
        er = [x_("errors") for x_ in (h, L)]
        verdict("NO ERRORS", all(e["errors"] == 0 and e["exceptions"] == 0 for e in er), str([(e["errors"], e["exceptions"], (e.get('kinds') or [])[:3]) for e in er]))
    finally:
        try:
            L("set TLDRevamp.Net.GameMode.Sync True")
            for k, v in own.items(): L(f"set {S}.{k} {v}")
        except Exception as ex: print("restore", ex)
        L("mp stop"); h("mp stop")
        if RELAY != "off": h("net relay off"); L("net relay off")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"GAMEMODE: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
