#!/usr/bin/env python3
"""One machine, hosting alone: the official MP's rules that our sessions must follow.
  pause: the world keeps running while the pause menu is open (single player freezes it)
  death: the player respawns in the world (single player goes to the main menu — which would end the session)"""
import sys, time, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
from mplib import running_build, local_build_hash

if running_build() != local_build_hash(): game.quit_game(); game.launch()
ok = {}
with game.ProtectedSession():
    seeded.restart_same_seed(); b = Bridge()
    b("set TLDRevamp.TestSafety.BlockAutoSave True")
    print("host:", "server" in b("mp host 27070"))
    # pause
    b("call mainscript.s.PressedEscape False False"); time.sleep(1.5)
    ts, open_ = b("get UnityEngine.Time.timeScale")["value"], b("get mainscript.s.pauseMenuOpen")["value"]
    t0 = b("get napszakvaltakozas.s.t")["value"]; time.sleep(2); t1 = b("get napszakvaltakozas.s.t")["value"]
    print(f"pause: menu open {open_}, timeScale {ts}, world clock advanced {t1 - t0:.2f} s in 2 s")
    ok["pause"] = open_ is True and ts == 1 and t1 - t0 > 1.0
    b("call mainscript.s.PressedEscape False False"); time.sleep(1)
    # death
    b("set kaposztaleves.s.settings.god False")
    p0 = b("mp players")["me"]
    print("kill:", b("call mainscript.s.player.Death"))
    t = time.time(); respawned = False
    while time.time() - t < 40:
        try:
            st = b("mp status")
            if b("get mainscript.s.died")["value"] is False and "server" in st: respawned = True; break
        except Exception: pass
        time.sleep(1)
    in_world = b("get DataFromMenuScript.s.mainmenu")["value"] is False
    p1 = b("mp players")["me"]
    d = ((p1[0] - p0[0]) ** 2 + (p1[2] - p0[2]) ** 2) ** 0.5
    print(f"death: respawned {respawned}, still in the world {in_world}, still hosting {'server' in b('mp status')}, moved {d:.0f} m, "
          f"respawns counted {b('get TLDRevamp.Net.SessionRules.Respawns')['value']}")
    ok["death"] = respawned and in_world and "server" in b("mp status")
    b("mp stop"); b("set kaposztaleves.s.settings.god False")
print("SESSION-RULES VERDICT:", "PASS" if all(ok.values()) else "FAIL", ok)
