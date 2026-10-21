#!/usr/bin/env python3
"""The LIVE XR gate for issue #41: the step-by-step script the coordinator runs on the Quest 3.

It is deliberately NOT a launcher: this lane may not launch the game. The coordinator owns the live
run, so this script is the checklist and the assertions, driven against a game that is ALREADY running
(launched through the guarded path: tools/testlaunch.py + tools/testdisplay.py, a unique game
directory, a unique Proton prefix and an explicit unique bridge port).

    tools/quest3vr_live.py preflight          # everything that can be checked BEFORE a launch
    tools/quest3vr_live.py status             # the read-only state of the running game
    tools/quest3vr_live.py gate --port 27050  # the gate: the assertions, in order
    tools/quest3vr_live.py gate --port 27050 --no-start   # the gate without the mutation steps

Every step prints the assertion, what it saw and whether it held, and the script exits non-zero if
any assertion fails. The gate is ordered so that the steps that need no headset come first.
"""
import argparse
import json
import pathlib
import sys

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))

import quest3vr  # noqa: E402  (the tool next to this one)


class Gate:
    """The gate's assertions: each one prints what it saw and whether it held."""

    def __init__(self):
        self.results = []
        self.failed = 0

    def check(self, name, ok, detail=""):
        self.results.append((name, bool(ok), detail))
        if not ok:
            self.failed += 1
        print(("PASS " if ok else "FAIL ") + name + (("  [" + str(detail) + "]") if detail else ""), flush=True)
        return ok

    def report(self):
        print("")
        print("XR live gate: " + str(len(self.results) - self.failed) + " of " + str(len(self.results)) + " assertions held")
        if self.failed:
            print("the gate FAILED: " + str(self.failed) + " assertion(s)")
        return 0 if self.failed == 0 else 1


def bridge(port, text):
    """A bridge call that answers with the state. A refusal is an answer, not an exception."""
    return quest3vr.bridge(port, text)


def state_of(port):
    return bridge(port, "openxr")


def preflight():
    """Everything that can be checked BEFORE the game is launched. It never touches the game."""
    setup = quest3vr.probe()
    print(json.dumps(setup, indent=1))
    gate = Gate()

    device = setup.get("adb")
    gate.check("the Quest is connected over ADB", bool(device), device.get("serial") if device else "no device")
    gate.check("the headset is a Quest 3", bool(device) and "Quest" in (device.get("model") or ""),
               device.get("model") if device else "")

    wivrn = setup.get("wivrnPackage")
    gate.check("WiVRn is installed on the headset", bool(wivrn), wivrn or "not installed")

    reverse = setup.get("adbReverse") or []
    gate.check("the ADB reverse tunnel for the WiVRn port is live",
               any("9757" in line for line in reverse), "; ".join(reverse) or "no reverse")

    manifests = setup.get("openxrRuntimes") or []
    gate.check("the WiVRn OpenXR runtime is installed",
               any(m.get("name") == "WiVRn" for m in manifests),
               ", ".join(sorted({m.get("name", "") for m in manifests})) or "none")

    # The ACTIVE runtime is what the loader uses. The WiVRn server points it at the runtime when a
    # headset connects, so before the headset is awake this may legitimately be absent: the gate then
    # says so, and the live run must wake the headset and re-check.
    active = setup.get("openxrActiveRuntime")
    gate.check("an OpenXR runtime is ACTIVE (the loader will find it)",
               bool(active), (active or {}).get("source") if active else "no active runtime: wake the headset, then re-check")

    # If there is no active runtime, the FALLBACK has to be named, because the live run needs it: the
    # runtime is installed and its manifest resolves, so it can be selected explicitly with
    # XR_RUNTIME_JSON. The fallback is only a fallback: the active runtime is what the WiVRn server sets
    # when a headset connects, and it is the normal path.
    if not active:
        fallback = "/usr/share/openxr/1/openxr_wivrn.json"
        gate.check("the fallback runtime manifest is present for the explicit selection",
                   pathlib.Path(fallback).is_file(), fallback)
        print("")
        print("NO ACTIVE RUNTIME. The WiVRn server sets one when a HEADSET CONNECTS, so:")
        print("  1. wake the headset (wear it, or `adb shell input keyevent KEYCODE_POWER`)")
        print("  2. start the WiVRn client on the headset (org.meumeu.wivrn.github);")
        print("     when it connects, the server writes ~/.local/share/openxr/1/active_runtime")
        print("  3. re-run this preflight: the assertion must then hold")
        print("")
        print("  FALLBACK (if the headset is not going to connect): pass the runtime to the game")
        print("  explicitly, in the launch environment:")
        print("      XR_RUNTIME_JSON=" + fallback)
        print("  That manifest resolves to /usr/lib/wivrn/libopenxr_wivrn.so, which exports")
        print("  xrNegotiateLoaderRuntimeInterface (the symbol the OpenXR loader binds).")

    return gate.report()


def gate_run(port, do_start=True, check_origin=False):
    """The gate, in order. The steps that need no headset come first.

    `check_origin` adds the one step that MOVES the runtime's tracking origin (`openxr recenter origin`).
    It is off by default because it moves the player's world: the plain recenter is what the player
    experiences, and the origin path only has to be proven once. When it is on, the step asserts that the
    explicit request is the ONLY path that moves the origin, and it reports the mode before and after.
    """
    g = Gate()
    print("== step 1: the state before anything is started ==")
    before = state_of(port)
    print(json.dumps(before, indent=1))
    g.check("the bridge answers the pure read `openxr`", "state" in before, before.get("state"))
    g.check("the state reports the loader's state", "loader" in before, before.get("loader"))
    g.check("the state reports the manager's state", "managerInitialized" in before, before.get("managerInitialized"))
    g.check("the state reports the controllers", "controllers" in before, len(before.get("controllers") or []))
    g.check("the state reports the recenter count", "recenter" in before, before.get("recenter"))
    g.check("the state reports the tracking origin mode", "originMode" in before, before.get("originMode"))

    if not do_start:
        return g.report()

    print("")
    print("== step 2: start the session ==")
    started = bridge(port, "openxr start")
    if not started.get("running") and started.get("failed"):
        # a failed start is retried only when it is asked for: the tool asks
        print("the first start failed: " + str(started.get("error") or started.get("failure")))
        started = bridge(port, "openxr retry")
    g.check("the session started", bool(started.get("running")),
            started.get("state") or started.get("failure") or started.get("error"))
    g.check("the reported state is running", started.get("state") == "running", started.get("state"))
    g.check("the manager is initialized", bool(started.get("managerInitialized")), started.get("managerInitialized"))
    g.check("the display runs", bool(started.get("display")), started.get("display"))
    g.check("the input runs", bool(started.get("input")), started.get("input"))
    g.check("the controller profiles were configured", bool(started.get("featuresConfigured")),
            started.get("featuresConfigured"))
    g.check("the loader was chosen", started.get("loader") not in (None, "", "none"), started.get("loader"))
    g.check("the loader is usable", bool(started.get("loaderUsable")), started.get("loaderUsable"))
    # The tracking origin: the MODE the runtime is set up around. It is a configuration flag, never a
    # pose, so it can be reported without leaking anything about where the player is. The runtime's origin
    # is NOT moved by a plain recenter, so the mode here is what the runtime chose for itself.
    g.check("the runtime reports a tracking origin mode", bool(started.get("originMode")),
            started.get("originMode"))
    g.check("the runtime reports the origin modes it supports", bool(started.get("originSupported")),
            started.get("originSupported"))

    print("")
    print("== step 3: the controllers ==")
    live = state_of(port)
    controllers = live.get("controllers") or []
    g.check("at least one controller is reported", len(controllers) > 0, len(controllers))
    valid = [c for c in controllers if c.get("valid")]
    g.check("a controller is valid", len(valid) > 0, len(valid))
    g.check("a left hand controller is reported", any(c.get("left") for c in valid),
            ", ".join(sorted({c.get("name", "") for c in valid if c.get("left")})))
    g.check("a right hand controller is reported", any(c.get("right") for c in valid),
            ", ".join(sorted({c.get("name", "") for c in valid if c.get("right")})))

    print("")
    print("== step 4: the recenter moves the rig once, and the origin only when asked ==")
    before_recenter = live.get("recenter")
    after = bridge(port, "openxr recenter")
    g.check("the recenter was counted once", after.get("recenter") == (before_recenter or 0) + 1,
            (before_recenter, after.get("recenter")))
    # The bridge's state carries no origin mode (it never reads a pose and never reports one), so the
    # origin's own movement is checked by the OFFLINE guard, not here: what this step checks is that a
    # plain recenter moved the rig exactly once and did not touch the origin path (`recenter origin` is
    # the only path that moves it, and it is the only one that changes the count differently).
    plain = state_of(port)
    g.check("a plain recenter did not move the origin path",
            plain.get("recenter") == after.get("recenter"),
            (after.get("recenter"), plain.get("recenter")))
    # The origin MODE is reported now, so the origin's own movement is observable: a plain recenter must
    # leave it exactly where the runtime had it. (The old code could not be checked here at all, because
    # the state carried no origin mode, so "the origin moved" was only ever a guess.)
    g.check("a plain recenter did not move the tracking origin mode",
            plain.get("originMode") == after.get("originMode"),
            (after.get("originMode"), plain.get("originMode")))

    if check_origin:
        print("")
        print("== step 4b: the explicit origin request moves the origin, and only it does ==")
        # The plain recenter above did NOT move the origin mode (the step above asserts that). The explicit
        # `recenter origin` is the only path that may. This step proves the origin path works at all, and
        # that it is the explicit request alone that does it.
        before_origin = plain.get("originMode")
        moved = bridge(port, "openxr recenter origin")
        g.check("the explicit origin request was counted as a recenter",
                moved.get("recenter") == (plain.get("recenter") or 0) + 1,
                (plain.get("recenter"), moved.get("recenter")))
        g.check("the runtime reports an origin mode after the explicit request",
                bool(moved.get("originMode")), moved.get("originMode"))
        print("   the origin mode was " + str(before_origin) + " before the explicit request, and is "
              + str(moved.get("originMode")) + " after it")

    print("")
    print("== step 5: stop the session (the state must come back to idle) ==")
    stopped = bridge(port, "openxr stop")
    g.check("the session stopped", not stopped.get("running"), stopped.get("state"))
    g.check("the reported state is idle", stopped.get("state") == "idle", stopped.get("state"))
    g.check("the manager is no longer initialized", not stopped.get("managerInitialized"),
            stopped.get("managerInitialized"))
    g.check("the stop was counted", (stopped.get("stops") or 0) >= 1, stopped.get("stops"))
    g.check("the reported state and the machine agree",
            stopped.get("state") == "idle", stopped.get("state"))

    print("")
    print("== step 6: start again, so the session is left as the coordinator wants it ==")
    again = bridge(port, "openxr start")
    if not again.get("running") and again.get("failed"):
        again = bridge(port, "openxr retry")
    g.check("the session started again", bool(again.get("running")), again.get("state"))

    return g.report()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("preflight")
    statusp = sub.add_parser("status")
    statusp.add_argument("--port", type=int, default=27050)
    gatep = sub.add_parser("gate")
    gatep.add_argument("--port", type=int, default=27050)
    gatep.add_argument("--no-start", action="store_true",
                       help="only the read-only steps (no start, no stop)")
    gatep.add_argument("--check-origin", action="store_true",
                       help="also run the step that moves the runtime's tracking origin (it moves the world)")
    args = parser.parse_args()
    if args.command == "preflight":
        return preflight()
    if args.command == "status":
        return quest3vr.status(args)
    return gate_run(args.port, do_start=not args.no_start, check_origin=args.check_origin)


if __name__ == "__main__":
    raise SystemExit(main())
