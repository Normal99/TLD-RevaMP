#!/usr/bin/env python3
"""Offline tests of the cinematic camera's tool (no game needed).

  python3 -m unittest tools/test_cinematic.py

Two things are checked here:

  * the tool's own command surface: which `cinema` commands only read, which change something, and which are
    refused outright. The tool refuses a malformed command *before* sending it, so a typo in a script cannot turn
    into a mutation on a machine the script was only meant to read.
  * that the tool's surface still matches the mod's own (src/TLDRevamp/Cinematic/CinematicBridge.cs and
    CinematicCameraCore.cs). The two are separate implementations of the same rule, and this test is what keeps
    them from drifting apart: it reads the C# source and compares.

Nothing here starts the game, opens a socket or touches a file outside a temporary directory.
"""
import argparse
import io
import json
import os
import pathlib
import re
import socket
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import cinematic

TOOLS = pathlib.Path(__file__).resolve().parent
ROOT = TOOLS.parent


class CommandSurface(unittest.TestCase):
    """Which camera commands only read, and which change something."""

    def test_status_is_a_query(self):
        for argument in ("", "   ", "status", " status ", "status extra"):
            self.assertTrue(cinematic.is_read_only(argument), argument)
            verb, read_only, valid, reason = cinematic.classify(argument)
            self.assertTrue(read_only, argument)
            self.assertTrue(valid, argument)
            self.assertEqual(verb, "status", argument)

    def test_every_other_command_is_a_mutation(self):
        for argument in ("on", "off", "free", "orbit", "toggle", "recentre", "speed 4", "fov 35", "radius 20",
                         "shot", "shot load at 0 move 1 2 3 4 5 6", "shot unload", "shot write", "screenshot"):
            self.assertFalse(cinematic.is_read_only(argument), argument)
            _, read_only, valid, reason = cinematic.classify(argument)
            self.assertFalse(read_only, argument)
            self.assertTrue(valid, f"{argument}: {reason}")

    def test_unknown_commands_are_refused(self):
        for argument in ("nonsense", "label hello", "onn", "camera", "statusx"):
            _, read_only, valid, reason = cinematic.classify(argument)
            self.assertFalse(valid, argument)
            self.assertTrue(reason, argument)

    def test_numeric_commands_need_one_number(self):
        for argument in ("speed", "speed 4 5", "speed abc", "fov", "fov 1 2", "fov x", "radius", "radius 1 2"):
            _, _, valid, reason = cinematic.classify(argument)
            self.assertFalse(valid, argument)
            self.assertTrue(reason, argument)

    def test_numeric_commands_reject_nan_and_infinity(self):
        for argument in ("speed nan", "speed inf", "fov -inf", "radius NaN"):
            _, _, valid, reason = cinematic.classify(argument)
            self.assertFalse(valid, argument)
            self.assertTrue(reason, argument)

    def test_a_number_is_accepted(self):
        for argument in ("speed 4.5", "fov 35", "radius 20", "speed -1", "fov 1e3"):
            _, _, valid, reason = cinematic.classify(argument)
            self.assertTrue(valid, f"{argument}: {reason}")

    def test_shot_needs_a_known_verb(self):
        _, _, valid, reason = cinematic.classify("shot load")
        self.assertFalse(valid)
        self.assertTrue(reason)
        _, _, valid, reason = cinematic.classify("shot load at 0 move 1 2 3 4 5 6")
        self.assertTrue(valid, reason)
        _, _, valid, reason = cinematic.classify("shot unload")
        self.assertTrue(valid, reason)
        _, _, valid, reason = cinematic.classify("shot write")
        self.assertTrue(valid, reason)
        _, _, valid, reason = cinematic.classify("shot nonsense")
        self.assertFalse(valid)
        self.assertTrue(reason)

    def test_the_bounds_match_the_mod(self):
        # The tool's own expectation of the camera's bounds, so a bridge that stopped clamping is caught.
        self.assertEqual(cinematic.LIMITS["speed"], (0.25, 60.0))
        self.assertEqual(cinematic.LIMITS["fov"], (10.0, 120.0))
        self.assertEqual(cinematic.LIMITS["radius"], (1.0, 400.0))
        self.assertEqual(cinematic.clamp("speed", 99999), 60.0)
        self.assertEqual(cinematic.clamp("speed", 0.0), 0.25)
        self.assertEqual(cinematic.clamp("fov", 500), 120.0)
        self.assertEqual(cinematic.clamp("radius", 0), 1.0)
        self.assertEqual(cinematic.clamp("fov", 60), 60.0)


class MatchesTheMod(unittest.TestCase):
    """The tool's surface and the mod's own surface must say the same thing."""

    def source(self, relative):
        return (ROOT / relative).read_text(encoding="utf-8")

    def test_the_mod_has_the_same_commands(self):
        bridge = self.source("src/TLDRevamp/Cinematic/CinematicBridge.cs")
        for verb in cinematic.MUTATION_COMMANDS:
            self.assertRegex(bridge, rf'case "{verb}"', f"the mod handles '{verb}'")
        for verb in cinematic.QUERY_COMMANDS:
            self.assertRegex(bridge, rf'case "{verb}"', f"the mod handles '{verb}'")

    def test_the_mod_has_the_same_shot_verbs(self):
        bridge = self.source("src/TLDRevamp/Cinematic/CinematicBridge.cs")
        for verb in cinematic.SHOT_COMMANDS:
            self.assertRegex(bridge, rf'"{verb}"', f"the mod handles shot '{verb}'")

    def test_the_mod_clamps_the_same_way(self):
        core = self.source("src/TLDRevamp/Cinematic/CinematicCameraCore.cs")
        for name, (low, high) in cinematic.LIMITS.items():
            self.assertIn(f"{low:g}f", core, f"the mod's {name} floor is {low}")
            self.assertIn(f"{high:g}f", core, f"the mod's {name} ceiling is {high}")

    def test_the_mod_classifies_the_same_way(self):
        # The mod's pure policy: only the status is a query.
        core = self.source("src/TLDRevamp/Cinematic/CinematicCameraCore.cs")
        policy = core[core.index("class CinematicCommandPolicy"):]
        policy = policy[:policy.index("\n    }")]
        self.assertIn('arg == "status"', policy)
        self.assertIn('arg.StartsWith("status "', policy)
        self.assertIn("arg.Length == 0", policy)

    def test_the_bridge_calls_the_pure_policy(self):
        bridge = self.source("src/TLDRevamp/DebugBridge.cs")
        self.assertIn("Cinematic.CinematicCommandPolicy.IsReadOnly(arg)", bridge)


class NoSecrets(unittest.TestCase):
    """The tool must not read the environment, the user's files or the machine's details."""

    def assertNoUse(self, source, forbidden, why):
        """A short failure message: the whole source in the message is unreadable."""
        if forbidden in source:
            line = next((n for n, l in enumerate(source.splitlines(), 1) if forbidden in l), 0)
            self.fail(f"{why} (line {line})")

    def test_the_tool_does_not_read_the_environment(self):
        source = (TOOLS / "cinematic.py").read_text(encoding="utf-8")
        for forbidden in ("os.environ", "getenv", "expanduser", "Path.home()", "os.getenv", "platform."):
            self.assertNoUse(source, forbidden, f"the cinematic tool must not use {forbidden}")

    def test_the_tool_does_not_write_files(self):
        source = (TOOLS / "cinematic.py").read_text(encoding="utf-8")
        for forbidden in ("write_text", "write_bytes", "open(", "mkdir", "unlink", "rmtree"):
            self.assertNoUse(source, forbidden, f"the cinematic tool must not use {forbidden}")

    def test_the_tool_does_not_run_commands(self):
        source = (TOOLS / "cinematic.py").read_text(encoding="utf-8")
        for forbidden in ("subprocess", "os.system", "popen", "shutil"):
            self.assertNoUse(source, forbidden, f"the cinematic tool must not use {forbidden}")

    def test_the_tool_only_talks_to_the_bridge(self):
        source = (TOOLS / "cinematic.py").read_text(encoding="utf-8")
        # One socket, to the loopback bridge, on a port.
        self.assertIn('socket.create_connection(("127.0.0.1", port)', source)
        # No other host: the only address in the file is the loopback one.
        addresses = set(re.findall(r'\(?\"([0-9]{1,3}(?:\.[0-9]{1,3}){3})\"?', source))
        self.assertEqual(addresses, {"127.0.0.1"}, f"the cinematic tool must only reach the loopback bridge: {addresses}")
        for host in ("0.0.0.0", "localhost", "http://", "https://", "urllib", "requests"):
            self.assertNoUse(source, host, f"the cinematic tool must not reach {host}")


class FakeBridge(unittest.TestCase):
    """The tool's bridge wrapper: it must refuse a malformed command before sending it."""

    def test_a_malformed_command_is_not_sent(self):
        sent = []

        class Fake:
            def try_call(self, command):
                sent.append(command)
                return {"error": "should not get here"}

        bridge = Fake()
        # The wrapper lives on the class, so it is exercised through a real instance's method.
        wrapper = cinematic.Bridge.__new__(cinematic.Bridge)
        wrapper.try_call = bridge.try_call
        reply = wrapper.cinema("speed my name")
        self.assertIn("error", reply)
        self.assertEqual(sent, [], "a malformed command must not reach the bridge")

    def test_a_valid_command_is_sent(self):
        sent = []

        class Fake:
            def try_call(self, command):
                sent.append(command)
                return {"mode": "free", "active": True}

        wrapper = cinematic.Bridge.__new__(cinematic.Bridge)
        wrapper.try_call = Fake().try_call
        reply = wrapper.cinema("free")
        self.assertEqual(reply["mode"], "free")
        self.assertEqual(sent, ["cinema free"])

    def test_the_status_is_sent_as_a_query(self):
        sent = []

        class Fake:
            def try_call(self, command):
                sent.append(command)
                return {"mode": "off"}

        wrapper = cinematic.Bridge.__new__(cinematic.Bridge)
        wrapper.try_call = Fake().try_call
        wrapper.cinema("status")
        self.assertEqual(sent, ["cinema status"])


class ReportFormat(unittest.TestCase):
    """The check's own output: PASS/FAIL lines and a summary."""

    def test_a_report_counts_and_summarises(self):
        report = cinematic.Report()
        report.check(True, "something works", "detail")
        report.check(False, "something else fails")
        self.assertEqual(report.passed, 1)
        self.assertEqual(report.failed, 1)
        self.assertEqual(len(report.lines), 2)
        self.assertEqual(report.summary(), 1)

    def test_a_clean_report_exits_zero(self):
        report = cinematic.Report()
        report.check(True, "everything works")
        self.assertEqual(report.summary(), 0)


class EveryRegisteredSubcommandExists(unittest.TestCase):
    """Every subcommand argparse OFFERS must have a callable behind it.

    This is the guard for the bug that shipped: ``screenshot`` was registered in
    the parser and dispatched by main(), but the function had never been written,
    so the command crashed with NameError the first time anyone ran it. Nineteen
    tests passed because none of them enumerated the parser's own subcommands.
    """

    def test_every_subcommand_resolves_to_a_callable(self):
        cli = cinematic.parser()
        actions = [a for a in cli._actions if isinstance(a, argparse._SubParsersAction)]
        self.assertEqual(len(actions), 1, "the tool must have exactly one subcommand group")
        commands = sorted(actions[0].choices)
        self.assertTrue(commands, "the parser must register at least one subcommand")
        for name in commands:
            self.assertTrue(
                callable(getattr(cinematic, name, None)),
                f"the registered subcommand {name!r} has no callable cinematic.{name}() behind it")


class SnapshotComparison(unittest.TestCase):
    """The comparison of two gameplay snapshots.

    Two things were wrong with it before:

      * it compared the positions and rotations EXACTLY, so the player's own idle
        sway (a fraction of a degree, a millimetre) was reported as "the gameplay
        cameras and the player moved". The two-machine check already allows 0.05 m
        and 2 degrees for exactly this reason;
      * it treated two identical error strings as "nothing changed", so a check that
        could not read a single value passed.
    """

    def snapshot(self, **overrides):
        """A snapshot with realistic values: the bridge sends text, not JSON lists."""
        base = {
            "cam_pos": "1.00,2.00,3.00",
            "cam_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
            "cam_fov": 60.0,
            "cam_enabled": True,
            "main_pos": "1.00,2.00,3.00",
            "main_fov": 60.0,
            "main_enabled": True,
            "far_pos": "1.00,2.00,3.00",
            "far_fov": 60.0,
            "far_enabled": True,
            "sky_enabled": True,
            "shot_fov": 60.0,
            "player_pos": "1.00,2.00,3.00",
            "player_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
            "body_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
        }
        base.update(overrides)
        return base

    def test_an_idle_sway_is_not_a_difference(self):
        # The player shuffles on the spot: a millimetre and a fraction of a degree.
        before = self.snapshot()
        after = self.snapshot(player_pos="1.01,2.00,3.00",
                              body_rot="(0.00000, 0.00700, 0.00000, 0.99998)")
        self.assertEqual(cinematic.differences(before, after), [])
        self.assertEqual(cinematic.same(before, after), [])

    def test_a_real_move_is_a_difference(self):
        before = self.snapshot()
        after = self.snapshot(player_pos="1.50,2.00,3.00")
        self.assertTrue(cinematic.differences(before, after))
        self.assertIn("player_pos", cinematic.same(before, after))

    def test_a_real_turn_is_a_difference(self):
        before = self.snapshot()
        # 90 degrees around Y.
        after = self.snapshot(body_rot="(0.00000, 0.70711, 0.00000, 0.70711)")
        self.assertTrue(cinematic.differences(before, after))
        self.assertIn("body_rot", cinematic.same(before, after))

    def test_the_field_of_view_is_compared_exactly(self):
        before = self.snapshot()
        after = self.snapshot(cam_fov=60.001)
        self.assertTrue(cinematic.differences(before, after), "a hair of FOV is still a difference")
        self.assertIn("cam_fov", cinematic.same(before, after))

    def test_the_enabled_state_is_compared_exactly(self):
        before = self.snapshot()
        after = self.snapshot(sky_enabled=False)
        self.assertTrue(cinematic.differences(before, after))
        self.assertIn("sky_enabled", cinematic.same(before, after))

    def test_an_unreadable_value_is_not_a_difference_but_is_unreadable(self):
        before = self.snapshot()
        after = self.snapshot(cam_fov="<error: the camera is not there>")
        self.assertEqual(cinematic.differences(before, after), [],
                         "an unreadable value is an unreadable field, not a difference")
        self.assertTrue(cinematic.unreadable(after))

    def test_two_identical_errors_are_unreadable_not_equal(self):
        broken = self.snapshot(cam_fov="<error: no such member>")
        self.assertTrue(cinematic.unreadable(broken))
        self.assertEqual(cinematic.same(broken, broken), [],
                         "two identical errors must not be reported as equal-and-fine")

    def test_a_missing_field_is_unreadable(self):
        snapshot = self.snapshot()
        del snapshot["cam_fov"]
        self.assertTrue(cinematic.unreadable(snapshot))

    def test_a_full_snapshot_is_readable(self):
        self.assertEqual(cinematic.unreadable(self.snapshot()), [])


class UnreadableValues(unittest.TestCase):
    """The values the check must refuse to read.

    The comparison used to be too trusting: a position that was a single number, a position with a NaN in it and
    a zero quaternion all counted as "readable", so a check that could not read them reported "nothing changed"
    and passed. A rotation with five numbers in it even crashed the comparison with a ValueError. A check that
    cannot read a value must fail, and it must say WHICH field it could not read.
    """

    def snapshot(self, **overrides):
        """A snapshot with realistic values: the bridge sends text, not JSON lists."""
        base = {
            "cam_pos": "1.00,2.00,3.00",
            "cam_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
            "cam_fov": 60.0,
            "cam_enabled": True,
            "main_pos": "1.00,2.00,3.00",
            "main_fov": 60.0,
            "main_enabled": True,
            "far_pos": "1.00,2.00,3.00",
            "far_fov": 60.0,
            "far_enabled": True,
            "sky_enabled": True,
            "shot_fov": 60.0,
            "player_pos": "1.00,2.00,3.00",
            "player_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
            "body_rot": "(0.00000, 0.00000, 0.00000, 1.00000)",
        }
        base.update(overrides)
        return base

    def names(self, snapshot):
        """The names of the unreadable fields, without the reason."""
        return [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)]

    # --- the four probes the review found -------------------------------------------------

    def test_a_position_that_is_a_single_number_is_unreadable(self):
        snapshot = self.snapshot(cam_pos="7")
        self.assertIn("cam_pos", self.names(snapshot))
        self.assertEqual(cinematic.differences(snapshot, snapshot), [],
                         "an unreadable value is an unreadable field, not a difference")

    def test_a_position_with_a_nan_in_it_is_unreadable(self):
        snapshot = self.snapshot(player_pos=[float("nan"), 0.0, 0.0])
        self.assertIn("player_pos", self.names(snapshot))
        self.assertEqual(cinematic.differences(snapshot, snapshot), [])

    def test_a_rotation_with_five_numbers_does_not_crash_and_is_unreadable(self):
        snapshot = self.snapshot(cam_rot="(0.10000, 0.20000, 0.30000, 0.40000, 0.50000)")
        # before the repair this raised "too many values to unpack (expected 4, got 5)"
        self.assertIn("cam_rot", self.names(snapshot))
        self.assertEqual(cinematic.differences(snapshot, snapshot), [])
        self.assertEqual(cinematic.same(snapshot, snapshot), [])

    def test_a_zero_quaternion_is_unreadable(self):
        snapshot = self.snapshot(body_rot="(0.00000, 0.00000, 0.00000, 0.00000)")
        self.assertIn("body_rot", self.names(snapshot))
        self.assertEqual(cinematic.differences(snapshot, snapshot), [])

    # --- the same classes of bad value, in other shapes -------------------------------------

    def test_a_position_with_two_numbers_is_unreadable(self):
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos="1.00,2.00")))

    def test_a_position_with_four_numbers_is_unreadable(self):
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos="1.00,2.00,3.00,4.00")))

    def test_a_rotation_with_three_numbers_is_unreadable(self):
        self.assertIn("cam_rot", self.names(self.snapshot(cam_rot="(0.00000, 0.00000, 1.00000)")))

    def test_an_infinite_number_is_unreadable(self):
        self.assertIn("main_pos", self.names(self.snapshot(main_pos=[float("inf"), 0.0, 0.0])))
        self.assertIn("main_pos", self.names(self.snapshot(main_pos="(Infinity, 0.00, 0.00)")))

    def test_a_not_a_number_in_the_text_is_unreadable(self):
        self.assertIn("player_pos", self.names(self.snapshot(player_pos="(NaN, 0.00, 0.00)")))

    def test_a_field_of_view_with_two_numbers_is_unreadable(self):
        self.assertIn("cam_fov", self.names(self.snapshot(cam_fov="60.0,61.0")))

    def test_a_field_of_view_with_no_number_is_unreadable(self):
        self.assertIn("cam_fov", self.names(self.snapshot(cam_fov="wide")))

    def test_an_enabled_state_that_is_a_number_is_unreadable(self):
        # 1 and 0 are truthy and falsey in Python, but the bridge sends true and false.
        self.assertIn("cam_enabled", self.names(self.snapshot(cam_enabled=1)))
        self.assertIn("cam_enabled", self.names(self.snapshot(cam_enabled="true")))

    def test_junk_with_numbers_in_it_is_not_readable(self):
        # "1.00,2.00,3.00 approx" holds three numbers, but it is not a position.
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos="1.00,2.00,3.00 approx")))
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos="1.00,2.00,3.00 and more")))
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos="[1.00,2.00,3.00]")))

    def test_a_list_with_a_junk_item_is_not_readable(self):
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos=["1.00", "2.00", "3.00"])))
        self.assertIn("cam_pos", self.names(self.snapshot(cam_pos=["1.00", 2.00, None])))

    # --- the values that must still be read --------------------------------------------------

    def test_the_bridge_format_is_read(self):
        # The bridge renders a Vector3 as "1.00,2.00,3.00" and a Quaternion as "(0.00, 0.00, 0.00, 1.00)".
        self.assertEqual(cinematic._numbers("1.00,2.00,3.00"), [1.0, 2.0, 3.0])
        self.assertEqual(cinematic._numbers("(0.00000, 0.00000, 0.00000, 1.00000)"),
                         [0.0, 0.0, 0.0, 1.0])
        self.assertEqual(cinematic._numbers("60"), [60.0])
        self.assertEqual(cinematic._numbers([1.0, 2.0, 3.0]), [1.0, 2.0, 3.0])
        self.assertEqual(cinematic._numbers(60.0), [60.0])

    def test_a_full_snapshot_is_still_readable(self):
        self.assertEqual(cinematic.unreadable(self.snapshot()), [])

    def test_a_valid_snapshot_has_no_differences(self):
        before = self.snapshot()
        self.assertEqual(cinematic.differences(before, self.snapshot()), [])
        self.assertEqual(cinematic.same(before, self.snapshot()), [])

    def test_the_tolerances_still_hold(self):
        before = self.snapshot()
        after = self.snapshot(player_pos="1.04,2.00,3.00",
                              body_rot="(0.00000, 0.00700, 0.00000, 0.99998)")
        self.assertEqual(cinematic.differences(before, after), [],
                         "an idle sway of 4 cm and 0.4 degrees is not a difference")

    def test_a_quaternion_and_its_negation_are_the_same_rotation(self):
        before = self.snapshot(body_rot="(0.00000, 0.00000, 0.00000, 1.00000)")
        after = self.snapshot(body_rot="(0.00000, 0.00000, 0.00000, -1.00000)")
        self.assertEqual(cinematic.differences(before, after), [],
                         "q and -q are the same rotation")

    def test_a_real_turn_is_still_a_difference(self):
        before = self.snapshot()
        after = self.snapshot(body_rot="(0.00000, 0.70711, 0.00000, 0.70711)")
        self.assertIn("body_rot", cinematic.same(before, after))

    def test_the_enabled_state_is_still_compared_exactly(self):
        before = self.snapshot()
        after = self.snapshot(sky_enabled=False)
        self.assertIn("sky_enabled", cinematic.same(before, after))

    def test_the_field_of_view_is_still_compared_exactly(self):
        before = self.snapshot()
        after = self.snapshot(cam_fov=60.001)
        self.assertIn("cam_fov", cinematic.same(before, after))

    def test_a_real_move_is_still_a_difference(self):
        before = self.snapshot()
        after = self.snapshot(player_pos="1.50,2.00,3.00")
        self.assertIn("player_pos", cinematic.same(before, after))

    def test_differences_does_not_crash_on_any_of_the_bad_values(self):
        bad = ["7", "1.00,2.00", "1.00,2.00,3.00,4.00", "wide", "1.00,2.00,3.00 approx",
               "(0.0, 0.0, 0.0, 0.0, 0.0)", "(0.0, 0.0, 0.0)", [float("nan"), 0.0, 0.0],
               [float("inf"), 0.0, 0.0], None, True, ""]
        for value in bad:
            before = self.snapshot(cam_pos=value, cam_rot=value, cam_fov=value, cam_enabled=value)
            after = self.snapshot(cam_pos=value, cam_rot=value, cam_fov=value, cam_enabled=value)
            self.assertEqual(cinematic.differences(before, after), [], value)
            self.assertEqual(cinematic.same(before, after), [], value)


class SnapshotReading(unittest.TestCase):
    """The snapshot is read from a bridge that may answer with an error."""

    class Fake:
        def __init__(self, error_for=()):
            self.error_for = set(error_for)
            self.sent = []

        def try_call(self, command):
            self.sent.append(command)
            for name, path in cinematic.SNAPSHOT_FIELDS:
                if command == "get " + path and name in self.error_for:
                    return {"error": "no such member"}
            return {"value": "1.00,2.00,3.00"}

    def test_a_field_the_bridge_could_not_read_keeps_its_error(self):
        snapshot = cinematic.gameplay_snapshot(self.Fake(error_for=("cam_fov",)))
        self.assertTrue(str(snapshot["cam_fov"]).startswith("<error:"))
        self.assertTrue(cinematic.unreadable(snapshot))

    def test_every_field_is_read(self):
        fake = self.Fake()
        snapshot = cinematic.gameplay_snapshot(fake)
        self.assertEqual(sorted(snapshot), sorted(name for name, _ in cinematic.SNAPSHOT_FIELDS))
        self.assertEqual(len(fake.sent), len(cinematic.SNAPSHOT_FIELDS))

    def test_a_whole_snapshot_reads_clean(self):
        snapshot = cinematic.gameplay_snapshot(self.Fake())
        # The fake answers every field with a position, so the boolean fields are not booleans: they must be
        # reported as unreadable rather than silently accepted.
        self.assertTrue(cinematic.unreadable(snapshot))


class BridgeLineReading(unittest.TestCase):
    """The bridge's own answer: a line that is not JSON is a broken answer, not a crash.

    The socket layer reads one line and parses it. A line the mod never sends - a stray
    print, a truncated write - must not kill the tool: it is reported like any other
    unreadable field.
    """

    class FakeFile:
        def __init__(self, line):
            self.line = line
            self.written = []

        def write(self, text):
            self.written.append(text)

        def flush(self):
            pass

        def readline(self):
            return self.line

    def bridge_with(self, line):
        bridge = cinematic.Bridge.__new__(cinematic.Bridge)
        bridge.file = self.FakeFile(line)
        bridge.sock = None
        return bridge

    def test_a_json_line_is_parsed(self):
        bridge = self.bridge_with('{"value": "1.00,2.00,3.00"}\n')
        self.assertEqual(bridge("get something"), {"value": "1.00,2.00,3.00"})

    def test_a_line_that_is_not_json_is_an_error_not_a_crash(self):
        bridge = self.bridge_with("this is not json\n")
        reply = bridge("get something")
        self.assertIn("error", reply)
        self.assertIn("not JSON", reply["error"])

    def test_a_closed_connection_raises(self):
        bridge = self.bridge_with("")
        with self.assertRaises(ConnectionError):
            bridge("get something")

    def test_the_broken_line_is_an_unreadable_field(self):
        bridge = self.bridge_with("not json at all\n")
        snapshot = cinematic.gameplay_snapshot(bridge)
        self.assertTrue(str(snapshot["cam_fov"]).startswith("<error:"))
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])


class MalformedEnvelopes(unittest.TestCase):
    """A bridge that answers with something that is not an object.

    The bridge answers a `get` with a JSON object. A null, a list, a string or a number is
    a broken answer, not a value: the field must be recorded as unreadable and named, and
    the check must exit 1 instead of dying on the reply.
    """

    class Fake:
        def __init__(self, reply):
            self.reply = reply
            self.sent = []

        def try_call(self, command):
            self.sent.append(command)
            return self.reply

    def snapshot_with(self, reply):
        return cinematic.gameplay_snapshot(self.Fake(reply))

    def test_a_null_reply_is_an_unreadable_field(self):
        snapshot = self.snapshot_with(None)
        self.assertTrue(str(snapshot["cam_fov"]).startswith("<error:"))
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])

    def test_a_list_reply_is_an_unreadable_field(self):
        snapshot = self.snapshot_with([1.0, 2.0, 3.0])
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])

    def test_a_string_reply_is_an_unreadable_field(self):
        snapshot = self.snapshot_with("1.00,2.00,3.00")
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])

    def test_a_number_reply_is_an_unreadable_field(self):
        snapshot = self.snapshot_with(60.0)
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])

    def test_a_boolean_reply_is_an_unreadable_field(self):
        snapshot = self.snapshot_with(True)
        self.assertIn("cam_fov", [entry.split(" ")[0] for entry in cinematic.unreadable(snapshot)])

    def test_the_error_names_the_shape_of_the_broken_answer(self):
        snapshot = self.snapshot_with(None)
        self.assertIn("null", snapshot["cam_fov"])
        snapshot = self.snapshot_with([1])
        self.assertIn("list", snapshot["cam_fov"])
        snapshot = self.snapshot_with("x")
        self.assertIn("string", snapshot["cam_fov"])
        snapshot = self.snapshot_with(3)
        self.assertIn("number", snapshot["cam_fov"])

    def test_a_well_formed_reply_is_still_read(self):
        snapshot = self.snapshot_with({"value": "1.00,2.00,3.00"})
        self.assertEqual(snapshot["cam_fov"], "1.00,2.00,3.00")

    def test_the_reply_is_normalised_to_an_object(self):
        self.assertEqual(cinematic.as_reply({"value": 1}), {"value": 1})
        for reply, shape in ((None, "null"), ([1], "a list"), ("x", "a string"),
                             (3, "a number"), (True, "a boolean")):
            normalised = cinematic.as_reply(reply)
            self.assertIn("error", normalised, repr(reply))
            self.assertIn(shape, normalised["error"], repr(reply))

    def test_a_broken_answer_is_not_a_difference(self):
        snapshot = self.snapshot_with(None)
        self.assertEqual(cinematic.differences(snapshot, snapshot), [])
        self.assertEqual(cinematic.same(snapshot, snapshot), [])


class ParserShape(unittest.TestCase):
    """The documented invocation must work.

    The docstring says `tools/cinematic.py check [--port 27050]`, but `--port` used
    to be declared only on the top-level parser, so the documented shape was refused
    with "unrecognized arguments".
    """

    def parse(self, argv):
        return cinematic.parser().parse_args(argv)

    def test_the_port_may_follow_the_subcommand(self):
        args = self.parse(["check", "--port", "1234"])
        self.assertEqual(args.command, "check")
        self.assertEqual(args.port, 1234)

    def test_the_port_may_precede_the_subcommand(self):
        args = self.parse(["--port", "1234", "check"])
        self.assertEqual(args.command, "check")
        self.assertEqual(args.port, 1234)

    def test_every_subcommand_takes_a_port(self):
        for argv in (["status"], ["check"], ["shot", "take.txt"], ["screenshot"]):
            args = self.parse(argv + ["--port", "1234"])
            self.assertEqual(args.port, 1234, argv)
            self.assertEqual(self.parse(argv).port, 27050, argv)

    def test_the_documented_shape_is_the_one_the_module_advertises(self):
        doc = cinematic.__doc__
        self.assertIn("tools/cinematic.py check [--port 27050]", doc)


class ScreenshotWait(unittest.TestCase):
    """The capture is written at the end of the frame, so the tool waits for the file."""

    def test_a_file_that_never_arrives_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            missing = os.path.join(folder, "shot.png")
            self.assertFalse(cinematic.wait_for_file(missing, timeout=0.2))

    def test_an_empty_file_is_not_ready(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "shot.png")
            open(path, "wb").close()
            self.assertFalse(cinematic.wait_for_file(path, timeout=0.2))

    def test_a_written_file_is_ready(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "shot.png")
            with open(path, "wb") as handle:
                handle.write(b"\x89PNG\r\n")
            self.assertTrue(cinematic.wait_for_file(path, timeout=1.0))

    def test_a_growing_file_is_not_ready_yet(self):
        # A capture that is still being written keeps changing size, so it is not finished.
        import threading

        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "shot.png")
            stop = threading.Event()

            def grow():
                with open(path, "ab") as handle:
                    while not stop.is_set():
                        handle.write(b"x" * 4096)
                        handle.flush()
                        stop.wait(0.01)

            writer = threading.Thread(target=grow, daemon=True)
            writer.start()
            try:
                self.assertFalse(cinematic.wait_for_file(path, timeout=0.4))
            finally:
                stop.set()
                writer.join(timeout=1.0)

    def test_the_wait_is_bounded(self):
        self.assertLessEqual(cinematic.SCREENSHOT_WAIT_SECONDS, 2.0)
        self.assertGreater(cinematic.SCREENSHOT_WAIT_SECONDS, 0.0)


class ScreenshotCommand(unittest.TestCase):
    """`screenshot` must not report success before the file is there."""

    def run_with(self, reply, files):
        printed = []
        original_print = cinematic.print if hasattr(cinematic, "print") else print

        class FakeBridge:
            def __init__(self, port=27050, timeout=35):
                self.port = port

            def cinema(self, argument=""):
                return reply

            def close(self):
                pass

        waited = []

        def fake_wait(path, **kwargs):
            waited.append(path)
            return files.get(path, False)

        real_bridge = cinematic.Bridge
        real_wait = cinematic.wait_for_file
        real_stdout = sys.stdout
        try:
            cinematic.Bridge = FakeBridge
            cinematic.wait_for_file = fake_wait
            sys.stdout = io.StringIO()
            try:
                code = cinematic.screenshot(argparse.Namespace(port=27050))
            finally:
                out = sys.stdout.getvalue()
                sys.stdout = real_stdout
        finally:
            cinematic.Bridge = real_bridge
            cinematic.wait_for_file = real_wait
        return code, out, waited

    def test_a_missing_file_is_a_failure(self):
        code, out, waited = self.run_with({"path": "/nowhere/shot.png"}, {})
        self.assertEqual(code, 1)
        self.assertIn("did not appear", out)
        self.assertEqual(waited, ["/nowhere/shot.png"])

    def test_an_error_reply_is_a_failure(self):
        code, out, waited = self.run_with({"error": "the screenshot could not be saved"}, {})
        self.assertEqual(code, 1)
        self.assertEqual(waited, [])

    def test_a_reply_without_a_path_is_a_failure(self):
        code, out, waited = self.run_with({"ok": True}, {})
        self.assertEqual(code, 1)
        self.assertEqual(waited, [], "without a path there is nothing to wait for")

    def test_a_ready_file_succeeds(self):
        code, out, waited = self.run_with({"path": "/nowhere/shot.png"}, {"/nowhere/shot.png": True})
        self.assertEqual(code, 0)
        self.assertEqual(waited, ["/nowhere/shot.png"])


import re
# ---- the C# source oracle ----------------------------------------------------------------
#
# The lifecycle source is read with a small lexer of its own, so a hook cannot be faked by a
# comment, by a string literal, or by a line of code that belongs to a different member.
#
# The lexer keeps its state ACROSS LINES: a block comment that opens on one line and closes on
# another is removed as a whole, so the text after its opening delimiter is not code. It knows
# the literal forms of C#: a regular string ("..." with backslash escapes), a verbatim string
# (@"..." with doubled quotes), a raw string ("""...""") and a character literal.
#
# Three views of the same text are produced, all exactly as long as the input, so a region
# found in one of them can be read in another:
#
#   code         - the comments removed and the string CONTENTS blanked, so the braces that are
#                  left are the code's own and the words inside a string are gone
#   with_strings - the comments removed, the literals kept
#   literals     - where each string literal starts, and what it holds
#
# The oracle is a member model on top of that: a member is a header (the declaration, with its
# attributes) and a body (a block, or the expression of an expression-bodied member). A hook
# is a Harmony patch attribute plus the method bodies that belong to it, so the teardown call
# has to be in the hook's OWN body and not merely somewhere in the file.


class CSharpLexer:
    """One C# source, read into the views the oracle works on."""

    def __init__(self, text):
        self.text = text
        self.code, self.with_strings, self.literals, self.mask = _scan_csharp(text)
        self._by_quote = {quote: content for quote, content, _, _ in self.literals}

    def code_lines(self):
        """The source's lines with the comments and the string contents removed."""
        return [line for line in self.code.splitlines() if line.strip()]

    def lines_with_strings(self):
        """The source's lines with the comments removed and the literals kept."""
        return [line for line in self.with_strings.splitlines() if line.strip()]

    def literal_at_quote(self, offset):
        """The text of the string literal whose opening quote is at this offset, or None."""
        return self._by_quote.get(offset)

    def is_literal(self, offset):
        """Whether this offset is inside a string literal (its quotes included)."""
        return 0 <= offset < len(self.mask) and self.mask[offset]


def _scan_csharp(text):
    """Three views of a C# source and the literal table. All views are as long as the input."""
    size = len(text)
    code = list(text)
    with_strings = list(text)
    mask = [False] * size
    literals = []

    def blank(start, stop):
        for k in range(start, stop):
            if code[k] != "\n":
                code[k] = " "
                with_strings[k] = " "

    def mark(start, stop):
        for k in range(start, stop):
            mask[k] = True

    def verbatim_end(start):
        """The offset of the quote that closes a verbatim string whose content starts here."""
        stop = start
        while stop < size:
            if text[stop] == '"':
                if stop + 1 < size and text[stop + 1] == '"':
                    stop += 2
                    continue
                break
            stop += 1
        return min(stop, size - 1)

    def regular_end(start):
        """The offset of the quote that closes a regular string whose content starts here."""
        stop = start
        while stop < size:
            if text[stop] == "\\":
                stop += 2
                continue
            if text[stop] == '"':
                break
            stop += 1
        return min(stop, size - 1)

    i = 0
    in_block = False
    while i < size:
        char = text[i]
        if in_block:
            # A block comment runs until its closing delimiter, whatever lines it spans.
            if char == "*" and i + 1 < size and text[i + 1] == "/":
                blank(i, i + 2)
                i += 2
                in_block = False
                continue
            if char != "\n":
                code[i] = " "
                with_strings[i] = " "
            i += 1
            continue
        if char == "/" and i + 1 < size and text[i + 1] == "/":
            stop = text.find("\n", i)
            if stop < 0:
                stop = size
            blank(i, stop)
            i = stop
            continue
        if char == "/" and i + 1 < size and text[i + 1] == "*":
            blank(i, i + 2)
            i += 2
            in_block = True
            continue
        if char == "@" and i + 1 < size and text[i + 1] == '"':
            quote = i + 1
            content_start = quote + 1
            content_end = verbatim_end(content_start)
            content = text[content_start:content_end].replace('""', '"')
            blank(content_start, content_end)
            mark(quote, content_end)
            literals.append((quote, content, content_start, content_end))
            i = content_end + 1
            continue
        if char == '"' and i + 2 < size and text[i + 1] == '"' and text[i + 2] == '"':
            quote = i
            content_start = quote + 3
            close = text.find('"""', content_start)
            if close < 0:
                close = size
            content = text[content_start:close]
            blank(content_start, close)
            mark(quote, min(close + 3, size))
            literals.append((quote, content, content_start, close))
            i = min(close + 3, size)
            continue
        if char == '"':
            quote = i
            content_start = quote + 1
            content_end = regular_end(content_start)
            content = text[content_start:content_end]
            blank(content_start, content_end)
            mark(quote, content_end)
            literals.append((quote, content, content_start, content_end))
            i = content_end + 1
            continue
        if char == "'":
            stop = i + 1
            while stop < size:
                if text[stop] == "\\":
                    stop += 2
                    continue
                if text[stop] == "'":
                    break
                stop += 1
            stop = min(stop, size - 1)
            blank(i + 1, stop)
            mark(i, stop)
            i = stop + 1
            continue
        i += 1
    return "".join(code), "".join(with_strings), literals, mask


# A method declaration: the modifiers, the type, the name and the parameter list. A field, a
# property or a statement is not one, so it is not mistaken for a method.
_METHOD_DECL = re.compile(
    r"[ \t\r\n]*"
    r"(?P<mods>(?:(?:public|private|protected|internal|static|readonly|const|sealed|override|virtual|abstract"
    r"|extern|partial|new|async|unsafe|event|volatile|explicit|implicit|file)\s+)*)"
    r"(?P<type>[A-Za-z_][A-Za-z0-9_\.<>\[\],\?]*[ \t\r\n]+)"
    r"(?P<name>[A-Za-z_]\w*)"
    r"[ \t\r\n]*(?:<[^<>()]*>[ \t\r\n]*)?\(")

# The Harmony patch attribute that declares a lifecycle hook.
_HARMONY_PATCH = re.compile(
    r"HarmonyPatch\s*\(\s*typeof\s*\(\s*(?P<type>\w+)\s*\)\s*,\s*nameof\s*\(\s*(?P<type2>\w+)\s*\.\s*(?P<member>\w+)\s*\)")


class CSharpMember:
    """One declared member of a C# source: a header and a body."""

    def __init__(self, name, header, kind, body_start, body_end, span_start, span_end):
        self.name = name
        self.header = header
        self.kind = kind
        self.body_start = body_start
        self.body_end = body_end
        self.span_start = span_start
        self.span_end = span_end

    @property
    def is_method(self):
        return self.name is not None

    def body(self, lexer):
        """The text of the member's body, with the string contents blanked."""
        if self.body_start < 0:
            return ""
        return lexer.code[self.body_start:self.body_end]


def _brace_pairs(code):
    """Which brace closes which, over the code with the string contents blanked."""
    stack = []
    pairs = {}
    for index, char in enumerate(code):
        if char == "{":
            stack.append(index)
        elif char == "}" and stack:
            pairs[stack.pop()] = index
    return pairs


def _matching_paren(lexer, open_index, end):
    """The offset of the parenthesis that closes the one at `open_index`."""
    depth = 0
    index = open_index
    while index < end:
        if not lexer.is_literal(index):
            char = lexer.code[index]
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    return index
        index += 1
    return end


def _skip_attributes(lexer, position, end):
    """The offset of the first thing after the attribute lists at `position`."""
    index = position
    while True:
        while index < end and lexer.code[index].isspace():
            index += 1
        if index >= end or lexer.code[index] != "[":
            return index
        depth = 0
        while index < end:
            if not lexer.is_literal(index):
                if lexer.code[index] == "[":
                    depth += 1
                elif lexer.code[index] == "]":
                    depth -= 1
                    if depth == 0:
                        index += 1
                        break
            index += 1


def _expression_end(lexer, start, end):
    """The offset of the semicolon that ends an expression-bodied member."""
    depth = 0
    index = start
    while index < end:
        if not lexer.is_literal(index):
            char = lexer.code[index]
            if char in "([{":
                depth += 1
            elif char in ")]}":
                depth -= 1
            elif char == ";" and depth == 0:
                return index
        index += 1
    return end


def csharp_members(lexer):
    """Every member of the source, the outer ones first.

    A member is a header and a body: a block (`{ ... }`) or an expression (the `=>` of an
    expression-bodied member, up to its semicolon). The list is flat and every member carries
    the offsets of its own body, so a caller can look inside it.
    """
    code = lexer.code
    size = len(code)
    pairs = _brace_pairs(code)
    members = []

    def walk(start, end):
        index = start
        while index < end:
            while index < end and code[index].isspace():
                index += 1
            if index >= end:
                return
            raw = index
            declaration = _skip_attributes(lexer, index, end)
            match = _METHOD_DECL.match(code, declaration)
            if match is not None:
                open_paren = match.end() - 1
                close_paren = _matching_paren(lexer, open_paren, end)
                after = close_paren + 1
                while after < end and code[after].isspace():
                    after += 1
                if after < end and code[after] == "{":
                    body_end = pairs.get(after, end)
                    member = CSharpMember(match.group("name"), code[raw:after + 1], "block",
                                          after + 1, body_end, raw, body_end + 1)
                    index = body_end + 1
                elif code.startswith("=>", after):
                    semi = _expression_end(lexer, after + 2, end)
                    member = CSharpMember(match.group("name"), code[raw:after + 2], "expression",
                                          after + 2, semi, raw, semi + 1)
                    index = semi + 1
                else:
                    member = CSharpMember(match.group("name"), code[raw:after], "none",
                                          -1, -1, raw, after + 1)
                    index = after + 1
                members.append(member)
                continue
            # Not a method: a type (a block) or a field or property (a statement).
            scan = declaration
            while scan < end and code[scan] not in "{;":
                scan += 1
            if scan >= end:
                return
            if code[scan] == ";":
                members.append(CSharpMember(None, code[raw:scan], "statement", -1, -1, raw, scan + 1))
                index = scan + 1
            else:
                body_end = pairs.get(scan, end)
                members.append(CSharpMember(None, code[raw:scan], "block", scan + 1, body_end, raw, body_end + 1))
                walk(scan + 1, body_end)
                index = body_end + 1

    walk(0, size)
    return members


# The four hooks the camera needs, and the reason each one gives the teardown.
LIFECYCLE_HOOKS = (
    ("fpscontroller", "Death", "death"),
    ("fpscontroller", "Respawn", "respawn"),
    ("menuhandler", "LoadScene", "scene load"),
    ("mainscript", "PressedEscape", "main menu"),
)


def _declaration(members, type_name, member_name):
    """The member that the Harmony patch attribute declares."""
    pattern = re.compile(
        r"HarmonyPatch\s*\(\s*typeof\s*\(\s*" + re.escape(type_name) +
        r"\s*\)\s*,\s*nameof\s*\(\s*" + re.escape(type_name) +
        r"\s*\.\s*" + re.escape(member_name) + r"\s*\)")
    for member in members:
        if pattern.search(member.header):
            return member
    return None


def _hook_bodies(members, declaration):
    """Return only the direct Harmony patch methods owned by a declaration."""
    if declaration is None:
        return []

    def is_patch_method(member):
        if not member.is_method:
            return False
        if member.name in ("Prefix", "Postfix"):
            return True
        header = member.header or ""
        return "[HarmonyPrefix]" in header or "[HarmonyPostfix]" in header

    if declaration.is_method:
        return [declaration] if is_patch_method(declaration) else []

    candidates = []
    for member in members:
        if not is_patch_method(member):
            continue
        if member.span_start < declaration.body_start or member.span_end > declaration.body_end:
            continue

        owned = True
        for container in members:
            if container.is_method or container.body_start < 0:
                continue
            if container is declaration:
                continue
            if container.body_start <= declaration.body_start:
                continue
            if (container.body_start <= member.span_start
                    and member.span_end <= container.body_end):
                owned = False
                break

        if owned:
            candidates.append(member)

    return candidates



def _calls_forced(lexer, member, reason):
    """Whether the member's body calls `Forced("<reason>")`.

    The call is looked for in the code view, so a `Forced("death")` that is only a string
    literal is invisible, and the argument is read from the literal table, so the reason
    compared is the literal's own text.
    """
    if member.body_start < 0:
        return False
    region = lexer.code[member.body_start:member.body_end]
    for match in re.finditer(r"\bForced\s*\(", region):
        offset = member.body_start + match.end()
        while offset < member.body_end and lexer.code[offset].isspace():
            offset += 1
        while offset < member.body_end and lexer.code[offset] in "@$":
            offset += 1
        literal = lexer.literal_at_quote(offset)
        if literal is not None and literal == reason:
            return True
    return False


def lifecycle_failures(source_text):
    """The ways a lifecycle source fails the hook oracle. An empty list means it passes.

    The oracle asks three things of the source:

      * each of the four events has its own Harmony patch declaration;
      * the teardown that belongs to that event - `Forced("<reason>")` - is called in the body
        of that very hook, not in some other member of the file;
      * the shared teardown itself only runs when the camera is on, and it does turn the
        camera off.
    """
    lexer = CSharpLexer(source_text)
    members = csharp_members(lexer)
    failures = []

    for type_name, member_name, reason in LIFECYCLE_HOOKS:
        declaration = _declaration(members, type_name, member_name)
        if declaration is None:
            failures.append("no hook for " + type_name + "." + member_name +
                            " (the Harmony patch declaration is missing)")
            continue
        bodies = _hook_bodies(members, declaration)
        if not bodies:
            failures.append("the " + reason + " hook has no method body of its own")
            continue
        if not any(_calls_forced(lexer, body, reason) for body in bodies):
            failures.append("the " + reason + " hook does not call Forced(\"" + reason + "\")")

    # The escape hook only ends the world when it really is the main menu.
    declaration = _declaration(members, "mainscript", "PressedEscape")
    if declaration is not None:
        bodies = _hook_bodies(members, declaration)
        if bodies and not any(re.search(r"!\s*_mainMenu", body.body(lexer)) for body in bodies):
            failures.append("the main menu hook does not check _mainMenu")

    # The shared teardown: it only runs when the camera is on, and it turns it off.
    shared = [member for member in members if member.is_method and member.name == "Forced"]
    if not shared:
        failures.append("the shared teardown Forced(string) is missing")
    else:
        body = shared[0].body(lexer)
        if not re.search(r"if\s*\(\s*!\s*CinematicCamera\s*\.\s*Active\s*\)\s*return\s*;", body):
            failures.append("the shared teardown does nothing when the camera is off")
        if not re.search(r"CinematicCamera\s*\.\s*SetOff\s*\(", body):
            failures.append("the shared teardown does not turn the camera off")

    return failures



class SourceGuards(unittest.TestCase):
    """Guards over the production source, so a plain Python run can see a removed guard.

    The C# isolation test covers the same ground (and CI runs it), but the Python
    suite is the one that runs everywhere, so the properties are checked here too:
    the four lifecycle hooks exist, and the field of view is clamped at every site
    that sets it.

    The source is read with the lexer above, which keeps its block-comment state across
    lines, so a fake hook hidden in a `/* ... */` comment cannot satisfy a check, and the
    hook checks are scoped to a hook's OWN body, so a `Forced("death")` that belongs to
    some other member cannot stand in for the death hook's teardown.
    """

    def source(self, relative):
        return (ROOT / relative).read_text(encoding="utf-8")

    def lexer(self, relative):
        """The source read by the lexer: comments removed, string contents blanked."""
        return CSharpLexer(self.source(relative))

    def code_lines(self, relative):
        """The source's lines with the comments and the string literals removed."""
        return self.lexer(relative).code_lines()

    def lines_with_strings(self, relative):
        """The source's lines with the comments removed and the strings kept."""
        return self.lexer(relative).lines_with_strings()

    LIFECYCLE = "src/TLDRevamp/Cinematic/CinematicLifecycle.cs"
    CAMERA = "src/TLDRevamp/Cinematic/CinematicCamera.cs"
    BRIDGE = "src/TLDRevamp/Cinematic/CinematicBridge.cs"
    CORE = "src/TLDRevamp/Cinematic/CinematicCameraCore.cs"
    RIG = "src/TLDRevamp/Cinematic/CinematicRig.cs"

    def test_the_four_lifecycle_hooks_exist(self):
        failures = lifecycle_failures(self.source(self.LIFECYCLE))
        self.assertEqual(failures, [], "the lifecycle source fails the hook oracle: " + "; ".join(failures))

    def test_each_hook_owns_its_own_forced_teardown(self):
        # The teardown that belongs to an event must be in THAT event's hook body. A
        # `Forced("death")` that lives in another member does not put the camera away when
        # the player dies, so the oracle scopes each check to the hook's own body.
        source = self.source(self.LIFECYCLE)
        lexer = CSharpLexer(source)
        members = csharp_members(lexer)
        for type_name, member_name, reason in LIFECYCLE_HOOKS:
            declaration = _declaration(members, type_name, member_name)
            self.assertIsNotNone(declaration, f"the camera must have a hook for {reason}")
            bodies = _hook_bodies(members, declaration)
            self.assertTrue(bodies, f"the {reason} hook must have a method body of its own")
            self.assertTrue(any(_calls_forced(lexer, body, reason) for body in bodies),
                            f"the {reason} hook must call Forced(\"{reason}\") in its own body")

    def test_the_forced_teardown_turns_the_camera_off(self):
        # The shared teardown is where the camera is actually put away, so the two things
        # that make it safe live there: it does nothing when the camera is off, and it turns
        # the camera off when it is on.
        source = self.source(self.LIFECYCLE)
        lexer = CSharpLexer(source)
        shared = [m for m in csharp_members(lexer) if m.is_method and m.name == "Forced"]
        self.assertTrue(shared, "the shared teardown Forced(string) must exist")
        body = shared[0].body(lexer)
        self.assertRegex(body, r"if\s*\(\s*!\s*CinematicCamera\s*\.\s*Active\s*\)\s*return\s*;",
                          "the forced teardown does nothing when the camera is off")
        self.assertRegex(body, r"CinematicCamera\s*\.\s*SetOff\s*\(",
                          "the forced teardown turns the camera off")

    def test_the_field_of_view_is_clamped_at_every_site_that_sets_it(self):
        camera = "\n".join(self.code_lines(self.CAMERA))
        bridge = "\n".join(self.code_lines(self.BRIDGE))
        core = self.source(self.CORE)
        self.assertRegex(camera, r"set\s*=>\s*State\.Fov\s*=\s*CinematicLimits\.ClampFov\s*\(",
                         "the camera's Fov property must clamp")
        self.assertRegex(bridge, r"State\.Fov\s*=\s*CinematicLimits\.ClampFov\s*\(",
                         "the bridge must clamp the field of view")
        self.assertIn("Fov = CinematicLimits.ClampFov(Fov)", core,
                      "the camera state must normalise its own field of view")

    def test_no_gameplay_camera_is_ever_re_foved(self):
        for relative in (self.CAMERA, self.BRIDGE, self.RIG, self.CORE):
            for line in self.code_lines(relative):
                self.assertNotRegex(line, r"\.\s*(Cam|mainCam|farCamera|skyCamera|saveScreenshotCam)\s*\.\s*fieldOfView\s*=",
                                    f"{relative}: the camera must not re-FOV a gameplay camera")

    def test_the_rig_only_writes_its_own_camera_field_of_view(self):
        rig = "\n".join(self.code_lines(self.RIG))
        self.assertRegex(rig, r"_camera\.fieldOfView\s*=\s*Fov")
        self.assertRegex(rig, r"cam\.fieldOfView\s*=\s*Fov")

    def test_the_camera_never_sets_the_scene_teardown_flag(self):
        # The scene-teardown flag is a READ. The check that says so must look at the
        # code of every line, not at a whole-file strip that stops at the first comment.
        for line in self.code_lines(self.CAMERA):
            self.assertNotRegex(line, r"SceneTearingDown\s*=",
                                "the camera must never set the scene-teardown flag")

    def test_the_rig_test_is_in_this_tree(self):
        for name in ("CinematicRigIsolationTest.cs", "CinematicRigIsolationTest.csproj", "CinematicRigStubs.cs"):
            self.assertTrue((ROOT / "tools/unit" / name).is_file(),
                            f"the runtime rig test {name} must be in the tree so CI runs it")


class LifecycleSourceOracle(unittest.TestCase):
    """The lifecycle source oracle, driven against synthetic sources.

    The oracle is what proves the camera puts itself away when the game takes the world
    away. It has to be strict about two things that a whole-file search cannot see:

      * a hook that only exists inside a comment is not a hook;
      * a teardown that belongs to some other member is not this hook's teardown.

    The synthetic sources below are the mutants of the real one: each takes the real
    lifecycle source and breaks it in one way, and the oracle has to notice.
    """

    LIFECYCLE = "src/TLDRevamp/Cinematic/CinematicLifecycle.cs"

    def source(self):
        return (ROOT / self.LIFECYCLE).read_text(encoding="utf-8")

    def failures(self, text):
        return lifecycle_failures(text)

    def test_lifecycle_convention_detection(self):
        source = self.source()
        original = 'private static void Prefix() => Forced("death");'

        negative1 = 'private static void Prefix() { }\nprivate static void UnrelatedHelper() => Forced("death");'
        self.assertIn(original, source)
        source1 = source.replace(original, negative1)
        self.assertIn(negative1, source1)
        failures1 = self.failures(source1)
        self.assertTrue(failures1)
        self.assertTrue(any('death' in failure for failure in failures1))

        negative2 = 'private static void Prefix() { }\nprivate static class Nested { private static void Prefix() => Forced("death"); }'
        source2 = source.replace(original, negative2)
        self.assertIn(negative2, source2)
        failures2 = self.failures(source2)
        self.assertTrue(failures2)
        self.assertTrue(any('death' in failure for failure in failures2))

        negative3 = 'private static void Prefix() { }\nprivate static void PrefixUnused() => Forced("death");'
        source3 = source.replace(original, negative3)
        self.assertIn(negative3, source3)
        failures3 = self.failures(source3)
        self.assertTrue(failures3)
        self.assertTrue(any('death' in failure for failure in failures3))

        positive = source.replace('[HarmonyPrefix]', '')
        self.assertNotEqual(source, positive)
        positive_failures = self.failures(positive)
        self.assertEqual([], positive_failures)

    def test_the_real_source_passes(self):
        self.assertEqual(self.failures(self.source()), [])

    # --- the lexer: comments are not code ------------------------------------------------

    def test_a_block_comment_that_spans_lines_is_removed_as_a_whole(self):
        # The old strip stopped at the first "/*", so everything after it on that line, and
        # every line after it, was treated as code.
        source = ("class X\n"
                  "{\n"
                  "    /*\n"
                  "    HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))\n"
                  "    Forced(\"death\")\n"
                  "    */\n"
                  "    void Real() { }\n"
                  "}\n")
        lines = CSharpLexer(source).code_lines()
        joined = "\n".join(lines)
        self.assertNotIn("HarmonyPatch", joined)
        self.assertNotIn("Forced", joined)
        self.assertIn("void Real()", joined)

    def test_a_line_comment_does_not_leak(self):
        source = "class X { void A() { } // HarmonyPatch(typeof(fpscontroller))\n }\n"
        joined = "\n".join(CSharpLexer(source).code_lines())
        self.assertNotIn("HarmonyPatch", joined)

    def test_an_escaped_quote_does_not_end_a_string(self):
        # A string that holds an escaped quote must not let the rest of the line be read
        # as code, and a comment delimiter inside a string is not a comment.
        source = 'class X { string a = "he said \\"hi\\" /* not a comment */"; void B() { } }\n'
        lines = CSharpLexer(source).code_lines()
        joined = "\n".join(lines)
        self.assertNotIn("not a comment", joined)
        self.assertIn("void B()", joined)

    def test_a_verbatim_string_does_not_end_at_a_quote(self):
        source = 'class X { string b = @"raw ""quoted"" // not a comment"; void C() { } }\n'
        joined = "\n".join(CSharpLexer(source).code_lines())
        self.assertNotIn("not a comment", joined)
        self.assertIn("void C()", joined)

    def test_a_comment_marker_inside_a_string_is_not_a_comment(self):
        # The "//" inside the literal must not start a comment, so the code AFTER the
        # literal on the same line must still be there.
        source = 'class X { string d = "a // b"; void D() { } }\n'
        joined = "\n".join(CSharpLexer(source).code_lines())
        self.assertIn("void D()", joined, "the code after a string holding // must survive")

    # --- the mutants of the real source ---------------------------------------------------

    REAL_DEATH_HOOK = """        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]
        private static class OnDeath
        {
            [HarmonyPrefix]
            private static void Prefix() => Forced("death");
        }"""

    def test_a_fake_hook_in_a_multiline_comment_does_not_count(self):
        # The hook is moved into a block comment: the file no longer hooks the death.
        source = self.source().replace(self.REAL_DEATH_HOOK,
                                       "        /*\n" + self.REAL_DEATH_HOOK + "\n        */")
        failures = self.failures(source)
        self.assertTrue(failures, "a hook that only exists inside a comment is not a hook")
        self.assertTrue(any("fpscontroller.Death" in failure for failure in failures), failures)

    def test_a_fake_hook_in_a_line_comment_does_not_count(self):
        source = self.source().replace(
            self.REAL_DEATH_HOOK,
            "        // " + self.REAL_DEATH_HOOK.replace("\n", " ").replace("        ", " "))
        failures = self.failures(source)
        self.assertTrue(failures, "a hook that only exists inside a comment is not a hook")

    def test_a_teardown_moved_to_another_method_does_not_count(self):
        # The hook no longer puts the camera away; an unrelated method does instead. The
        # literal moves with it, so a whole-file search for Forced("death") would still
        # find it: only a hook-scoped search can tell that it belongs to another member.
        source = self.source().replace(
            'private static void Prefix() => Forced("death");',
            'private static void Prefix() => SomewhereElse();')
        source = source.replace(
            "        /// The player died",
            '        private static void SomewhereElse() { Forced("death"); }\n\n'
            "        /// The player died")
        failures = self.failures(source)
        self.assertTrue(failures, "a teardown in another method is not this hook's teardown")
        self.assertTrue(any("death" in failure for failure in failures), failures)

    def test_each_missing_hook_body_call_is_caught(self):
        for reason in ("death", "respawn", "scene load", "main menu"):
            source = self.source().replace('Forced("%s")' % reason, "SomethingElse()")
            failures = self.failures(source)
            self.assertTrue(failures, "the %s hook must call its own teardown" % reason)
            self.assertTrue(any(reason in failure for failure in failures),
                            "the %s failure must name the hook: %s" % (reason, failures))

    def test_a_removed_hook_declaration_is_caught(self):
        source = self.source().replace(
            "[HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]",
            "[HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Update))]")
        failures = self.failures(source)
        self.assertTrue(failures, "a hook on another method is not the death hook")

    def test_a_forced_call_in_a_string_is_not_a_teardown(self):
        # A message that mentions the call is not the call.
        source = self.source().replace(
            'private static void Prefix() => Forced("death");',
            'private static void Prefix() { Note("Forced(\\"death\\")"); }')
        failures = self.failures(source)
        self.assertTrue(failures, "a Forced call inside a string is not a teardown")

    def test_the_shared_teardown_must_turn_the_camera_off(self):
        source = self.source().replace("CinematicCamera.SetOff(why);", "/* nothing */")
        failures = self.failures(source)
        self.assertTrue(failures, "the shared teardown must turn the camera off")

    def test_the_shared_teardown_must_do_nothing_when_the_camera_is_off(self):
        source = self.source().replace("if (!CinematicCamera.Active) return;", "/* always */")
        failures = self.failures(source)
        self.assertTrue(failures, "the shared teardown must do nothing when the camera is off")

    def test_the_main_menu_hook_must_check_the_flag(self):
        source = self.source().replace("if (!_mainMenu) return;", "/* always */")
        failures = self.failures(source)
        self.assertTrue(failures, "the main menu hook must check _mainMenu")


class TheWholeCheck(unittest.TestCase):
    """The whole `check` routine, driven against a mock bridge.

    No game, no socket: the mock answers the same commands the mod answers. This is
    the only place where the comparison, the readability gate and the pass/fail
    accounting are exercised together.
    """

    VALUES = {
        "mainscript.s.player.Cam.transform.position": "1.00,2.00,3.00",
        "mainscript.s.player.Cam.transform.rotation": "(0.00000, 0.00000, 0.00000, 1.00000)",
        "mainscript.s.player.Cam.fieldOfView": 60.0,
        "mainscript.s.player.Cam.enabled": True,
        "mainscript.s.player.mainCam.transform.position": "1.00,2.00,3.00",
        "mainscript.s.player.mainCam.fieldOfView": 60.0,
        "mainscript.s.player.mainCam.enabled": True,
        "mainscript.s.player.farCamera.transform.position": "1.00,2.00,3.00",
        "mainscript.s.player.farCamera.fieldOfView": 60.0,
        "mainscript.s.player.farCamera.enabled": True,
        "mainscript.s.player.skyCamera.enabled": True,
        "mainscript.s.player.saveScreenshotCam.fieldOfView": 60.0,
        "mainscript.s.player.transform.position": "1.00,2.00,3.00",
        "mainscript.s.player.transform.rotation": "(0.00000, 0.00000, 0.00000, 1.00000)",
        "mainscript.s.player.BodyRot.rotation": "(0.00000, 0.00000, 0.00000, 1.00000)",
    }

    class Mock:
        """Answers like the mod, and changes one value while the camera is on."""

        def __init__(self, during=None, unread=(), malformed=None):
            self.during = during or {}
            self.unread = set(unread)
            # A field whose answer is not a JSON object at all: the bridge is broken.
            self.malformed = dict(malformed or {})
            self.phase = "before"

        def cinema(self, argument=""):
            if argument == "status":
                return {"mode": "off", "active": False, "position": [1, 2, 3], "target": [1, 3, 3], "radius": 1.0}
            if argument == "free":
                self.phase = "during"
                return {"mode": "free", "active": True}
            if argument == "orbit":
                return {"mode": "orbit", "active": True}
            if argument == "off":
                self.phase = "after"
                return {"active": False, "cursorOwned": False}
            if argument == "speed 99999":
                return {"speed": 60.0}
            if argument == "speed 0.01":
                return {"speed": 0.25}
            if argument == "fov 500":
                return {"fov": 120.0}
            if argument == "fov 1":
                return {"fov": 10.0}
            if argument == "radius 100000":
                return {"radius": 400.0}
            if argument == "radius 0":
                return {"radius": 1.0}
            if argument == "shot":
                return {"loaded": False}
            return {"error": "unknown"}

        def try_call(self, command):
            path = command[4:] if command.startswith("get ") else command
            if path in self.unread:
                return {"error": "no such member"}
            if path in self.malformed:
                return self.malformed[path]
            value = TheWholeCheck.VALUES[path]
            if self.phase == "during" and path in self.during:
                value = self.during[path]
            return {"value": value}

        def close(self):
            pass

    def run_check(self, mock):
        real_bridge = cinematic.Bridge
        real_stdout = sys.stdout
        cinematic.Bridge = lambda port, timeout=35: mock
        sys.stdout = io.StringIO()
        try:
            code = cinematic.check(argparse.Namespace(port=27050))
        finally:
            out = sys.stdout.getvalue()
            sys.stdout = real_stdout
            cinematic.Bridge = real_bridge
        return code, out

    def fails(self, out):
        return [line for line in out.splitlines() if line.startswith("FAIL")]

    def test_a_clean_game_passes(self):
        code, out = self.run_check(self.Mock())
        self.assertEqual(code, 0, out)
        self.assertEqual(self.fails(out), [])

    def test_an_idle_sway_passes(self):
        # The player's own character shuffles on the spot: 2 cm and 0.4 degrees.
        mock = self.Mock(during={
            "mainscript.s.player.transform.position": "1.02,2.00,3.00",
            "mainscript.s.player.BodyRot.rotation": "(0.00000, 0.00350, 0.00000, 0.99999)",
        })
        code, out = self.run_check(mock)
        self.assertEqual(code, 0, out)
        self.assertEqual(self.fails(out), [])

    def test_a_changed_field_of_view_fails(self):
        mock = self.Mock(during={"mainscript.s.player.Cam.fieldOfView": 75.0})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("cam_fov" in line for line in self.fails(out)), out)

    def test_a_disabled_gameplay_camera_fails(self):
        mock = self.Mock(during={"mainscript.s.player.Cam.enabled": False})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("cam_enabled" in line for line in self.fails(out)), out)

    def test_a_moved_gameplay_camera_fails(self):
        mock = self.Mock(during={"mainscript.s.player.Cam.transform.position": "4.00,2.00,3.00"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("cam_pos" in line for line in self.fails(out)), out)

    def test_a_moved_player_fails(self):
        mock = self.Mock(during={"mainscript.s.player.transform.position": "1.50,2.00,3.00"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("player_pos" in line for line in self.fails(out)), out)

    def test_a_turned_body_fails(self):
        mock = self.Mock(during={
            "mainscript.s.player.BodyRot.rotation": "(0.00000, 0.70711, 0.00000, 0.70711)"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("body_rot" in line for line in self.fails(out)), out)

    def test_an_unreadable_value_fails(self):
        mock = self.Mock(unread=("mainscript.s.player.Cam.fieldOfView",))
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("can be read" in line for line in self.fails(out)), out)

    def test_a_position_that_is_a_single_number_fails(self):
        # before the repair this passed: the value held one number, so it looked readable and unchanged
        mock = self.Mock(during={"mainscript.s.player.Cam.transform.position": "7"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_pos" in line
                            for line in self.fails(out)), out)

    def test_a_position_with_a_nan_in_it_fails(self):
        mock = self.Mock(during={"mainscript.s.player.transform.position": [float("nan"), 0.0, 0.0]})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "player_pos" in line
                            for line in self.fails(out)), out)

    def test_a_rotation_with_five_numbers_fails_instead_of_crashing(self):
        # before the repair this raised "ValueError: too many values to unpack (expected 4, got 5)"
        mock = self.Mock(during={"mainscript.s.player.Cam.transform.rotation":
                                 "(0.10000, 0.20000, 0.30000, 0.40000, 0.50000)"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_rot" in line
                            for line in self.fails(out)), out)

    def test_a_zero_quaternion_fails(self):
        mock = self.Mock(during={"mainscript.s.player.BodyRot.rotation":
                                 "(0.00000, 0.00000, 0.00000, 0.00000)"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "body_rot" in line
                            for line in self.fails(out)), out)

    def test_a_null_reply_fails_instead_of_crashing(self):
        # before the repair this raised "TypeError: argument of type 'NoneType' is not iterable"
        mock = self.Mock(malformed={"mainscript.s.player.Cam.fieldOfView": None})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_fov" in line
                            for line in self.fails(out)), out)

    def test_a_list_reply_fails_instead_of_crashing(self):
        # before the repair this raised "TypeError: argument of type 'list' is not iterable"
        mock = self.Mock(malformed={"mainscript.s.player.Cam.transform.position": [1.0, 2.0, 3.0]})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_pos" in line
                            for line in self.fails(out)), out)

    def test_a_string_reply_fails_instead_of_crashing(self):
        # before the repair this raised "AttributeError: 'str' object has no attribute 'get'"
        mock = self.Mock(malformed={"mainscript.s.player.Cam.transform.position": "1.00,2.00,3.00"})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_pos" in line
                            for line in self.fails(out)), out)

    def test_a_scalar_reply_fails_instead_of_crashing(self):
        # before the repair this raised "TypeError: argument of type 'int' is not iterable"
        mock = self.Mock(malformed={"mainscript.s.player.Cam.fieldOfView": 60.0})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("read" in line and "cam_fov" in line
                            for line in self.fails(out)), out)

    def test_a_malformed_reply_names_the_field(self):
        mock = self.Mock(malformed={"mainscript.s.player.BodyRot.rotation": None})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertTrue(any("body_rot" in line for line in self.fails(out)), out)

    def test_a_malformed_reply_is_not_a_difference(self):
        # An unreadable field is reported by the readability gate, not as a movement.
        mock = self.Mock(malformed={"mainscript.s.player.Cam.transform.position": None})
        code, out = self.run_check(mock)
        self.assertEqual(code, 1)
        self.assertFalse(any("moved" in line for line in self.fails(out)), out)



if __name__ == "__main__":
    unittest.main()
