#!/usr/bin/env python3
"""The mutation test for the death lane: a broken implementation must be CAUGHT by the tests.

This is the negative proof the brief asks for. Each case breaks the production rule on purpose (in a copy, never
in the repository) and checks that the test suite fails. A test that cannot fail proves nothing.

  python3 -m unittest tools.test_deathlogic_mutation -v
"""
import os, re, shutil, subprocess, sys, tempfile, unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEATHSTATE = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
DEATHLOGIC = os.path.join(ROOT, "tools", "deathlogic.py")
TESTPY = os.path.join(ROOT, "tools", "test_deathlogic.py")
STATETEST = os.path.join(ROOT, "tools", "unit", "DeathStateTest.cs")


def run_python(path, cwd=ROOT):
    """Run a python test file and return (ok, output)."""
    r = subprocess.run([sys.executable, "-m", "unittest", path, "-v"],
                       cwd=cwd, capture_output=True, text=True, timeout=300)
    return r.returncode == 0, r.stdout + r.stderr


def run_dotnet(project, cwd=ROOT):
    """Run a C# test project and return (ok, output)."""
    artifacts = tempfile.mkdtemp(prefix="tld-deathloop-mut-")
    try:
        r = subprocess.run(["dotnet", "run", "--project", project, "-c", "Release",
                            "--artifacts-path", artifacts],
                           cwd=cwd, capture_output=True, text=True, timeout=900)
        return r.returncode == 0, r.stdout + r.stderr
    finally:
        shutil.rmtree(artifacts, ignore_errors=True)


class MutationTest(unittest.TestCase):
    """Each case: break the production rule, and the matching test must fail."""

    def mutate(self, path, old, new, count=1):
        """Copy the tree's file into a temp tree, apply the mutation, and run the test there.

        The repository is never modified: the mutation lives in a temporary copy."""
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
        self.assertIn(old, text, f"the mutation target is not in {os.path.basename(path)}: {old!r}")
        return text.replace(old, new, count)

    # ---------------------------------------------------------------- the C# state machine

    def test_the_flood_bound_is_caught(self):
        """If the death screen stops refusing the periodic record, the state test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
            text = self.mutate(src,
                "public bool AllowPeriodicRecord => Phase != DeathPhase.DeathScreen;",
                "public bool AllowPeriodicRecord => true;")
            # the test drives the decide/commit API: a decision that is carried out is committed by the test
            text = text.replace("if (d.TryPeriodic(i / 60.0)) sent++;",
                                "if (d.DecidePeriodic(i / 60.0) == DeathState.RecordDecision.Send) { d.PeriodicSent(i / 60.0); sent++; }")
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathStateTest.cs", "DeathStateTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathStateTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the death screen's record flood was NOT caught by the state test")
            self.assertIn("FAIL", out)

    def test_the_death_record_is_caught_when_it_is_not_spent(self):
        """If the death record may be sent again and again, the state test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
            text = self.mutate(src,
                "if (DeathRecordSent) { Suppressed++; return false; }",
                "if (false) { Suppressed++; return false; }")
            text = text.replace("Check(\"the death record is allowed once\", d.TryDeathRecord());",
                                "Check(\"the death record is allowed once\", d.TryDeathRecord()); d.DeathRecordSentOk();")
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathStateTest.cs", "DeathStateTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathStateTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the repeated death record was NOT caught")
            self.assertIn("FAIL", out)

    def test_the_abandon_is_caught_when_it_does_nothing(self):
        """If the abandoned death keeps holding the budget, the state test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
            text = self.mutate(src,
                "public void DeathAbandoned(double now)\n        {\n            if (Phase != DeathPhase.DeathScreen) return;",
                "public void DeathAbandoned(double now)\n        {\n            if (true) return;")
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathStateTest.cs", "DeathStateTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathStateTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the abandoned death was NOT caught")
            self.assertIn("FAIL", out)

    def test_the_fallback_is_caught_when_it_counts_as_an_anchored_drop(self):
        """If a fallback is claimed as an anchored drop, the state test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
            text = self.mutate(src,
                "case AnchorOutcome.NotRestorable:\n                case AnchorOutcome.PlacementFailed: AnchorFallbacks++; break;",
                "case AnchorOutcome.NotRestorable:\n                case AnchorOutcome.PlacementFailed: AnchorDrops++; break;")
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathStateTest.cs", "DeathStateTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathStateTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the fallback counted as an anchored drop was NOT caught")
            self.assertIn("FAIL", out)

    def test_the_failed_send_is_caught_when_it_spends_the_record(self):
        """If a send that failed still spends the one record of the death, the state test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs")
            text = self.mutate(src,
                "public void DeathRecordFailed()\n        {\n            Suppressed++;\n        }",
                "public void DeathRecordFailed()\n        {\n            Suppressed++;\n            DeathRecordSent = true;\n        }")
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathStateTest.cs", "DeathStateTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathStateTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the failed send that spent the record was NOT caught")
            self.assertIn("FAIL", out)

    def test_the_corpse_release_is_caught_when_it_does_nothing(self):
        """If the corpse of a finished death is never released, the wiring test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "SessionRules.Death.cs")
            with open(src, "r", encoding="utf-8") as f:
                text = f.read()
            old = "if (wasSessionDeath) Death.Respawn(_pending == AnchorOutcome.None ? AnchorOutcome.NoAnchor : _pending, Time.unscaledTimeAsDouble);"
            new = "if (wasSessionDeath) { }"
            self.assertIn(old, text)
            text = text.replace(old, new)
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "SessionRules.Death.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs"),
                        os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"))
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathWiringTest.cs", "DeathWiringTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathWiringTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the death that never leaves the death screen was NOT caught")
            self.assertIn("Exception", out)

    def test_the_stale_anchor_is_caught_when_it_is_used(self):
        """If the anchor of another session is used anyway, the wiring test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "SessionRules.Death.cs")
            with open(src, "r", encoding="utf-8") as f:
                text = f.read()
            old = "!Death.HasAnchor || !AnchorIsCurrent(DeathSession) ||"
            new = "!Death.HasAnchor ||"
            self.assertIn(old, text)
            text = text.replace(old, new)
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "SessionRules.Death.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs"),
                        os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"))
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathWiringTest.cs", "DeathWiringTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathWiringTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the stale anchor was NOT caught")
            self.assertIn("Exception", out)

    def test_the_seated_anchor_is_caught_when_it_uses_the_seat_height(self):
        """If a seated death is anchored at the seat's height, the wiring test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "SessionRules.Death.cs")
            with open(src, "r", encoding="utf-8") as f:
                text = f.read()
            old = "                p.y = GroundHeight(p);"
            new = "                // p.y = GroundHeight(p);"
            self.assertIn(old, text)
            text = text.replace(old, new)
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "SessionRules.Death.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs"),
                        os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"))
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathWiringTest.cs", "DeathWiringTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathWiringTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the seated anchor at the seat's height was NOT caught")
            self.assertIn("Exception", out)

    def test_the_self_heal_is_caught_when_it_always_acts(self):
        """If the self-heal gives a fresh death up, the wiring test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(ROOT, "src", "TLDRevamp", "Net", "SessionRules.Death.cs")
            with open(src, "r", encoding="utf-8") as f:
                text = f.read()
            old = "if (Time.unscaledTimeAsDouble - Death.DiedAt < SelfHealAfterS) return;"
            new = "// the guard removed"
            self.assertIn(old, text)
            text = text.replace(old, new)
            os.makedirs(os.path.join(tmp, "src", "TLDRevamp", "Net"), exist_ok=True)
            with open(os.path.join(tmp, "src", "TLDRevamp", "Net", "SessionRules.Death.cs"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(os.path.join(ROOT, "src", "TLDRevamp", "Net", "DeathState.cs"),
                        os.path.join(tmp, "src", "TLDRevamp", "Net", "DeathState.cs"))
            os.makedirs(os.path.join(tmp, "tools", "unit"), exist_ok=True)
            for name in ("DeathWiringTest.cs", "DeathWiringTest.csproj"):
                shutil.copy(os.path.join(ROOT, "tools", "unit", name), os.path.join(tmp, "tools", "unit", name))
            ok, out = run_dotnet(os.path.join("tools", "unit", "DeathWiringTest.csproj"), cwd=tmp)
            self.assertFalse(ok, "the self-heal that always acts was NOT caught")
            self.assertIn("Exception", out)

    # ---------------------------------------------------------------- the python rules

    def test_the_python_flood_bound_is_caught(self):
        """If the python budget lets the death screen send every frame, the python test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            text = self.mutate(DEATHLOGIC,
                "        if self.dead:\n            if now >= self.next_due:",
                "        if False:\n            if now >= self.next_due:")
            os.makedirs(os.path.join(tmp, "tools"), exist_ok=True)
            with open(os.path.join(tmp, "tools", "deathlogic.py"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(TESTPY, os.path.join(tmp, "tools", "test_deathlogic.py"))
            ok, out = run_python("tools.test_deathlogic", cwd=tmp)
            self.assertFalse(ok, "the python record flood was NOT caught")
            self.assertIn("FAILED", out)

    def test_the_python_corpse_ownership_is_caught(self):
        """If the corpse of somebody else is accepted, the python test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            text = self.mutate(DEATHLOGIC,
                "owned = [(net, row) for net, row in fresh if row.get(\"owner\") == joiner_id]",
                "owned = [(net, row) for net, row in fresh]")
            os.makedirs(os.path.join(tmp, "tools"), exist_ok=True)
            with open(os.path.join(tmp, "tools", "deathlogic.py"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(TESTPY, os.path.join(tmp, "tools", "test_deathlogic.py"))
            ok, out = run_python("tools.test_deathlogic", cwd=tmp)
            self.assertFalse(ok, "the wrong corpse was NOT caught")
            self.assertIn("FAILED", out)

    def test_the_python_record_check_is_caught(self):
        """If the record's dead flag is not checked, the python test must fail."""
        with tempfile.TemporaryDirectory() as tmp:
            text = self.mutate(DEATHLOGIC,
                "if dead_expected and dead is not True:",
                "if False:")
            os.makedirs(os.path.join(tmp, "tools"), exist_ok=True)
            with open(os.path.join(tmp, "tools", "deathlogic.py"), "w", encoding="utf-8") as f:
                f.write(text)
            shutil.copy(TESTPY, os.path.join(tmp, "tools", "test_deathlogic.py"))
            ok, out = run_python("tools.test_deathlogic", cwd=tmp)
            self.assertFalse(ok, "the unchecked dead flag was NOT caught")
            self.assertIn("FAILED", out)


if __name__ == "__main__":
    unittest.main()
