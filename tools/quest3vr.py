#!/usr/bin/env python3
"""Automated Quest 3 / WiVRn checks for the OpenXR branch of issue #41. Never changes device settings.

  tools/quest3vr.py probe
  tools/quest3vr.py status [--port 27050]
  tools/quest3vr.py run --seconds 600 [--port 27050] [--out bench/quest3-openxr.json]
  tools/quest3vr.py screenshot --out bench/quest3.png

`probe` reports ADB, the attached headset and the local WiVRn/OpenXR setup. `status` reads the XR session's
state from a game that is ALREADY running: the loader's state, the session's state, the tracking origin, the
controllers and the recenter count. It sends only the pure read `openxr`, so it never starts or stops a session
and never needs developer access. `run` requires the game already running
with RevaMP's debug bridge on; it starts OpenXR, records display/input/controller/subssystem state, samples rendering
and multiplayer health, recenters, captures a screen, and restores the previous file-system condition. It does not
launch or control the headset's system settings. Physical comfort and in-headset visual quality remain a short human
check; these runs make the setup, health, recovery and logs repeatable without user input.
"""
import argparse
import datetime
import json
import os
import pathlib
import re
import shutil
import socket
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent


def run(args, timeout=30):
    try:
        result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
        return {"ok": result.returncode == 0, "stdout": result.stdout.strip(), "stderr": result.stderr.strip()}
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"ok": False, "stdout": "", "stderr": type(error).__name__ + ": " + str(error)}


def adb_device():
    if not shutil.which("adb"):
        return None
    listing = run(["adb", "devices", "-l"])
    if not listing["ok"]:
        return None
    for line in listing["stdout"].splitlines()[1:]:
        fields = line.split()
        if len(fields) >= 2 and fields[1] == "device":
            props = {part.split(":", 1)[0]: part.split(":", 1)[1] for part in fields[2:] if ":" in part}
            model = run(["adb", "-s", fields[0], "shell", "getprop", "ro.product.model"])
            return {"serial": fields[0], "model": model["stdout"] if model["ok"] else props.get("model", "")}
    return None


def openxr_runtimes():
    """The OpenXR runtime manifests on this machine, and which one is ACTIVE.

    The Unity OpenXR loader finds its runtime the way every OpenXR client does:
      1. $XR_RUNTIME_JSON, if it is set;
      2. the ACTIVE runtime file: $XDG_DATA_HOME/openxr/1/active_runtime (this is where the WiVRn
         server points it when a headset connects) or $XDG_CONFIG_HOME/openxr/1/active_runtime.json;
      3. otherwise the manifests themselves, which is only a list of what is INSTALLED.

    Knowing which one is active is the difference between "the runtime is installed" and "the game will
    find it", so the probe reports both.
    """
    home = pathlib.Path.home()
    config_home = pathlib.Path(os.environ.get("XDG_CONFIG_HOME") or home / ".config")
    data_home = pathlib.Path(os.environ.get("XDG_DATA_HOME") or home / ".local" / "share")

    manifests = []
    for base in (config_home / "openxr" / "1", pathlib.Path("/etc/xdg/openxr/1"),
                 pathlib.Path("/usr/share/openxr/1")):
        if not base.is_dir():
            continue
        for entry in sorted(base.iterdir()):
            if entry.suffix != ".json":
                continue
            try:
                data = json.loads(entry.read_text(encoding="utf-8"))
            except Exception:
                continue
            manifests.append({"file": str(entry), "name": data.get("runtime", {}).get("name", ""),
                              "library": data.get("runtime", {}).get("library_path", "")})

    # the active runtime: the file the loader follows
    active = None
    explicit = os.environ.get("XR_RUNTIME_JSON")
    if explicit:
        active = {"source": "XR_RUNTIME_JSON", "path": explicit,
                  "exists": pathlib.Path(explicit).is_file()}
    else:
        for candidate, source in (
                (data_home / "openxr" / "1" / "active_runtime", "XDG_DATA_HOME/openxr/1/active_runtime"),
                (config_home / "openxr" / "1" / "active_runtime.json", "XDG_CONFIG_HOME/openxr/1/active_runtime.json"),
                (pathlib.Path("/usr/share/openxr/1/active_runtime.json"), "/usr/share/openxr/1/active_runtime.json")):
            if candidate.exists():
                target = candidate
                try:
                    text = candidate.read_text(encoding="utf-8").strip()
                    if text.startswith("{"):
                        data = json.loads(text)
                        active = {"source": source, "path": str(candidate), "name": data.get("runtime", {}).get("name", "")}
                    else:
                        target = pathlib.Path(text)
                        active = {"source": source, "path": str(candidate), "target": str(target),
                                  "exists": target.is_file()}
                except Exception:
                    active = {"source": source, "path": str(candidate), "exists": True}
                break
    return manifests, active


# The WiVRn client ships under exactly two package ids: the Play Store build and the GitHub build.
# Nothing else is WiVRn, and a name that merely CONTAINS one of them is a different app.
WIVRN_PACKAGE_IDS = ("org.meumeu.wivrn", "org.meumeu.wivrn.github")


# Android names an installed package's directory `<id>-<build>`, where the build part is an
# opaque HASH: the base64 of the signing hash (`-XyZ987==`) or the hex of it. A hash always
# carries digits, and a NAME never does: an arbitrary alphabetic extension
# (`org.meumeu.wivrn-extra`) is a different package, not a build. A second `-` in the token is
# the same signal, because the id itself carries dots, not dashes.
_BUILD_TOKEN = re.compile(r"^[A-Za-z0-9+/]{3,}={1,2}$|^[0-9a-f]{4,}$")


def _is_build_token(suffix):
    """Is this the opaque build hash of an Android package directory?"""
    if not suffix or "-" in suffix:
        return False
    if not any(char.isdigit() for char in suffix):
        # a build hash always carries digits; a word does not
        return False
    return bool(_BUILD_TOKEN.match(suffix))


def wivrn_path_matches(path_line, package_id):
    """Does this `pm path` answer really belong to the package we asked for?

    `pm path <id>` answers with a line per APK, like
    `package:/data/app/~~hash==/<id>-<build>==/base.apk`. The answer is only about the package we
    named when it is attributable to THAT id, and the id appears as a DIRECTORY of the path:

      * a directory component that IS the id exactly, and
      * a directory component that is the id plus an opaque build token (`<id>-<build>`), where
        the build token is the base64/hex hash Android appends, never an arbitrary name.

    The APK file itself (`base.apk`, `split_<config>.apk`) is accepted because it sits UNDER that
    canonical directory. A different package's directory never matches: `org.meumeu.wivrn.github`
    is not `org.meumeu.wivrn` with extra characters, it is another app, so a bare prefix is
    rejected here as well.
    """
    value = (path_line or "").strip()
    if not value.startswith("package:"):
        return False
    apk = value[len("package:"):].strip()
    if not apk:
        return False
    parts = [part for part in apk.split("/") if part]
    if not parts:
        return False
    # the trailing element is the APK file name; the package directory is above it
    directories = parts[:-1] if parts[-1].endswith(".apk") else parts
    if not directories:
        return False
    for component in directories:
        if component == package_id:
            return True
        if component.startswith(package_id + "-"):
            if _is_build_token(component[len(package_id) + 1:]):
                return True
    return False


def probe():
    device = adb_device()
    openxr, active = openxr_runtimes()
    wivrn = None
    wivrn_id = None
    wivrn_state = "absent"
    if device:
        # the Quest store build and the GitHub build have different package names
        for name in WIVRN_PACKAGE_IDS:
            pkg = run(["adb", "-s", device["serial"], "shell", "pm", "path", name])
            if not pkg["ok"]:
                # the query itself failed: the state is UNKNOWN, never an absence and never an install
                wivrn_state = "unknown"
                continue
            for line in pkg["stdout"].splitlines():
                line = line.strip()
                if not line:
                    continue
                if wivrn_path_matches(line, name):
                    wivrn = line
                    wivrn_id = name
                    wivrn_state = "installed"
                    break
            if wivrn_id:
                break
    # the reverse tunnel the headset's WiVRn client connects through
    reverse = None
    if device:
        rev = run(["adb", "-s", device["serial"], "reverse", "--list"])
        if rev["ok"] and rev["stdout"]:
            reverse = [line.strip() for line in rev["stdout"].splitlines() if line.strip()]

    return {"adb": device, "wivrnPackage": wivrn, "wivrnPackageId": wivrn_id,
            "wivrnPackageState": wivrn_state, "openxrRuntimes": openxr,
            "openxrActiveRuntime": active, "adbReverse": reverse,
            "env": {k: os.environ.get(k) for k in ("XR_RUNTIME_JSON", "XRT_JSON", "WIVRN_ADB")}}


def controller_summary(state):
    """The controllers the bridge reports: how many, and which hand each one is on.

    The bridge reports a controller as `{"name":..., "valid":..., "left":..., "right":...}`. A controller
    that is not `valid` is one the runtime knows about but cannot use: it is the shape a "the headset is
    on but the controllers did not bind" failure takes.
    """
    controllers = state.get("controllers") or []
    valid = [c for c in controllers if c.get("valid")]
    return {
        "count": len(controllers),
        "valid": len(valid),
        "invalid": len(controllers) - len(valid),
        "names": sorted({c.get("name", "") for c in controllers if c.get("name")}),
        "left": sorted({c.get("name", "") for c in valid if c.get("left")}),
        "right": sorted({c.get("name", "") for c in valid if c.get("right")}),
        "detail": controllers,
    }


def status(args):
    """The XR session's state, read from a running game. It never starts or stops anything.

    This is the READ-ONLY view the hardware check needs: the loader's state, the session's state, the
    tracking origin, the controllers and the recenter count, plus the ADB and WiVRn setup. It sends only
    `openxr` (the pure read), so it cannot change the session and cannot need developer access.
    """
    setup = probe()
    out = {"setup": setup, "bridge": None, "error": None}
    try:
        state = bridge(args.port, "openxr")
    except Exception as error:
        out["error"] = "the debug bridge did not answer: " + str(error)
        print(json.dumps(out, indent=1))
        return 1
    out["bridge"] = {
        "state": state.get("state"),
        "running": state.get("running"),
        "failed": state.get("failed"),
        "failure": state.get("failure"),
        "error": state.get("error"),
        "enabled": state.get("enabled"),
        "loader": state.get("loader"),
        "loaderName": state.get("loaderName"),
        "loaderUsable": state.get("loaderUsable"),
        "managerActive": state.get("managerActive"),
        "managerInitialized": state.get("managerInitialized"),
        "featuresConfigured": state.get("featuresConfigured"),
        "runtime": state.get("runtime"),
        "api": state.get("api"),
        # The tracking origin: the MODE the runtime is set up around (device, floor, a tracking
        # reference) and the modes it supports. It is a configuration flag, never a pose: the bridge
        # reports no coordinate, so nothing about where the player is leaves the machine.
        "originMode": state.get("originMode"),
        "originSupported": state.get("originSupported"),
        "display": state.get("display"),
        "input": state.get("input"),
        "recenter": state.get("recenter"),
        "starts": state.get("starts"),
        "stops": state.get("stops"),
        "failures": state.get("failures"),
        "retries": state.get("retries"),
        "controllers": controller_summary(state),
    }
    print(json.dumps(out, indent=1))
    return 0


def bridge(port, text):
    with socket.create_connection(("127.0.0.1", port), timeout=35) as sock:
        stream = sock.makefile("rwb")
        stream.write((text + "\n").encode("utf-8")); stream.flush()
        line = stream.readline(4 * 1024 * 1024)
    if not line:
        raise RuntimeError("bridge disconnected")
    reply = json.loads(line)
    if not isinstance(reply, dict):
        raise RuntimeError("bridge refused " + text.split()[0] + ": " + str(reply))
    # A refusal is an "error" that is not one of the state fields. The XR state carries its own "error" (the reason
    # of a failed start) and its own "failed" flag, so a failed start is a readable answer and not an exception:
    # the tool has to be able to retry it.
    if reply.get("error") and "state" not in reply and not reply.get("failed"):
        raise RuntimeError("bridge refused " + text.split()[0] + ": " + str(reply.get("error")))
    return reply


def screenshot(device, out):
    """The headset's screen, as a PNG.

    It returns None when the screen cannot be captured, instead of raising: a Quest 3 that is ASLEEP
    (or a screen that is off) answers `screencap` with an EMPTY file, and a missing screenshot is not a
    failed XR session. Raising here used to abort a healthy run and to fail the offline test suite as
    soon as a real headset was attached.
    """
    if not device:
        return None
    out.parent.mkdir(parents=True, exist_ok=True)
    try:
        payload = subprocess.run(["adb", "-s", device["serial"], "exec-out", "screencap", "-p"],
                                 capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if payload.returncode != 0 or not payload.stdout.startswith(b"\x89PNG"):
        return None    # the screen is asleep or off: there is nothing to capture
    out.write_bytes(payload.stdout)
    return out


def run_session(args):
    setup = probe()
    states, frames, errors = [], [], []
    original = bridge(args.port, "openxr")
    started = bridge(args.port, "openxr start")
    # the bridge reports "running" for a live session and "failed" with a reason for one that did not start.
    # A failed start is retried only when it is asked for: the tool asks, so a headset plugged in late still works.
    if not started.get("running") and started.get("failed"):
        started = bridge(args.port, "openxr retry")
    if not started.get("running"):
        raise RuntimeError("OpenXR did not start: " + str(started.get("error") or started.get("failure") or "unknown"))
    shot = None
    # The XR session must be stopped again whatever happens: the module docstring promises the run
    # restores the previous state, and an exception in the sampling loop (or in the screenshot) used
    # to leave the game's XR session started.
    try:
        end = time.monotonic() + args.seconds
        next_recenter = time.monotonic() + max(10, args.seconds // 3)
        while time.monotonic() < end:
            try:
                state = bridge(args.port, "openxr")
                stats = bridge(args.port, "stats")
                mp = bridge(args.port, "mp status")
                err = bridge(args.port, "errors")
                states.append({"t": datetime.datetime.now(datetime.timezone.utc).isoformat(), "openxr": state, "mp": mp})
                # The real key names of Telemetry.FrameStatsJson(): frames, fps, p50ms, p99ms, p999ms, maxms,
                # hitches50ms, gc0, managedMB, bridgeMsTotal, bridgeMaxMs, time. The old names ("p50", "p95",
                # "p99", "hitches") do not exist, so every sample was {"fps": <n>} and the latency and hitch data
                # the tool exists to capture was never recorded.
                frames.append({k: stats.get(k) for k in ("fps", "p50ms", "p99ms", "p999ms", "maxms", "hitches50ms",
                                                         "gc0", "managedMB", "bridgeMsTotal", "bridgeMaxMs") if k in stats})
                errors.append({"errors": err.get("errors"), "exceptions": err.get("exceptions")})
            except Exception as error:
                raise RuntimeError("health probe failed: " + str(error))
            if time.monotonic() >= next_recenter:
                bridge(args.port, "openxr recenter"); next_recenter += max(10, args.seconds // 3)
            time.sleep(args.sample)
        shot = None
        if setup["adb"]:
            shot = screenshot(setup["adb"], pathlib.Path(args.out).with_suffix(".png"))
    finally:
        try:
            bridge(args.port, "openxr stop")
        except Exception:
            pass
    bad = [e for e in errors if (e["errors"] or 0) > (errors[0]["errors"] or 0) or (e["exceptions"] or 0) > (errors[0]["exceptions"] or 0)] if errors else []
    started_ok = any(s["openxr"].get("running") for s in states)
    controls = states[-1]["openxr"].get("controllers", []) if states else []
    result = {"setup": setup, "original": original, "started": started, "startedOk": started_ok,
              "controllers": controls, "samples": states, "frames": frames, "newErrors": bad, "screenshot": str(shot) if shot else None}
    out = pathlib.Path(args.out); out.parent.mkdir(parents=True, exist_ok=True); out.write_text(json.dumps(result, indent=1), encoding="utf-8")
    print(json.dumps({"ok": started_ok and not bad, "started": started_ok, "controllers": len(controls), "newErrors": len(bad), "out": str(out), "screenshot": str(shot) if shot else None}))
    return 0 if started_ok and not bad else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("probe")
    statusp = sub.add_parser("status", help="read the XR session's state from a running game (never mutates)")
    statusp.add_argument("--port", type=int, default=27050)
    shot = sub.add_parser("screenshot"); shot.add_argument("--out", default=str(ROOT / "bench/quest3.png"))
    runp = sub.add_parser("run")
    runp.add_argument("--seconds", type=int, default=60)
    runp.add_argument("--sample", type=float, default=5)
    runp.add_argument("--port", type=int, default=27050)
    runp.add_argument("--out", default=str(ROOT / "bench/quest3-openxr.json"))
    args = parser.parse_args()
    if args.command == "probe":
        print(json.dumps(probe(), indent=1)); return 0
    if args.command == "status":
        return status(args)
    device = adb_device()
    if args.command == "screenshot":
        out = screenshot(device, pathlib.Path(args.out)); print(out); return 0
    if args.seconds <= 0 or args.sample <= 0: parser.error("seconds and sample must be positive")
    return run_session(args)


if __name__ == "__main__":
    raise SystemExit(main())
