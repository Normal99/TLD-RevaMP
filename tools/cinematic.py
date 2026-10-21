#!/usr/bin/env python3
"""Cinematic camera check for issue #36 (the local filmmaking camera).

  tools/cinematic.py status [--port 27050]     the camera's state (read-only, safe on any machine)
  tools/cinematic.py check [--port 27050]      the full check: modes, bounds, teardown, and the proof that
                                                the gameplay camera and the player did not move
  tools/cinematic.py shot <file> [--port 27050]  load a shot list and play it
  tools/cinematic.py screenshot [--port 27050]   a picture of the camera's view

`--port` may be given before or after the subcommand: `cinematic.py --port 27050 check` and
`cinematic.py check --port 27050` are the same command.

The check drives the mod's debug bridge, which is off unless this machine's config turns it on. It never touches
the game's cameras itself: everything happens through the mod's own commands, and the mod reports back what it
measured. It sends nothing over the network and changes no game setting.

What `check` proves:

  * the camera turns on in free mode and in orbit mode, and off again, and the state really changes;
  * the speed, the field of view and the orbit radius are clamped to their bounds;
  * the gameplay camera (position, rotation, field of view, enabled state) and the player's transform are
    unchanged before and after the camera was used - the camera never touches them. Positions and rotations are
    compared with a tolerance (0.05 m, 2 degrees) because the player's own character sways on the spot; the
    field of view and the enabled state are compared exactly. A value the bridge could not read is a failure,
    never a "nothing changed";
  * the camera is gone and the cursor is back the way it was after the camera was turned off.

Every step prints PASS or FAIL; the exit code is 1 if anything failed.
"""
import argparse
import json
import math
import pathlib
import re
import socket
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent

# ---- the camera's command surface ------------------------------------------------------
#
# The mod owns the camera's command surface (src/TLDRevamp/Cinematic/CinematicBridge.cs). This module mirrors it
# so the tool can classify a command *before* sending it and refuse the ones that are not the camera's own. The
# two are kept in step by tools/test_cinematic.py, which compares this list with the C# source.

# The commands that only read: the camera's status. Any peer may ask for these.
QUERY_COMMANDS = {"status"}

# The commands that change what this machine shows. They are refused when the host's developer policy does not
# allow them.
MUTATION_COMMANDS = {"on", "off", "free", "orbit", "toggle", "recentre", "speed", "fov", "radius", "shot",
                      "screenshot"}

# The verbs that take a number.
NUMERIC_COMMANDS = {"speed", "fov", "radius"}

# The shot list's own verbs.
SHOT_COMMANDS = {"load", "write", "unload"}

# The bounds the camera holds its values to. The mod clamps them too; this is the tool's own expectation, so a
# bridge that stopped clamping would be caught.
LIMITS = {
    "speed": (0.25, 60.0),
    "fov": (10.0, 120.0),
    "radius": (1.0, 400.0),
}

# How long the tool waits for the mod's screenshot to be written. The capture happens at the end of the frame,
# so the file appears a moment after the command was answered.
SCREENSHOT_WAIT_SECONDS = 2.0


def is_read_only(argument):
    """Whether a `cinema` command only reads. The same rule the mod's pure policy uses."""
    if argument is None:
        return True
    argument = argument.strip()
    if not argument:
        return True
    if argument == "status":
        return True
    if argument.startswith("status "):
        return True
    return False


def describe_reply(reply):
    """What the bridge answered, in words, for the error message.

    The bridge answers with a JSON object. A null, a list, a string or a number is a
    broken answer, and the report has to be able to say WHICH shape it was.
    """
    if reply is None:
        return "null"
    if isinstance(reply, bool):
        return "a boolean"
    if isinstance(reply, (int, float)):
        return "a number"
    if isinstance(reply, str):
        return "a string"
    if isinstance(reply, (list, tuple)):
        return "a list"
    if isinstance(reply, dict):
        return "an object"
    return "something else"


def as_reply(reply):
    """The bridge's answer as a dictionary.

    The bridge answers with a JSON object. Anything else - a null, a list, a string, a
    number - is a broken answer, not a value, so it becomes an error the check can
    name instead of an exception the check dies on.
    """
    if isinstance(reply, dict):
        return reply
    return {"error": "the bridge answered with " + describe_reply(reply) + ", not an object"}


def classify(argument):
    """(verb, is_read_only, is_valid, reason) for a `cinema` command's argument.

    The tool uses this to refuse a command before it is sent, so a typo in a script does not turn into a mutation
    on a machine that the script was only meant to read.
    """
    if argument is None:
        argument = ""
    argument = argument.strip()
    if not argument or is_read_only(argument):
        return ("status", True, True, "")
    parts = argument.split()
    verb = parts[0].lower()
    rest = parts[1:]
    if verb not in MUTATION_COMMANDS:
        return (verb, False, False, "unknown camera command")
    if verb in NUMERIC_COMMANDS:
        if len(rest) != 1:
            return (verb, False, False, f"cinema {verb} needs one number")
        try:
            value = float(rest[0])
        except ValueError:
            return (verb, False, False, f"cinema {verb} needs a number")
        if value != value or value in (float("inf"), float("-inf")):
            return (verb, False, False, f"cinema {verb} needs a finite number")
        low, high = LIMITS[verb]
        return (verb, False, True, "")
    if verb == "shot":
        if not rest:
            return (verb, False, True, "")
        what = rest[0].lower()
        if what not in SHOT_COMMANDS:
            return (verb, False, False, "cinema shot takes load, write or unload")
        if what == "load" and len(rest) < 2:
            return (verb, False, False, "cinema shot load needs a shot list")
        return (verb, False, True, "")
    return (verb, False, True, "")


def clamp(name, value):
    """The camera's own bounds, as the tool expects them."""
    low, high = LIMITS[name]
    return max(low, min(high, value))


class Bridge:
    """One bridge connection: a command in, one JSON line out."""

    def __init__(self, port=27050, timeout=35):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.file = self.sock.makefile("rw", encoding="utf-8", newline="\n")

    def __call__(self, command):
        self.file.write(command + "\n")
        self.file.flush()
        line = self.file.readline()
        if not line:
            raise ConnectionError("the bridge closed the connection")
        try:
            return json.loads(line)
        except ValueError:
            # A line that is not JSON at all is a broken answer, not a crash: it is
            # reported like any other unreadable field.
            return {"error": "the bridge answered with something that is not JSON"}

    def try_call(self, command):
        """The bridge answers a refused command with an error; keep it instead of raising.

        The answer is normalised to an object: a bridge that answers with a null, a
        list or a string is broken, and the check has to be able to say so by name
        instead of dying on the reply.
        """
        return as_reply(self(command))

    def cinema(self, argument=""):
        """Sends a `cinema` command, after checking it against the camera's own command surface.

        A malformed command is refused here rather than sent: the mod would refuse it too, but the point of the
        check is that a typo in a script cannot turn into a mutation on a machine the script was only reading.
        """
        verb, read_only, valid, reason = classify(argument)
        if not valid:
            return {"error": reason}
        return self.try_call(("cinema " + argument).strip())

    def close(self):
        try:
            self.file.close()
            self.sock.close()
        except OSError:
            pass


class Report:
    def __init__(self):
        self.passed = 0
        self.failed = 0
        self.lines = []

    def check(self, ok, what, detail=""):
        if ok:
            self.passed += 1
            print(f"PASS {what}" + (f"  ({detail})" if detail else ""))
        else:
            self.failed += 1
            print(f"FAIL {what}" + (f"  ({detail})" if detail else ""))
        self.lines.append({"ok": bool(ok), "what": what, "detail": detail})
        return ok

    def summary(self):
        total = self.passed + self.failed
        print(f"\n{self.passed}/{total} checks passed" + (f", {self.failed} FAILED" if self.failed else ""))
        return 0 if self.failed == 0 else 1


# The fields of the snapshot, and how each one is compared.
#
# Positions and rotations are compared with a tolerance, because the player's own character shuffles on the spot
# (idle sway) even when nothing has touched it. The tolerances are the ones the two-machine check already uses
# (tools/cinematictwomachine.py): 0.05 m of position and 2 degrees of rotation.
#
# The field of view and the enabled state are compared EXACTLY. Nothing about the game moves them on its own, so
# any difference at all is the camera's doing.
POSITION_FIELDS = ("cam_pos", "main_pos", "far_pos", "player_pos")
ROTATION_FIELDS = ("cam_rot", "player_rot", "body_rot")
# The exact fields split by what they hold: an enabled state is a boolean, a field of view is a single number.
ENABLED_FIELDS = ("cam_enabled", "main_enabled", "far_enabled", "sky_enabled")
FOV_FIELDS = ("cam_fov", "main_fov", "far_fov", "shot_fov")
EXACT_FIELDS = FOV_FIELDS + ENABLED_FIELDS

POSITION_TOLERANCE = 0.05          # metres
ANGLE_TOLERANCE = 2.0              # degrees

SNAPSHOT_FIELDS = [
    ("cam_pos", "mainscript.s.player.Cam.transform.position"),
    ("cam_rot", "mainscript.s.player.Cam.transform.rotation"),
    ("cam_fov", "mainscript.s.player.Cam.fieldOfView"),
    ("cam_enabled", "mainscript.s.player.Cam.enabled"),
    ("main_pos", "mainscript.s.player.mainCam.transform.position"),
    ("main_fov", "mainscript.s.player.mainCam.fieldOfView"),
    ("main_enabled", "mainscript.s.player.mainCam.enabled"),
    ("far_pos", "mainscript.s.player.farCamera.transform.position"),
    ("far_fov", "mainscript.s.player.farCamera.fieldOfView"),
    ("far_enabled", "mainscript.s.player.farCamera.enabled"),
    ("sky_enabled", "mainscript.s.player.skyCamera.enabled"),
    ("shot_fov", "mainscript.s.player.saveScreenshotCam.fieldOfView"),
    ("player_pos", "mainscript.s.player.transform.position"),
    ("player_rot", "mainscript.s.player.transform.rotation"),
    ("body_rot", "mainscript.s.player.BodyRot.rotation"),
]


# The numbers in a value the bridge sent are read strictly: the WHOLE value must be numbers and nothing else.
#
# The bridge answers a `get` with the value rendered as text: a Vector3 comes back as "1.00,2.00,3.00", a
# Quaternion as "(0.00000, 0.00000, 0.00000, 1.00000)" and a float as a plain number. A JSON list of numbers is
# accepted too. Anything else is not a reading: text with numbers in it ("1.00,2.00,3.00 approx"), a value with
# the wrong number of parts, a number that is not finite (NaN, infinity) and a zero quaternion are all unreadable,
# because a check that cannot read a value must not claim that nothing changed.
_NUMBER_TEXT = re.compile(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?")


def _numbers(raw):
    """The numbers in a value the bridge sent, or None if the value is not a clean list of them.

    Every number must be finite: a NaN or an infinity is not a position and not a rotation. A string must be a
    comma-separated list of numbers and nothing else, so junk that happens to contain digits does not qualify.
    """
    if isinstance(raw, bool):
        return None
    if isinstance(raw, (int, float)):
        value = float(raw)
        return [value] if math.isfinite(value) else None
    if isinstance(raw, (list, tuple)):
        numbers = []
        for item in raw:
            if isinstance(item, bool) or not isinstance(item, (int, float)):
                return None
            value = float(item)
            if not math.isfinite(value):
                return None
            numbers.append(value)
        return numbers or None
    if isinstance(raw, str):
        text = raw.strip()
        if text.startswith("(") and text.endswith(")"):
            text = text[1:-1].strip()
        if not text:
            return None
        numbers = []
        for token in text.split(","):
            token = token.strip()
            if token.lower() in ("nan", "inf", "+inf", "-inf", "infinity", "+infinity", "-infinity"):
                return None
            if not _NUMBER_TEXT.fullmatch(token):
                return None
            value = float(token)
            if not math.isfinite(value):
                return None
            numbers.append(value)
        return numbers or None
    return None


def _has_non_finite(value):
    """Whether the value holds a number that is not finite (a NaN or an infinity), whatever else it holds.

    A NaN in a position is not a position: the check has to say so instead of calling the field unreadable for
    no reason at all.
    """
    if isinstance(value, bool):
        return False
    if isinstance(value, (int, float)):
        return not math.isfinite(float(value))
    if isinstance(value, (list, tuple)):
        return any(not isinstance(item, bool) and isinstance(item, (int, float))
                   and not math.isfinite(float(item)) for item in value)
    if isinstance(value, str):
        for token in value.strip().strip("()").split(","):
            token = token.strip()
            if token.lower() in ("nan", "inf", "+inf", "-inf", "infinity", "+infinity", "-infinity"):
                return True
            if _NUMBER_TEXT.fullmatch(token):
                try:
                    if not math.isfinite(float(token)):
                        return True
                except ValueError:
                    pass
    return False


def _reading(name, value):
    """The numbers of one snapshot field, and why it cannot be read (an empty reason means it can).

    A position is exactly three numbers, a rotation is exactly four (a quaternion that is all zeros is not a
    rotation at all), a field of view is exactly one number and an enabled state is exactly a boolean. The arity is
    checked here, so no caller ever unpacks a list of the wrong length.
    """
    if name in ENABLED_FIELDS:
        if isinstance(value, bool):
            return None, ""
        return None, "not a boolean"
    numbers = _numbers(value)
    if numbers is None:
        return None, "not a finite number" if _has_non_finite(value) else "no value"
    if name in FOV_FIELDS:
        if len(numbers) != 1:
            return None, "not a single number"
    elif name in POSITION_FIELDS:
        if len(numbers) != 3:
            return None, "not three numbers"
    elif name in ROTATION_FIELDS:
        if len(numbers) != 4:
            return None, "not four numbers"
        if all(number == 0.0 for number in numbers):
            return None, "the quaternion is zero"
    return numbers, ""


def gameplay_snapshot(bridge):
    """Everything the gameplay depends on, read straight from the game.

    The five cameras are the player's world camera, the main camera, the distant camera, the sky camera and the
    screenshot camera; the player transform is what the network reports as this player's position. If the cinematic
    camera changed any of them, the two snapshots would differ.

    A field the bridge could not read is kept as an error string, never as a value: two identical errors are still
    two failures, and the check must say so instead of reporting "nothing changed".
    """
    snapshot = {}
    for name, path in SNAPSHOT_FIELDS:
        # A reply that is not an object is a broken answer, not a value: the field is
        # recorded as unreadable, by name, and the check says so instead of crashing.
        reply = as_reply(bridge.try_call("get " + path))
        if "error" in reply:
            snapshot[name] = "<error: " + str(reply["error"])[:60] + ">"
        else:
            snapshot[name] = reply.get("value")
    return snapshot


def unreadable(snapshot):
    """The fields of a snapshot that could not be read.

    A missing field, an error string, a value that holds no numbers, a value with the wrong number of parts, a
    value that is not finite and a zero quaternion are all unreadable: the check cannot claim the camera moved
    nothing when it does not know what the value was.
    """
    bad = []
    for name, _ in SNAPSHOT_FIELDS:
        if name not in snapshot:
            bad.append(name + " (missing)")
            continue
        value = snapshot[name]
        if isinstance(value, str) and value.startswith("<error:"):
            bad.append(name + " (error)")
            continue
        _, reason = _reading(name, value)
        if reason:
            bad.append(name + " (" + reason + ")")
    return bad


def _rotation_difference_degrees(qa, qb):
    """How far apart two rotations are, in degrees.

    The bridge sends the rotation as a quaternion, so the difference is the angle of the rotation that carries one
    onto the other. An idle sway of a fraction of a degree is far below the 2 degree tolerance. A quaternion and its
    negation are the same rotation, so they are the same angle.
    """
    if qa is None or qb is None or len(qa) != 4 or len(qb) != 4:
        return None
    ax, ay, az, aw = qa
    bx, by, bz, bw = qb
    # the relative rotation qb * inverse(qa)
    rw = aw * bw + ax * bx + ay * by + az * bz
    rx = aw * bx - ax * bw - ay * bz + az * by
    ry = aw * by + ax * bz - ay * bw - az * bx
    rz = aw * bz - ax * by + ay * bx - az * bw
    norm = (rw * rw + rx * rx + ry * ry + rz * rz) ** 0.5
    if norm == 0:
        return None
    cos_half = min(1.0, max(-1.0, abs(rw) / norm))
    return math.degrees(2.0 * math.acos(cos_half))


def _position_difference(pa, pb):
    """The largest distance between two positions, in metres."""
    if pa is None or pb is None or len(pa) != 3 or len(pb) != 3:
        return None
    return max(abs(pa[i] - pb[i]) for i in range(3))


def differences(before, after):
    """Which fields really differ between two snapshots.

    Positions and rotations are compared with their tolerance, so the player's own idle sway is not a difference;
    the field of view and the enabled state are compared exactly. A field that could not be read on either side is
    not a difference here - it is an unreadable field, and the check fails on that separately.
    """
    out = []
    for name, _ in SNAPSHOT_FIELDS:
        if name not in before or name not in after:
            continue
        a, b = before[name], after[name]
        if isinstance(a, str) and a.startswith("<error:"):
            continue
        if isinstance(b, str) and b.startswith("<error:"):
            continue
        na, reason_a = _reading(name, a)
        nb, reason_b = _reading(name, b)
        if reason_a or reason_b:
            # an unreadable field is not a difference: the readability gate reports it instead
            continue
        if name in ENABLED_FIELDS:
            if a is not b:
                out.append(f"{name} changed {a!r} -> {b!r}")
        elif name in FOV_FIELDS:
            if na[0] != nb[0]:
                out.append(f"{name} changed {a!r} -> {b!r}")
        elif name in POSITION_FIELDS:
            moved = _position_difference(na, nb)
            if moved is not None and moved > POSITION_TOLERANCE:
                out.append(f"{name} moved {moved:.3f} m")
        elif name in ROTATION_FIELDS:
            turned = _rotation_difference_degrees(na, nb)
            if turned is not None and turned > ANGLE_TOLERANCE:
                out.append(f"{name} turned {turned:.2f} deg")
        else:
            if a != b:
                out.append(f"{name} changed {a!r} -> {b!r}")
    return out


def same(before, after):
    """The names of the fields that really differ between two snapshots."""
    return [entry.split(" ", 1)[0] for entry in differences(before, after)]


def check(args):
    report = Report()
    bridge = Bridge(args.port)
    try:
        start = bridge.cinema("status")
        report.check("mode" in start, "the camera's status can be read", start.get("mode", "?"))
        report.check(start.get("active") is False, "the camera starts off")

        before = gameplay_snapshot(bridge)
        unread = unreadable(before)
        report.check(not unread, "every value the check compares can be read",
                     ", ".join(unread) if unread else "all readable")

        # --- free mode -----------------------------------------------------------------
        reply = bridge.cinema("free")
        report.check(reply.get("mode") == "free", "the camera opens in free mode", reply.get("mode", "?"))
        report.check(reply.get("active") is True, "the camera is on")
        during = gameplay_snapshot(bridge)
        unread = unreadable(during)
        report.check(not unread, "every value can still be read while the camera is on",
                     ", ".join(unread) if unread else "all readable")
        moved = differences(before, during)
        report.check(not moved, "the gameplay cameras and the player did not move",
                     ", ".join(moved) or "nothing changed")

        # --- orbit mode ----------------------------------------------------------------
        reply = bridge.cinema("orbit")
        report.check(reply.get("mode") == "orbit", "the camera switches to orbit mode", reply.get("mode", "?"))
        during_orbit = gameplay_snapshot(bridge)
        unread = unreadable(during_orbit)
        report.check(not unread, "every value can still be read in orbit mode",
                     ", ".join(unread) if unread else "all readable")
        moved = differences(before, during_orbit)
        report.check(not moved, "the gameplay cameras and the player did not move in orbit mode",
                     ", ".join(moved) or "nothing changed")

        # --- the bounds ----------------------------------------------------------------
        reply = bridge.cinema("speed 99999")
        report.check(0.25 <= reply.get("speed", 0) <= 60, "the speed is clamped", str(reply.get("speed")))
        reply = bridge.cinema("speed 0.01")
        report.check(0.25 <= reply.get("speed", 0) <= 60, "a speed below the floor is clamped", str(reply.get("speed")))
        reply = bridge.cinema("fov 500")
        report.check(10 <= reply.get("fov", 0) <= 120, "the field of view is clamped", str(reply.get("fov")))
        reply = bridge.cinema("fov 1")
        report.check(10 <= reply.get("fov", 0) <= 120, "a field of view below the floor is clamped", str(reply.get("fov")))
        reply = bridge.cinema("radius 100000")
        report.check(1 <= reply.get("radius", 0) <= 400, "the orbit radius is clamped", str(reply.get("radius")))
        reply = bridge.cinema("radius 0")
        report.check(1 <= reply.get("radius", 0) <= 400, "an orbit radius below the floor is clamped", str(reply.get("radius")))

        # --- the orbit stays a sane distance from its target ------------------------------
        status = bridge.cinema("status")
        # The orbit's target is in the game's own coordinates, and so is the camera: the separation between them
        # is what the orbit radius means.
        position = status.get("position", [0, 0, 0])
        target = status.get("target", [0, 0, 0])
        separation = sum((position[i] - target[i]) ** 2 for i in range(3)) ** 0.5
        radius = status.get("radius", 0)
        report.check(separation > 0.05, "the orbit camera is not inside its target", f"{separation:.2f} m")
        report.check(abs(separation - radius) < 0.05, "the orbit camera is exactly the radius from its target",
                     f"{separation:.2f} vs {radius:.2f} m")

        # --- the shot list ---------------------------------------------------------------
        shot = bridge.cinema("shot")
        report.check("loaded" in shot, "the shot list's status can be read")

        # --- off again --------------------------------------------------------------------
        reply = bridge.cinema("off")
        report.check(reply.get("active") is False, "the camera turns off")
        report.check(reply.get("cursorOwned") is False, "the camera gave the cursor back")
        after = gameplay_snapshot(bridge)
        unread = unreadable(after)
        report.check(not unread, "every value the check compares can still be read",
                     ", ".join(unread) if unread else "all readable")
        moved = differences(before, after)
        report.check(not moved, "the gameplay cameras and the player are exactly as they were",
                     ", ".join(moved) or "nothing changed")

        # --- the free text is refused -------------------------------------------------------
        reply = bridge.cinema("speed my name")
        report.check("error" in reply, "a free-text argument is refused", str(reply.get("error"))[:60])

        return report.summary()
    finally:
        bridge.close()


def status(args):
    bridge = Bridge(args.port)
    try:
        print(json.dumps(bridge.try_call("cinema status"), indent=1))
        return 0
    finally:
        bridge.close()


def shot(args):
    """Load a shot list and let it play. The directives are the ones the mod's own parser accepts."""
    text = pathlib.Path(args.file).read_text(encoding="utf-8")
    # The bridge takes one line per directive; newlines are written as \\n.
    escaped = text.replace("\n", "\\n")
    bridge = Bridge(args.port)
    try:
        reply = bridge.try_call("cinema shot load " + escaped)
        print(json.dumps(reply, indent=1))
        return 1 if "error" in reply else 0
    finally:
        bridge.close()


def wait_for_file(path, timeout=SCREENSHOT_WAIT_SECONDS, interval=0.05):
    """Whether a file has appeared, is not empty and has stopped growing.

    Unity writes the capture at the end of the frame, so the file appears a moment
    after the command was answered and is then still being written. A file that is
    there but still growing is not finished, so the size is compared twice: the file
    counts as ready when it exists, is not empty, and has the same size on two
    consecutive reads. The wait is bounded, so a capture that never arrives fails
    instead of hanging the caller.
    """
    deadline = time.monotonic() + timeout
    last_size = -1
    while time.monotonic() < deadline:
        try:
            size = pathlib.Path(path).stat().st_size
        except OSError:
            size = -1
        if size > 0 and size == last_size:
            return True
        last_size = size
        time.sleep(interval)
    return False


def screenshot(args):
    """Ask the mod to capture the camera's own view.

    The file is written by the MOD, into a folder of the camera's own
    (`BepInEx/tldrevamp-cinematic/`); nothing is uploaded, attached to a report or
    put into the repository's telemetry. The reply carries the path that was
    written, so the caller can assert the picture exists.

    The capture is asynchronous: the mod hands the path to Unity, and Unity writes
    the file at the end of the frame. So the tool waits for the file to appear, to be
    non-empty and to stop growing, for at most two seconds, and only then reports
    success. Without the wait the tool could print a path that does not exist yet.
    """
    bridge = Bridge(args.port)
    try:
        reply = bridge.cinema("screenshot")
        print(json.dumps(reply, indent=1))
        if "error" in reply:
            return 1
        path = reply.get("path")
        if not path:
            print("the mod did not report a path for the screenshot")
            return 1
        if not wait_for_file(path):
            print(f"the screenshot at {path} did not appear within {SCREENSHOT_WAIT_SECONDS:.0f}s")
            return 1
        return 0
    finally:
        bridge.close()


def parser():
    """The CLI definition, in one place so the tests can enumerate its subcommands.

    tools/test_cinematic.py walks argparse's own choices and asserts that every
    registered subcommand has a callable behind it. That is the guard which would
    have caught the missing screenshot() before anyone ran the command.
    """
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=27050)
    sub = ap.add_subparsers(dest="command", required=True)
    # Every subcommand takes --port too, so the documented shape (the flag after the
    # subcommand) works as well as the shape argparse gives by default (before it).
    sub.add_parser("status", help="the camera's state").add_argument("--port", type=int, default=argparse.SUPPRESS)
    sub.add_parser("check", help="the full check").add_argument("--port", type=int, default=argparse.SUPPRESS)
    shot_parser = sub.add_parser("shot", help="load a shot list")
    shot_parser.add_argument("file")
    shot_parser.add_argument("--port", type=int, default=argparse.SUPPRESS)
    sub.add_parser("screenshot", help="take a screenshot of the camera's view").add_argument(
        "--port", type=int, default=argparse.SUPPRESS)
    return ap


def main():
    args = parser().parse_args()
    if args.command == "status":
        return status(args)
    if args.command == "check":
        return check(args)
    if args.command == "screenshot":
        return screenshot(args)
    return shot(args)


if __name__ == "__main__":
    raise SystemExit(main())
