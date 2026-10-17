"""One machine: where a creature's eye is (bridge `mp aihead`) while it sees and chases the player. handoff 2026-10-08: on the
machine running it, the creature's head read 22-60 m from its root, so the sight ray toward another player started far from
the creature (remoteSeen 0). Fresh game, seeded world, god mode (restored)."""
import sys, time, math, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from roadkit import until, g, tp

if "--no-build" not in sys.argv: game.build()
game.quit_game(); game.launch()
with game.ProtectedSession():
    seeded.restart_same_seed()
    b = Bridge()
    god = None
    try:
        b("set TLDRevamp.TestSafety.BlockAutoSave True")
        god = g(b, "kaposztaleves.s.settings.god"); b("set kaposztaleves.s.settings.god True")
        ids = b("mp aiids")["ai"]; print("creatures:", ids, flush=True)
        rp = b("roadpoint"); print("road:", tp(b, rp["gx"], rp["gz"]), flush=True); time.sleep(2)
        print(b(f"devspawn {ids[0][0]}"), flush=True); time.sleep(3)
        for k in range(12):
            print(f"  t{k * 2}s", b("mp aihead"), flush=True)
            if k == 3:   # the player walks off 15 m: it chases
                me = b("gpos"); print("  player moves 15 m:", tp(b, me["gx"] + 15, me["gz"]), flush=True)
            time.sleep(2)
    finally:
        if god is not None: b(f"set kaposztaleves.s.settings.god {god}")
