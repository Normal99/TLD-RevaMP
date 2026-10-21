#!/usr/bin/env python3
"""Native VR checks on a real headset (docs/VR-NATIVE.md), without anybody wearing it.

The game runs in its own test copy (tools/installs) and its own Proton prefix, hidden and admitted like every other
test launch (tools/testlaunch.py + tools/testdisplay.py + tools/tld-admit). The headset's proximity sensor is switched
off over ADB so it renders on the desk, and the hands are driven through the debug bridge (`vrnative sim ...`), so the
controllers may stay asleep.

    tools/vrnative_live.py up     [--copy NAME] [--port N] [--seed N]   headset ready, deploy, launch, new world, VR on
    tools/vrnative_live.py cmd    "<bridge command>"                     one bridge call, its JSON answer
    tools/vrnative_live.py check  [--only grab,pull,...]                 the hands and comfort assertions
    tools/vrnative_live.py perf   [--seconds S]                          frame times in the headset
    tools/vrnative_live.py shot   OUT.png                                what the headset shows (ADB screenshot)
    tools/vrnative_live.py down                                          VR off, quit, the headset's sensor back on

Only one VR run at a time on the machine, and nothing else running the game next to it: the admission broker and the
launch guard refuse a second game.
"""
import argparse
import json
import math
import os
import pathlib
import subprocess
import sys
import time

TOOLS = pathlib.Path(__file__).resolve().parent
ROOT = TOOLS.parent


def setup_env(copy, port):
    base = pathlib.Path(os.environ.get("TLD_INSTALLS", str(pathlib.Path.home() / "tld-installs"))) / copy
    os.environ["TLD_DIR"] = str(base / "game")
    os.environ["TLD_TEST_COMPAT"] = str(base / "compat")
    os.environ["TLD_PREFIX"] = str(base / "compat" / "pfx")
    os.environ["TLD_BRIDGE_PORT"] = str(port)
    os.environ.setdefault("TLD_ADMIT_BIN", str(TOOLS / "tld-admit"))
    os.environ.setdefault("TLD_REMOTE", "")
    os.environ.setdefault("TLD_TEST_MODS", "")
    os.environ.setdefault("TLD_HEADLESS", "0")   # VR needs the real window and display (headless gamescope has neither)
    sys.path.insert(0, str(TOOLS))


# ---- the headset (ADB)

def adb(*args, timeout=20):
    p = subprocess.run(["adb"] + list(args), capture_output=True, text=True, timeout=timeout)
    return p.stdout.strip()


def serial():
    for line in adb("devices").splitlines()[1:]:
        f = line.split()
        if len(f) >= 2 and f[1] == "device":
            return f[0]
    return None


def headset_ready():
    s = serial()
    if not s:
        raise SystemExit("no headset on ADB")
    state = adb("-s", s, "shell", "dumpsys vrpowermanager")
    if "State: HEADSET_MOUNTED" in state and "Virtual proximity state: DISABLED" in state:
        raise SystemExit("somebody is wearing the headset: not starting a test on it")
    adb("-s", s, "reverse", "tcp:9757", "tcp:9757")
    adb("-s", s, "shell", "am broadcast -a com.oculus.vrpowermanager.prox_close")
    adb("-s", s, "shell", "input keyevent KEYCODE_WAKEUP")
    activity = adb("-s", s, "shell", "cmd package resolve-activity --brief org.meumeu.wivrn.github").splitlines()
    if activity and "/" in activity[-1]:
        adb("-s", s, "shell", "am start -n " + activity[-1])
    print("headset: sensor off, awake, WiVRn client started", flush=True)
    return s


def headset_restore():
    s = serial()
    if s:
        adb("-s", s, "shell", "am broadcast -a com.oculus.vrpowermanager.automation_disable")
        print("headset: proximity sensor back to normal", flush=True)


# ---- the game

def bridge():
    import bench
    return bench.Bridge()


def call(b, cmd):
    b.f.write(cmd + "\n")
    b.f.flush()
    line = b.f.readline()
    if not line:
        raise ConnectionError(cmd + ": the bridge closed the connection")
    return json.loads(line)


def up(args):
    import game
    if game.running():
        raise SystemExit("a game is already running in this copy")
    headset_ready()
    game.require_test_identity()
    game.deploy_local()
    game.launch()
    game.newgame(args.seed)
    b = bridge()
    call(b, "set TLDRevamp.TestSafety.BlockAutoSave True")
    reply = call(b, "openxr start")
    if not reply.get("running"):
        reply = call(b, "openxr retry")
    print(json.dumps({"openxr": {k: reply.get(k) for k in ("state", "running", "display", "input", "runtime", "error")}}), flush=True)
    time.sleep(3)
    print(json.dumps(call(b, "vrnative")), flush=True)


def down(args):
    import game
    try:
        if game.running():
            b = bridge()
            try:
                call(b, "vrnative sim off")
                call(b, "openxr stop")
            except Exception as e:
                print("stopping VR:", e)
            game.quit_game()
    finally:
        headset_restore()


def cmd(args):
    print(json.dumps(call(bridge(), args.command)), flush=True)


def shot(args):
    s = serial()
    data = subprocess.run(["adb", "-s", s, "exec-out", "screencap", "-p"], capture_output=True, timeout=30).stdout
    pathlib.Path(args.out).write_bytes(data)
    print(args.out, len(data), "bytes")


# ---- the assertions

class Gate:
    def __init__(self):
        self.n = 0
        self.failed = 0

    def check(self, name, ok, detail=""):
        self.n += 1
        if not ok:
            self.failed += 1
        print(("PASS " if ok else "FAIL ") + name + (("  [" + str(detail) + "]") if detail != "" else ""), flush=True)
        return ok


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def fmt(v):
    return " ".join("%.3f" % x for x in v)


def wait(b, test, seconds=3.0, step=0.1):
    t0 = time.monotonic()
    state = call(b, "vrnative")
    while not test(state) and time.monotonic() - t0 < seconds:
        time.sleep(step)
        state = call(b, "vrnative")
    return state


def check(args):
    b = bridge()
    g = Gate()
    only = set(args.only.split(",")) if args.only else None
    want = lambda name: only is None or name in only

    st = call(b, "vrnative")
    g.check("native VR is active in the headset", st.get("active") and st.get("headset"), {k: st.get(k) for k in ("active", "headset", "gameVr")})

    if want("comfort"):
        call(b, "set settingsscript.s.S.BWalkHeadBob True")
        call(b, "set settingsscript.s.S.BDriveHeadBob True")
        c0 = call(b, "vrnative")["comfortApplied"]
        time.sleep(1.0)
        st = call(b, "vrnative")
        g.check("head bob is off for the game's frames in the headset", st["comfortApplied"] > c0, st["comfortApplied"] - c0)
        g.check("the player's head bob settings stay saved as they were", st.get("walkHeadBobSetting") and st.get("driveHeadBobSetting"))
        g.check("no depth of field in the headset", st.get("depthOfField") is False, st.get("depthOfField"))
        g.check("no motion blur in the headset", st.get("motionBlur") is False and st.get("motionBlurP") in (False, None), (st.get("motionBlur"), st.get("motionBlurP")))

    items = call(b, "vrnative items 8")
    head = items.get("head")
    takeable = [i for i in items.get("items", []) if i["take"] and (i["mass"] or 0) < 15]
    g.check("there are items to take around the player", len(takeable) >= 2, [i["name"] for i in takeable][:6])
    if len(takeable) < 1:
        return finish(g)
    takeable.sort(key=lambda i: dist(i["center"], head))
    near = takeable[0]
    call(b, "vrnative sim on")

    if want("grab"):
        c = near["center"]
        call(b, "vrnative sim right at %s dir 0 -1 0" % fmt(c))
        call(b, "vrnative sim right grip down")
        st = wait(b, lambda s: s.get("holder") == "right")
        g.check("the right grip grabs the item it touches", st.get("holder") == "right" and st.get("gamePickedUp") == near["name"],
                (st.get("holder"), st.get("gamePickedUp"), st.get("lastGrab")))
        # move the hand: the item follows it
        target = [head[0] + 0.3, head[1] - 0.3, head[2] + 0.1]
        call(b, "vrnative sim right to %s in 0.3" % fmt(target))
        time.sleep(1.2)
        st = call(b, "vrnative")
        palm = st["right"].get("palm")
        held = st.get("heldPos")
        hold = st.get("holdPoint")
        g.check("the hold point is the hand", hold is not None and palm is not None and dist(hold, palm) < 0.6, (hold, palm))
        g.check("the held item follows the hand", held is not None and dist(held, hold) < 0.35, (held, hold))
        # a slow let-go is a drop
        call(b, "vrnative sim right grip up")
        st = wait(b, lambda s: s.get("holder") is None)
        g.check("letting go drops it", st.get("holder") is None and st.get("gamePickedUp") is None and st["counts"]["drops"] >= 1, st["counts"])

    if want("throw"):
        st = call(b, "vrnative")
        c = [i for i in call(b, "vrnative items 8")["items"] if i["name"] == near["name"]]
        if c:
            call(b, "vrnative sim right at %s dir 0 -1 0" % fmt(c[0]["center"]))
            call(b, "vrnative sim right grip down")
            st = wait(b, lambda s: s.get("holder") == "right")
            start = [head[0], head[1] - 0.2, head[2]]
            call(b, "vrnative sim right to %s in 0.3" % fmt(start))
            time.sleep(1.0)
            fwd = items.get("look") or [0, 0, 1]
            end = [start[0] + fwd[0] * 0.9, start[1] + 0.4, start[2] + fwd[2] * 0.9]
            call(b, "vrnative sim right to %s in 0.12" % fmt(end))
            time.sleep(0.09)
            st = call(b, "vrnative")
            speed = st["right"].get("speed")
            call(b, "vrnative sim right grip up")
            st = wait(b, lambda s: s.get("holder") is None)
            g.check("a swing throws the item", st["counts"]["throws"] >= 1 and st.get("lastThrowSpeed", 0) > 2, (speed, st.get("lastThrowSpeed"), st["counts"]))

    if want("pull") and len(takeable) >= 2:
        far = takeable[-1]
        palm_from = [head[0], head[1] - 0.3, head[2]]
        to = far["center"]
        d = [to[i] - palm_from[i] for i in range(3)]
        call(b, "vrnative sim left at %s dir %s" % (fmt(palm_from), fmt(d)))
        call(b, "vrnative sim left trigger down")   # makes the left hand the pointer
        call(b, "vrnative sim left trigger up")
        time.sleep(0.2)
        call(b, "vrnative sim left grip down")
        st = wait(b, lambda s: s.get("holder") == "left")
        reach = dist(palm_from, to)
        g.check("the left grip pulls in what its ray points at (%.1f m)" % reach, st.get("holder") == "left" or reach > 2.5,
                (st.get("holder"), st.get("gamePickedUp"), far["name"], st.get("lastGrab")))
        if st.get("holder") == "left":
            time.sleep(1.5)
            st = call(b, "vrnative")
            g.check("a pulled item comes to the left hand", dist(st["heldPos"], st["holdPoint"]) < 0.4, (st.get("heldPos"), st.get("holdPoint")))
            # hand-over: the right hand takes it
            call(b, "vrnative sim right at %s dir 0 -1 0" % fmt(st["heldPos"]))
            call(b, "vrnative sim right grip down")
            st = wait(b, lambda s: s.get("holder") == "right")
            g.check("the right hand takes it over from the left", st.get("holder") == "right", (st.get("holder"), st["counts"]))
            call(b, "vrnative sim left grip up")
            time.sleep(0.2)
            st = call(b, "vrnative")
            g.check("letting go with the left hand keeps it in the right", st.get("holder") == "right", st.get("holder"))
            call(b, "vrnative sim right grip up")
            wait(b, lambda s: s.get("holder") is None)

    if want("ray"):
        # the interaction ray comes from the pointing hand: the game's look point lands where the hand points
        p = [head[0] + 0.25, head[1] - 0.4, head[2]]
        call(b, "vrnative sim right at %s dir 0 -1 0.2" % fmt(p))
        call(b, "vrnative sim right trigger down")
        time.sleep(0.3)
        st = call(b, "vrnative")
        look = call(b, "get mainscript.s.player.lookPoint").get("value")
        call(b, "vrnative sim right trigger up")
        ray = st.get("ray") or {}
        g.check("the game's interaction ray starts on the hand", ray.get("live") and st["counts"]["rayCalls"] > 0, (ray, st["counts"]["rayCalls"]))
        ok = False
        if isinstance(look, dict):
            look = [look.get("x"), look.get("y"), look.get("z")]
        if isinstance(look, list) and ray.get("from"):
            ok = look[1] < ray["from"][1] - 0.3   # it points at the floor below the hand, the head looks ahead
        g.check("the game's look point is where the hand points", ok, (look, ray.get("from")))

    call(b, "vrnative sim off")
    return finish(g)


def finish(g):
    print("\nnative VR: %d of %d assertions held" % (g.n - g.failed, g.n), flush=True)
    return 0 if g.failed == 0 else 1


def perf(args):
    """The headset frame rate for a few seconds, with the test profile's FPS cap lifted for this run only (the
    headset's own refresh paces the frames in VR). `--set` applies vrnative settings first (e.g. "scale 0.7")."""
    b = bridge()
    for s in args.set:
        call(b, "vrnative " + s)
    keep = {k: call(b, "get " + k).get("value") for k in ("settingsscript.s.S.BEnableFpsLimit", "UnityEngine.Application.targetFrameRate")}
    try:
        call(b, "set settingsscript.s.S.BEnableFpsLimit False")
        call(b, "set UnityEngine.Application.targetFrameRate -1")
        call(b, "stats")
        samples = []
        t0 = time.time()
        while time.time() - t0 < args.seconds:
            time.sleep(1.0)
            samples.append(call(b, "vrnative").get("perf", {}))
        st = call(b, "stats")
    finally:
        call(b, "set settingsscript.s.S.BEnableFpsLimit " + str(keep["settingsscript.s.S.BEnableFpsLimit"]))
        call(b, "set UnityEngine.Application.targetFrameRate " + str(keep["UnityEngine.Application.targetFrameRate"]))
    fps = sorted(x.get("fps", 0) for x in samples if "fps" in x)
    gpu = sorted(x.get("appGpuMs", 0) for x in samples if "appGpuMs" in x)
    med = lambda v: v[len(v) // 2] if v else None
    last = samples[-1] if samples else {}
    print(json.dumps({"set": args.set, "fps_median": med(fps), "fps_min": fps[0] if fps else None,
                      "app_gpu_ms_median": med(gpu), "dropped": [samples[0].get("droppedFrames"), last.get("droppedFrames")] if samples else None,
                      "last": last, "stats": st}, indent=1), flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--copy", default=os.environ.get("TLD_VR_COPY", "VRNative"))
    ap.add_argument("--port", type=int, default=int(os.environ.get("TLD_VR_PORT", "27261")))
    sub = ap.add_subparsers(dest="what", required=True)
    p = sub.add_parser("up"); p.add_argument("--seed", type=int, default=20260924); p.set_defaults(fn=up)
    p = sub.add_parser("down"); p.set_defaults(fn=down)
    p = sub.add_parser("cmd"); p.add_argument("command"); p.set_defaults(fn=cmd)
    p = sub.add_parser("check"); p.add_argument("--only", default=""); p.set_defaults(fn=check)
    p = sub.add_parser("perf"); p.add_argument("--seconds", type=float, default=20); p.add_argument("--set", action="append", default=[]); p.set_defaults(fn=perf)
    p = sub.add_parser("shot"); p.add_argument("out"); p.set_defaults(fn=shot)
    args = ap.parse_args()
    setup_env(args.copy, args.port)
    return args.fn(args) or 0


if __name__ == "__main__":
    sys.exit(main())
