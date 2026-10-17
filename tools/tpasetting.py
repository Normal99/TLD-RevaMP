#!/usr/bin/env python3
"""The host's teleport-request setting is remembered (Settings → Revamp → Multiplayer, or the host's own card), this
machine alone:
  A  the card's button cycles it (Tpa.Cycle) → written to tldrevamp-settings.txt (tpa=<index>)
  B  the game restarted → the same setting (Tpa.Enabled / CooldownS) as before the restart
  The setting is put back as it was at the end.  tools/tpasetting.py [--no-build]"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game

VERDICTS = []
def verdict(name, ok, detail=""):
    VERDICTS.append((name, ok)); print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)
T = "TLDRevamp.Net.Tpa"
SETTINGS = game.tldenv.GAME_DIR / "BepInEx/config/tldrevamp-settings.txt"

def state(b):   # what the setting means: the wait only counts while it is on
    on = b(f"get {T}.Enabled")["value"]
    return (on, b(f"get {T}.CooldownS")["value"] if on in (True, "True", "true") else None)
def saved(): return next((l.split("=", 1)[1].strip() for l in SETTINGS.read_text().splitlines() if l.startswith("tpa=")), None)

if "--no-build" not in sys.argv: game.build()
if game.running(): game.quit_game()
game.launch()
b = game.Bridge()
before = state(b)
print("  before:", before, flush=True)
b(f"call {T}.Cycle")   # one step on: never the value it started with
want = state(b)
print("  after a click:", want, "| in the settings file: tpa =", saved(), flush=True)
game.quit_game(); game.launch(); b = game.Bridge()
got = state(b)
verdict("A: THE CARD'S BUTTON WRITES IT TO THE SETTINGS FILE", saved() is not None, f"tpa = {saved()}")
verdict("B: THE SETTING CHANGED AND SURVIVED A RESTART", want != before and got == want, f"before {before}, set {want}, after restart {got}")
for _ in range(len([-1, 0, 60, 300, 900])):   # back to where it was
    if state(b) == before: break
    b(f"call {T}.Cycle")
verdict("PUT BACK", state(b) == before, str(state(b)))
print(f"\n{sum(ok for _, ok in VERDICTS)}/{len(VERDICTS)} passed", flush=True)
