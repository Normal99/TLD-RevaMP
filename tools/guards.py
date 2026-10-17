#!/usr/bin/env python3
"""The two error floods of the 2026-10-09/10 playtest, desktop only, A/B in one run (each guard off, then on):
  WORN: something worn destroyed while on the player — makeinvisible.PreRender/PostRender threw a NullReferenceException
        twice a frame until the game was restarted (client 1: 16,840 pairs). Fixes.WornDestroyed.
  FLUID: a tank with a fluid type the game has no entry for — GetColor/GetCapString threw IndexOutOfRangeException every
        frame it was drawn (on every machine). Fixes.FluidTypeGuard: a record dropped on load, the readers guarded.
  tools/guards.py [--no-build] [--wear ITEM] [--tank 72]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
TANK = arg("--tank", 72, int)
W, F = "TLDRevamp.Fixes.WornDestroyed", "TLDRevamp.Fixes.FluidTypeGuard"
VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
def kinds(b, word):
    return sum(k["n"] for k in b("errors").get("kinds", []) if word in k["what"])

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    game.newgame()
    b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
    try:
        wearables = b("prefabswith wearable")["items"]
        wear = arg("--wear", None, int) or next((i for i, n in wearables if "hat" in n.lower() or "cap" in n.lower()), wearables[0][0])
        print(f"  wearable {wear} ({dict(wearables).get(wear)}), tank {TANK}", flush=True)
        def spawn(item, ahead=1.5):
            u = b("gpos"); x, z = u["ux"] + ahead, u["uz"]
            b(f"spawnpile {item} 1 {x} {b(f'ground {x} {z}').get('y') + 0.3} {z} 1 1 0 5"); time.sleep(2)
        for on in (False, True):
            tag = "GUARD ON" if on else "GUARD OFF (the bug)"
            b(f"set {W}.Enabled {on}"); b(f"set {F}.Enabled {on}")
            b("errors reset"); time.sleep(1)
            spawn(wear)
            r = b("guardlab worn"); print(f"  {tag} worn:", r, flush=True)
            time.sleep(3)
            nre, st = kinds(b, "makeinvisible"), b("guardlab")
            print(f"  {tag} after 3 s: makeinvisible errors {nre}, {st}", flush=True)
            if on: verdict("WORN, GUARD ON: NO ERRORS, OFF THE PLAYER", "error" not in r and nre == 0 and st["hideListGone"] == 0 and st["slotsHoldingDestroyed"] == 0, f"{nre} errors, {st}")
            else: verdict("WORN, GUARD OFF: THE FLOOD REPRODUCED (reference)", "error" not in r and nre > 10, f"{nre} errors in 3 s")
            b(f"set {W}.Enabled True"); time.sleep(1)   # cleans the hide list for the next round
            b("errors reset")
            spawn(TANK, 2.5)
            r = b("guardlab fluid"); print(f"  {tag} fluid:", r, flush=True)
            time.sleep(2)
            ex = kinds(b, "IndexOutOfRange")
            if on: verdict("FLUID, GUARD ON: NOTHING THROWN, BOTH DROPPED", "error" not in r and not r["loadException"] and not r["readException"]
                           and r["droppedOnLoad"] == 1 and r["droppedOnRead"] == 1 and r["unknownLeft"] == 0 and ex == 0, f"{r}, {ex} IndexOutOfRange logged")
            else: verdict("FLUID, GUARD OFF: THE GAME THROWS (reference)", "error" not in r and r["readException"] == "IndexOutOfRangeException", str(r))
        er = b("errors"); print("  errors at the end:", er["errors"], er["exceptions"], er["kinds"][:4], flush=True)
    finally:
        b(f"set {W}.Enabled True"); b(f"set {F}.Enabled True")
        b(f"set kaposztaleves.s.settings.god {god}")
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
fails = [n for n, ok in VERDICTS if not ok]
print(f"GUARDS: {len(VERDICTS) - len(fails)}/{len(VERDICTS)} PASS")
