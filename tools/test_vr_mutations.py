#!/usr/bin/env python3
"""Prove the VR guards detect deliberate regressions, without a game, a runtime or a headset.

This is the negative proof the brief asks for. Each case breaks the production rule on purpose (in a
temporary COPY, never in the repository) and checks that the matching guard FAILS. A guard that cannot fail
proves nothing: the two source guards that were defeated by exactly the defects they claimed to protect
against are replaced here by guards that drive the production code.

  python3 -m unittest tools.test_vr_mutations -v
"""
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
BRIDGE = ROOT / "src" / "TLDRevamp" / "Fixes" / "OpenXrBridge.cs"
LIFECYCLE = ROOT / "src" / "TLDRevamp" / "VR" / "VrLoaderLifecycle.cs"
POLICY = ROOT / "src" / "TLDRevamp" / "VR" / "VrLoaderPolicy.cs"
DEBUG = ROOT / "src" / "TLDRevamp" / "DebugBridge.cs"

BEHAVIOUR_PROJECT = "tools/unit/OpenXrBridgeBehaviourTest.csproj"
SOURCE_PROJECT = "tools/unit/OpenXrBridgeTest.csproj"


def run_project(project, tree):
    """Run a unit project against a (possibly mutated) tree. Returns (returncode, output)."""
    env = os.environ.copy()
    env["TLD_REPO_ROOT"] = str(tree)
    env["TLD_REVAMP_ROOT"] = str(tree)
    result = subprocess.run(
        ["dotnet", "run", "--disable-build-servers", "--project", project, "-c", "Release"],
        cwd=tree, env=env, capture_output=True, text=True, timeout=900)
    return result.returncode, result.stdout + result.stderr


class VrMutationTest(unittest.TestCase):
    """Each case: break the production rule, and the matching guard must fail."""

    def mutate_tree(self, name, edits):
        """Copy the tree's VR sources and the guards into a temp tree, apply the edits, run a guard.

        `edits` maps a relative source path to (old, new)."""
        tmp = pathlib.Path(tempfile.mkdtemp(prefix="tld-vr-mut-"))
        self.addCleanup(shutil.rmtree, tmp, True)
        for rel in ("src/TLDRevamp/Fixes/OpenXrBridge.cs",
                    "src/TLDRevamp/VR/VrLoaderLifecycle.cs",
                    "src/TLDRevamp/VR/VrLoaderPolicy.cs",
                    "src/TLDRevamp/DebugBridge.cs"):
            target = tmp / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(ROOT / rel, target)
        # the guards and their stubs
        for rel in ("tools/unit/OpenXrBridgeBehaviourTest.cs",
                    "tools/unit/OpenXrBridgeBehaviourTest.csproj",
                    "tools/unit/OpenXrBridgeStubs.cs",
                    "tools/unit/OpenXrBridgeTest.cs"):
            target = tmp / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(ROOT / rel, target)
        shutil.copy(ROOT / "tools/unit/OpenXrBridgeTest.csproj", tmp / "tools/unit/OpenXrBridgeTest.csproj")
        # the props file: every project owns its own output tree
        shutil.copy(ROOT / "tools/unit/Directory.Build.props", tmp / "tools/unit/Directory.Build.props")
        # ... and the crash guard that props file compiles into every test
        shutil.copy(ROOT / "tools/unit/TestCrashGuard.cs", tmp / "tools/unit/TestCrashGuard.cs")
        # the sources the guards read by relative path
        for rel in ("src/TLDRevamp/Plugin.cs", "src/TLDRevamp/Fixes/VrContractBridge.cs"):
            target = tmp / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(ROOT / rel, target)

        for rel, changes in edits.items():
            path = tmp / rel
            text = path.read_text(encoding="utf-8")
            # a file may carry SEVERAL edits: a mutation that needs more than one change must not be
            # silently reduced to its last one (a dict keyed by path would drop the earlier ones).
            if isinstance(changes, tuple):
                changes = [changes]
            for old, new in changes:
                self.assertIn(old, text, f"{name}: the mutation anchor is not in {rel}: {old[:70]!r}")
                text = text.replace(old, new, 1)
            path.write_text(text, encoding="utf-8")
        return tmp

    def assert_caught(self, name, project, edits, marker="FAIL"):
        tree = self.mutate_tree(name, edits)
        returncode, out = run_project(project, tree)
        self.assertEqual(
            1, returncode,
            f"{name}: expected an exact clean exit 1 from {project}, got {returncode}:\n{out[-3000:]}")
        if marker == "FAIL":
            failure = r"(?m)^FAIL(?:\s|$)"
        else:
            failure = r"(?m)^" + re.escape(marker) + r"\s*$"
        self.assertRegex(
            out, failure,
            f"{name}: the guard failed without an anchored assertion-failure line:\n{out[-3000:]}")

    def test_the_unmodified_copied_tree_is_a_clean_baseline(self):
        """Both copied-tree projects must pass without any mutation."""
        tree = self.mutate_tree("the unmodified copied tree", {})
        for project in (SOURCE_PROJECT, BEHAVIOUR_PROJECT):
            with self.subTest(project=project):
                returncode, out = run_project(project, tree)
                self.assertEqual(
                    0, returncode,
                    f"the unmodified copied tree must pass {project}, got {returncode}:\n{out[-3000:]}")

    # ---------------------------------------------------------------- the production defect (F3)

    def test_the_discarded_stop_action_is_caught(self):
        """Disable() must honour the action the lifecycle returns.

        The old defect: the early branch called `Lifecycle.RequestStop()` and THREW THE RESULT AWAY, so the
        machine moved to `Stopping` with the stop never accounted for and the reported state wrong.
        """
        self.assert_caught(
            "the discarded stop action",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "if (action != VrLoaderAction.None) Lifecycle.ReportExternallyStopped();",
                "Lifecycle.RequestStop();")},
        )

    def test_the_drive_from_the_wrong_step_is_caught(self):
        """Drive() must start at the action it was given.

        The old defect: `Drive()` always started at `ChooseLoader`, so a stop's `StopSubsystems` step was
        never run: the manager was never stopped and the session kept running.
        """
        self.assert_caught(
            "the drive from the wrong step",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "                var action = first;\n",
                "                var action = VrLoaderAction.ChooseLoader;\n")},
        )

    def test_the_stop_that_never_reaches_the_manager_is_caught(self):
        """A stop must really stop the manager's subsystems and deinitialize its loader."""
        self.assert_caught(
            "the stop that never reaches the manager",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "            manager.StopSubsystems();\n            return Lifecycle.ReportSubsystemsStopped();",
                "            return Lifecycle.ReportSubsystemsStopped();")},
        )

    def test_a_stop_that_leaves_the_machine_stopping_is_caught(self):
        """The machine must not be left in `Stopping` when the manager is already gone."""
        self.assert_caught(
            "the machine left in stopping",
            BEHAVIOUR_PROJECT,
            {str(LIFECYCLE.relative_to(ROOT)): (
                "            if (_state == VrLoaderState.Idle) return VrLoaderAction.None;\n"
                "            if (_state == VrLoaderState.Failed) return VrLoaderAction.None;\n"
                "            _state = VrLoaderState.Idle;",
                "            if (_state == VrLoaderState.Idle) return VrLoaderAction.None;\n"
                "            if (_state == VrLoaderState.Failed) return VrLoaderAction.None;\n"
                "            _state = VrLoaderState.Stopping;")},
        )

    def test_the_retry_after_a_failed_disable_is_caught(self):
        """A retry must still work after a Disable() from `failed`.

        The old defect: `RequestStop()` moved a FAILED machine to `Stopping`, where a retry is refused, so the
        failure became permanent.
        """
        self.assert_caught(
            "the retry after a failed disable",
            BEHAVIOUR_PROJECT,
            {str(LIFECYCLE.relative_to(ROOT)): (
                "            if (_state == VrLoaderState.Idle || _state == VrLoaderState.Failed) return VrLoaderAction.None;",
                "            if (_state == VrLoaderState.Idle) return VrLoaderAction.None;")},
        )

    # ---------------------------------------------------------------- the classification (F4)

    def test_a_query_classified_as_a_mutation_is_caught(self):
        """`openxr state` is a read: it must not need developer access."""
        self.assert_caught(
            "the query classified as a mutation",
            BEHAVIOUR_PROJECT,
            {str(POLICY.relative_to(ROOT)): (
                '        public static readonly string[] QueryVerbs = { "state" };',
                '        public static readonly string[] QueryVerbs = { };')},
        )

    def test_a_mutation_classified_as_a_query_is_caught(self):
        """`openxr start` and the rest change the session: they must NOT be reads."""
        self.assert_caught(
            "the mutation classified as a query",
            BEHAVIOUR_PROJECT,
            {str(POLICY.relative_to(ROOT)): (
                '        public static readonly string[] MutationVerbs = { "start", "retry", "stop", "recenter" };',
                '        public static readonly string[] MutationVerbs = { };')},
        )

    def test_the_recenter_origin_classified_as_a_query_is_caught(self):
        """`openxr recenter origin` moves the runtime's tracking origin: it is a mutation."""
        self.assert_caught(
            "the recenter origin classified as a query",
            BEHAVIOUR_PROJECT,
            {str(POLICY.relative_to(ROOT)): (
                '            if (what == "recenter origin") return VrBridgeVerb.Mutation;',
                '            if (what == "recenter origin") return VrBridgeVerb.Query;')},
        )

    # ---------------------------------------------------------------- the help (D2)

    def test_a_second_syntax_for_the_command_is_caught(self):
        """The help must print the command exactly once, with one syntax.

        The old defect: the help listed `openxr [state|start|stop|recenter]` AND
        `openxr [start|retry|stop|recenter]` — two syntaxes for one command, one of them stale.
        """
        self.assert_caught(
            "the second syntax in the help",
            SOURCE_PROJECT,
            {str(DEBUG.relative_to(ROOT)): (
                " + VR.VrBridgeCommandPolicy.Syntax + ",
                " + \"openxr [state|start|retry|stop|recenter [origin]] openxr [state|start|stop|recenter]\" + "),
             },
            marker="FAIL the help prints the policy's syntax",
        )

    def test_a_stale_syntax_left_in_the_help_is_caught(self):
        """The stale syntax must not survive: the help pins the exact line."""
        self.assert_caught(
            "the stale syntax left in the help",
            SOURCE_PROJECT,
            {str(POLICY.relative_to(ROOT)): (
                'public const string Syntax = "openxr [state|start|retry|stop|recenter [origin]]";',
                'public const string Syntax = "openxr [state|start|stop|recenter]";'),
             },
            marker="FAIL the policy's syntax is the documented one",
        )

    # ---------------------------------------------------------------- the source guards (F1, F2)

    def test_the_profiles_after_the_initialize_is_caught(self):
        """The controller profiles must be configured BEFORE the loader is initialized.

        The old guard compared the CASE LABELS, so moving the `ConfigureProfiles()` call into the initialize
        case left the suite green. This one drives the production bridge: the stub's loader records which
        profiles were enabled when it initialized.
        """
        self.assert_caught(
            "the profiles configured after the initialize",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "                        case VrLoaderAction.ConfigureFeatures:\n"
                "                            ConfigureProfiles();\n"
                "                            action = Lifecycle.ReportFeaturesConfigured();\n"
                "                            break;\n"
                "                        case VrLoaderAction.InitializeLoader:\n"
                "                            action = InitializeLoader();\n"
                "                            break;",
                "                        case VrLoaderAction.ConfigureFeatures:\n"
                "                            action = Lifecycle.ReportFeaturesConfigured();\n"
                "                            break;\n"
                "                        case VrLoaderAction.InitializeLoader:\n"
                "                            action = InitializeLoader();\n"
                "                            ConfigureProfiles();\n"
                "                            break;")},
        )

    def test_the_double_recenter_is_caught(self):
        """The recenter must move the rig once, and the runtime's origin only when it is asked for.

        The old guard counted the literal `input.TryRecenter()`, so a second recenter under any other name
        slipped through.
        """
        self.assert_caught(
            "the double recenter",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "                    input.TryRecenter();\n",
                "                    input.TryRecenter();\n"
                "                    input.TryRecenter();\n")},
        )

    def test_the_origin_moved_without_being_asked_for_is_caught(self):
        """The runtime's tracking origin must not move on a plain recenter."""
        self.assert_caught(
            "the origin moved without being asked for",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "                    if (origin != VrTrackingOrigin.Unchanged) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);",
                "                    input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);")},
        )

    def test_the_sticky_origin_request_is_caught(self):
        """A `recenter origin` request is for THAT recenter only.

        The defect this catches: the explicit origin request was a STICKY flag, so one `recenter origin` made
        every LATER plain recenter move the runtime's tracking origin as well. The world then kept moving
        under the camera rig and the two drifted apart again, which is the exact failure the one-owner
        recenter rule exists to prevent.
        """
        self.assert_caught(
            "the sticky origin request",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): [
                ("                    var origin = VrTrackingPolicy.OriginFor(requestOrigin, false, input != null, input != null);",
                 "                    var origin = VrTrackingPolicy.OriginFor(requestOrigin || _requestOrigin, false, input != null, input != null);"),
                ("        internal static long RecenterCount, StartCount, StopCount;",
                 "        internal static long RecenterCount, StartCount, StopCount;\n        internal static bool _requestOrigin;"),
                ('            if (what == "recenter origin") { Recenter(true); return State(); }',
                 '            if (what == "recenter origin") { _requestOrigin = true; Recenter(true); return State(); }')]},
        )

    def test_the_reported_origin_mode_is_caught(self):
        """The state must report the runtime's tracking-origin MODE.

        Without it, the hardware check cannot tell "the rig moved" from "the world moved": the origin's
        movement is only visible through the mode the runtime reports.
        """
        self.assert_caught(
            "the origin mode not reported",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "Json.Str(TrackingOriginMode())",
                "Json.Str(\"\")")},
        )

    def test_the_autonomous_start_is_caught(self):
        """Nothing may start a session on its own."""
        self.assert_caught(
            "the autonomous start",
            BEHAVIOUR_PROJECT,
            {str(BRIDGE.relative_to(ROOT)): (
                "            if (Time.unscaledTime < _nextObserve) return;",
                "            if (!Lifecycle.Running) Enable();\n"
                "            if (Time.unscaledTime < _nextObserve) return;")},
        )


if __name__ == "__main__":
    unittest.main()
