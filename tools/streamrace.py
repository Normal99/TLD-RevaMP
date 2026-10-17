#!/usr/bin/env python3
"""Bug 10 (2026-10-03, "the game would delete my car at times"): the game's own streaming race, single player.
itemPlaceRemoveScript.Upd runs RemoveStuff then PlaceStuff in one call; RemoveStuff stores and destroys a whole car
when any part of it is past itemRemoveDist, and PlaceStuff — the car's fresh record near the player, the car still
alive until the frame ends — deletes that record. Fixes.StreamRace puts the record back.
Per configuration (Fixes.StreamRace.Enabled off, then on), on a freshly started world: spawn a car next to the player,
`streamrace arm` (one part moved 400 m away, the game's Upd at once), then `streamrace check` a few seconds later.
  off: expect the car gone for good (no record, no object) — the vanilla bug
  on:  expect the record kept and the game placing the car again
  tools/streamrace.py [--no-build] [--car Car06Full]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge

CAR = sys.argv[sys.argv.index("--car") + 1] if "--car" in sys.argv else "Car06Full"
VERDICTS = []

def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)

if game.running(): game.quit_game()
if "--no-build" not in sys.argv: game.build()
else: game.deploy_local()
game.launch()
with game.ProtectedSession():
    for enabled in (False, True):
        game.newgame()
        b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        god = b("get kaposztaleves.s.settings.god").get("value"); b("set kaposztaleves.s.settings.god True")
        try:
            b(f"set TLDRevamp.Fixes.StreamRace.Enabled {enabled}")
            p = b("gpos"); b(f"tpground {p['gx'] + 15} {p['gz']}"); time.sleep(3)   # out of the start building
            print(f"  [{'on' if enabled else 'off'}] spawn:", b(f"devspawn {CAR}"), flush=True); time.sleep(8)
            a = b("streamrace arm"); print("  arm:", a, flush=True)
            if "error" in a: verdict(f"{'ON' if enabled else 'OFF'}: ARMED", False, a["error"]); continue
            time.sleep(4)   # the game's own Upd (once a second) places what has a record
            c = b(f"streamrace check {a['id']}"); print("  check:", c, flush=True)
            if enabled:
                verdict("ON: THE CAR SURVIVES (RECORD KEPT, PLACED AGAIN)", c["live"] and a["recordAfterUpd"],
                        f"record after Upd {a['recordAfterUpd']}, kept {a['recordsKept']}, live {c['live']}, members {c.get('members')} of {a['members']}")
            else:
                verdict("OFF: THE VANILLA RACE LOSES THE CAR", not c["live"] and not c["record"],
                        f"record after Upd {a['recordAfterUpd']}, live {c['live']}, record {c['record']}")
        finally:
            b("set TLDRevamp.Fixes.StreamRace.Enabled True")
            b(f"set kaposztaleves.s.settings.god {god if god is not None else False}")
game.quit_game()
print("\nSUMMARY"); [print(f"  {'PASS' if ok else 'FAIL'}  {n}") for n, ok in VERDICTS]
