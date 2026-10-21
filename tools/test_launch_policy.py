#!/usr/bin/env python3
"""Offline tests of the launch policy: a test copy is only ever launched hidden, muted and low-load.

The policy (the launch-policy violation record in .codex-handoff/TEST-MACHINES.md and
context/BACKGROUND-LAUNCH-MANDATORY.md): a test copy is launched only through the guarded path, with its
own game directory, Proton prefix and explicit bridge port, into Hyprland's hidden `special:tld-tests`
workspace, silent and with no initial focus, `bg on`, vsync off, the game's own FPS limiter on, and the
game-only mute. Graphics: regular on the desktop (the player's settings, 60 FPS), low on the second machine or
with TLD_TEST_GRAPHICS=low (the lowest quality, effects off, 30 FPS). A launch that cannot be proven hidden must FAIL CLOSED, never fall
back to an ordinary visible launch.

No game is launched here: the tests read the source and drive the pure functions with fakes.

  python3 -m unittest discover -s tools -p 'test_launch_policy.py' -v
"""
import os, pathlib, re, sys, unittest, unittest.mock

TOOLS = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))

import testdisplay  # noqa: E402


def setUpModule():
    # The tests below pin the low-graphics profile (quality 0, effects off, 30 FPS); RegularGraphicsTests covers the
    # desktop's default, regular graphics.
    global _graphics
    _graphics = os.environ.get("TLD_TEST_GRAPHICS")
    os.environ["TLD_TEST_GRAPHICS"] = "low"


def tearDownModule():
    if _graphics is None:
        os.environ.pop("TLD_TEST_GRAPHICS", None)
    else:
        os.environ["TLD_TEST_GRAPHICS"] = _graphics


class RegularGraphicsTests(unittest.TestCase):
    """The desktop's default: the graphics a player plays with, the 60 FPS cap, still hidden and muted."""

    def setUp(self):
        self.saved = os.environ.pop("TLD_TEST_GRAPHICS", None)
        self.addCleanup(lambda: os.environ.__setitem__("TLD_TEST_GRAPHICS", self.saved) if self.saved is not None else None)

    def test_the_desktop_defaults_to_regular_and_the_second_machine_to_low(self):
        self.assertEqual(testdisplay.graphics_mode("desktop"), "regular")
        self.assertEqual(testdisplay.graphics_mode("remote"), "low")
        with unittest.mock.patch.dict(os.environ, {"TLD_TEST_GRAPHICS": "low"}):
            self.assertEqual(testdisplay.graphics_mode("desktop"), "low")
        with self.assertRaises(ValueError):
            testdisplay.graphics_mode("desktop", "ultra")

    def test_regular_graphics_are_the_players_settings(self):
        s = testdisplay.settings_for("desktop")
        for key in ("BEnableShadows", "BMirrors", "BPostProcessing", "BGrass", "BAntialiasing", "BFog", "BSkyObjs"):
            self.assertTrue(s[key], key)
        self.assertEqual(s["FDrawDistance"], 15000.0)
        self.assertTrue(s["BEnableFpsLimit"])
        self.assertEqual(s["FFPSLimit"], 60)
        self.assertFalse(s["BVsync"])
        self.assertEqual(s["FSound"], 0.0)

    def test_regular_graphics_leave_the_quality_level_alone(self):
        calls = []
        testdisplay.configure(lambda c: calls.append(c) or {"value": True}, "desktop", strict=False)
        self.assertFalse([c for c in calls if c.startswith("quality")], calls)
        self.assertIn("set UnityEngine.Application.targetFrameRate 60", calls)
        state = {"width": 1280, "height": 720, "fullscreen": False, "quality": 5, "vsync": 0, "game_vsync": False,
                 "fps_limit_enabled": True, "fps_limit": 60, "target_fps": 60, "run_in_background": True,
                 "audio_listener": 0, "sound": 0}
        self.assertIs(testdisplay.check(state, "desktop"), state)

    def test_a_lower_cap_is_allowed_a_higher_one_is_not(self):
        testdisplay.validate_command("set settingsscript.s.S.FFPSLimit 30", "desktop")
        testdisplay.validate_command("set settingsscript.s.S.FFPSLimit 60", "desktop")
        with self.assertRaises(RuntimeError):
            testdisplay.validate_command("set settingsscript.s.S.FFPSLimit 144", "desktop")
        with self.assertRaises(RuntimeError):
            testdisplay.validate_command("set settingsscript.s.S.FSound 1", "desktop")


class ProfileTests(unittest.TestCase):
    """The profile the copy is launched with."""

    def test_the_desktop_copy_is_1280x720_windowed(self):
        s = testdisplay.settings_for("desktop")
        self.assertEqual(s["IResolutionX"], 1280)
        self.assertEqual(s["IResolutionY"], 720)
        self.assertEqual(s["IFullscreen"], 3)   # windowed

    def test_the_second_machine_copy_is_800x450_windowed(self):
        s = testdisplay.settings_for("remote")
        self.assertEqual(s["IResolutionX"], 800)
        self.assertEqual(s["IResolutionY"], 450)
        self.assertEqual(s["IFullscreen"], 3)

    def test_vsync_is_off(self):
        self.assertFalse(testdisplay.settings_for("desktop")["BVsync"])

    def test_the_game_fps_limiter_is_enabled_at_30(self):
        s = testdisplay.settings_for("desktop")
        self.assertTrue(s["BEnableFpsLimit"])
        self.assertEqual(s["FFPSLimit"], 30)

    def test_the_game_is_muted_and_not_the_desktop(self):
        self.assertEqual(testdisplay.settings_for("desktop")["FSound"], 0.0)
        # the mute is the game's own: no desktop-wide audio command anywhere in the launcher
        source = (TOOLS / "testdisplay.py").read_text(encoding="utf-8")
        for command in ("wpctl", "pactl", "pulseaudio", "amixer"):
            self.assertNotIn(command, source, "the launcher must not touch the desktop's audio")

    def test_the_costly_graphics_are_off(self):
        for key in ("BEnableShadows", "BMirrors", "BPostProcessing", "BDof", "BGrass", "BAntialiasing"):
            self.assertFalse(testdisplay.settings_for("desktop")[key], key)

    def test_the_launch_rules_place_the_window_in_the_hidden_workspace(self):
        for name in ("desktop", "remote"):
            rules = testdisplay.launch_rules(name)
            self.assertIn("special:tld-tests", rules)
            self.assertIn("silent", rules)
            self.assertIn("no_initial_focus", rules)
            self.assertIn("suppress_event fullscreen maximize", rules)
            self.assertIn("float", rules)


class CommandGuardTests(unittest.TestCase):
    """A test or an old journal may not undo the mandatory profile."""

    def test_the_profile_commands_are_accepted(self):
        for command in ("set settingsscript.s.S.BEnableFpsLimit True",
                         "set settingsscript.s.S.FFPSLimit 30",
                         "quality 0",
                         "set UnityEngine.QualitySettings.vSyncCount 0",
                         "set UnityEngine.AudioListener.volume 0",
                         "set settingsscript.s.S.FSound 0",
                         "set UnityEngine.Screen.fullScreen False"):
            testdisplay.validate_command(command)

    def test_the_profile_commands_may_not_be_undone(self):
        for command in ("set UnityEngine.QualitySettings.vSyncCount 1",
                         "set settingsscript.s.S.BEnableFpsLimit False",
                         "set settingsscript.s.S.FFPSLimit 60",
                         "set UnityEngine.AudioListener.volume 1",
                         "set settingsscript.s.S.FSound 1",
                         "set UnityEngine.Screen.fullScreen True",
                         "quality 1"):
            with self.subTest(command=command):
                with self.assertRaises(RuntimeError):
                    testdisplay.validate_command(command)

    def test_a_multi_line_command_is_refused(self):
        with self.assertRaises(RuntimeError):
            testdisplay.validate_command("ping\nset settingsscript.s.S.FSound 1")


class SourceGuardTests(unittest.TestCase):
    """The forbidden launch mechanisms must not come back."""

    def setUp(self):
        self.game = (TOOLS / "game.py").read_text(encoding="utf-8")
        self.display = (TOOLS / "testdisplay.py").read_text(encoding="utf-8")

    def test_the_launch_goes_through_the_guarded_path(self):
        # the compositor rules are built by testdisplay and applied by the guard
        self.assertIn("def background_command", self.display)
        self.assertIn("hyprctl dispatch exec", self.display)
        self.assertIn("launchguard", self.game)

    def test_there_is_no_steam_applaunch_left(self):
        self.assertNotIn("steam -applaunch", self.game)

    def test_there_is_no_raw_proton_or_setsid_launch(self):
        # the Proton command is built by the guard, not by an ad hoc script
        self.assertNotIn("setsid", self.game)
        for name in ("sd_launch.sh", "launch.sh", "proton.sh"):
            self.assertFalse((TOOLS / name).exists(), name + " must not be in the tools")

    def test_a_launch_without_a_compositor_is_refused(self):
        # the guard raises instead of falling back: there is no "return the ordinary command" branch
        self.assertNotRegex(self.display, r"def background_command[\s\S]{0,600}?\n\s*return command\b")
        self.assertIn("preflight(env)", self.display)

    def test_the_bridge_verifies_the_data_path_before_a_mutation(self):
        bench = (TOOLS / "bench.py").read_text(encoding="utf-8")
        self.assertIn("_verify_data_path", bench)
        # it is called before the command is sent
        self.assertRegex(bench, r"def __call__[\s\S]{0,600}?_verify_data_path\(\)[\s\S]{0,120}?return self\._raw\(cmd\)")

    def test_the_kill_is_scoped_to_the_lease(self):
        self.assertNotIn("pkill", self.game)
        self.assertIn("kill_prefix", self.game)


class TestLaunchPolicy(unittest.TestCase):
    def test_unity_focus_is_not_authoritative(self):
        from copy import deepcopy
        from testdisplay import check, profile

        pr = profile("desktop")
        base = {
            "width": pr["width"],
            "height": pr["height"],
            "fullscreen": False,
            "quality": 0,
            "vsync": 0,
            "game_vsync": False,
            "fps_limit_enabled": True,
            "fps_limit": 30,
            "target_fps": 30,
            "run_in_background": True,
            "audio_listener": 0,
            "sound": 0,
        }
        for focused in (True, False, None):
            with self.subTest(focused=focused):
                state = deepcopy(base)
                state["focused"] = focused
                self.assertIs(check(state, "desktop"), state)

        with self.subTest(focused="missing"):
            state = deepcopy(base)
            self.assertIs(check(state, "desktop"), state)

    def test_unity_resource_violation_is_still_rejected(self):
        from testdisplay import check, profile

        pr = profile("desktop")
        state = {
            "width": pr["width"],
            "height": pr["height"],
            "fullscreen": False,
            "quality": 0,
            "vsync": 0,
            "game_vsync": False,
            "fps_limit_enabled": True,
            "fps_limit": 30,
            "target_fps": 30,
            "run_in_background": True,
            "audio_listener": 0,
            "sound": 1,
            "focused": True,
        }
        with self.assertRaises(RuntimeError):
            check(state, "desktop")

    def test_compositor_policy_still_rejects_bad_windows(self):
        from copy import deepcopy
        from unittest.mock import patch
        import testdisplay
        import testlaunch

        window = {
            "pid": 12345,
            "workspace": {"name": "special:tld-tests"},
            "focusHistoryID": 12,
            "fullscreen": False,
            "floating": True,
            "size": [1280, 720],
        }
        activewindow = {"pid": 99999}

        def run(client, active):
            def query_result(*args, **kwargs):
                return [client] if args[0] == "clients" else active

            with patch.object(testdisplay, "preflight", return_value=None), \
                    patch.object(testdisplay, "query", side_effect=query_result):
                testlaunch.check_windows(
                    {"profile": "desktop"}, {}, [12345])

        run(deepcopy(window), deepcopy(activewindow))

        cases = []
        bad_workspace = deepcopy(window)
        bad_workspace["workspace"]["name"] = "special:wrong"
        cases.append(("workspace", bad_workspace, deepcopy(activewindow)))

        bad_history = deepcopy(window)
        bad_history["focusHistoryID"] = 0
        cases.append(("focusHistoryID", bad_history, deepcopy(activewindow)))

        cases.append((
            "activewindow",
            deepcopy(window),
            {"pid": 12345},
        ))

        for name, client, active in cases:
            with self.subTest(case=name):
                with self.assertRaises(RuntimeError):
                    run(client, active)

if __name__ == "__main__":
    unittest.main()
