#!/usr/bin/env python3
"""Standing benchmark in one of the player's saves (loaded fresh per pass, autosave blocked, no teleports).

  tools/savebench.py crowdedtest --vanilla-vs-mod     all mod fixes off vs on, order off,on,on,off
  tools/savebench.py crowdedtest --set Path=Value      one pass with runtime values
"""
import sys, time, json, pathlib, statistics as st
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game, seeded
from bench import Bridge
import drivebench

ALL = drivebench.ALL_FIXES + ["TLDRevamp.Fixes.RebaseParticles.Enabled", "TLDRevamp.Fixes.IdlePourPause.Enabled",
                              "TLDRevamp.Fixes.MirrorThrottle.Enabled", "TLDRevamp.Fixes.AiRaycastNoAlloc.Enabled", "TLDRevamp.Fixes.AlignNearestRoad.Enabled"]
WINDOW = 12


def one(save, sets):
    seeded.set_now(sets)          # before loading, so the world loads in this configuration
    b = game.loadsave(save)
    vs0 = b("get UnityEngine.QualitySettings.vSyncCount")["value"]
    b("set UnityEngine.QualitySettings.vSyncCount 0")
    try:
        time.sleep(3)
        b("reset"); b("loopprof start"); b("alloc reset"); time.sleep(WINDOW)
        s = b("stats"); ph = b("loopprof stop 200"); a = b("alloc")
    finally:
        b(f"set UnityEngine.QualitySettings.vSyncCount {vs0}")
    d = {q["p"]: q["avgMsAll"] for q in ph["phases"]}
    return {"fps": s["fps"], "p50ms": s["p50ms"], "p99ms": s["p99ms"], "maxms": s["maxms"], "gc": a["collections"],
            "allocKB": a["allocKBperFrame"], "update": d.get("Update.ScriptRunBehaviourUpdate"), "render": d.get("PostLateUpdate.FinishFrameRendering"),
            "physics": d.get("FixedUpdate.PhysicsFixedUpdate")}


def main():
    save = sys.argv[1]
    args = sys.argv[2:]
    with game.ProtectedSession():
        if not game.bridge_up():
            game.launch()
        if "--vanilla-vs-mod" in args:
            res = {"False": [], "True": []}
            try:
                for val in ("False", "True", "True", "False"):
                    r = one(save, [(p, val) for p in ALL])
                    res[val].append(r)
                    print(f"  {'mod    ' if val == 'True' else 'vanilla'} {r}", flush=True)
            finally:
                seeded.set_now([(p, "True") for p in ALL])
            print(f"\nVANILLA vs MOD, save '{save}', standing {WINDOW} s, 2 loads each")
            for k in ("fps", "p50ms", "p99ms", "maxms", "gc", "allocKB", "update", "render", "physics"):
                a_ = [x[k] for x in res["False"]]; b_ = [x[k] for x in res["True"]]
                print(f"  {k:8} vanilla {a_}  mod {b_}   mean {st.mean(a_):.2f} → {st.mean(b_):.2f} ({(st.mean(b_) / st.mean(a_) - 1) * 100:+.0f}%)")
            return
        sets = [tuple(a.split("=", 1)) for a in args[1::2]] if args and args[0] == "--set" else []
        print(one(save, sets))


if __name__ == "__main__":
    main()
