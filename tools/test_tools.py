#!/usr/bin/env python3
"""Offline tests of the test tools' own helpers (no game needed): python3 -m unittest tools/test_tools.py"""
import os, pathlib, re, shutil, subprocess, sys, tempfile, unittest

TOOLS = pathlib.Path(__file__).resolve().parent


def run(code, env_extra=None, home=None):
    """Run `code` in a fresh interpreter with a clean TLD_ environment and HOME (the module reads both at import)."""
    env = {k: v for k, v in os.environ.items() if not k.startswith("TLD_")}
    env["HOME"] = home or tempfile.mkdtemp()
    env.update(env_extra or {})
    return subprocess.run([sys.executable, "-c", f"import sys; sys.path.insert(0, {str(TOOLS)!r}); " + code],
                          capture_output=True, text=True, env=env)


class TldEnv(unittest.TestCase):
    def test_defaults(self):
        r = run("import tldenv as t; print(t.GAME_DIR); print(repr(t.SECOND)); print(t.ssh_args('h'))", home="/nonexistent-home")
        lines = r.stdout.splitlines()
        self.assertEqual(lines[0], "/nonexistent-home/.local/share/Steam/steamapps/common/The Long Drive")
        self.assertEqual(lines[1], "''")
        self.assertEqual(lines[2], "['ssh', '-o', 'BatchMode=yes', 'h']")

    def test_config_file_and_precedence(self):
        home = tempfile.mkdtemp()
        cfg = pathlib.Path(home) / ".config/tld-revamp"; cfg.mkdir(parents=True)
        (cfg / "tools.env").write_text('# comment\nexport TLD_SECOND="me@pc"\nTLD_DIR=~/games/tld\nTLD_SSH_KEY=~/.ssh/k\n'
                                       "TLD_REMOTE_DIR=$HOME/x y\nTLD_CACHE=/c\n")
        r = run("import tldenv as t, os; print(t.SECOND, t.GAME_DIR, t.SSH_KEY, t.REMOTE_GAME, t.CACHE, os.environ['TLD_DIR'], t.SECOND_IP)",
                {"TLD_CACHE": "/from-env"}, home=home)
        self.assertEqual(r.stdout.split(" ", 1)[0], "me@pc")
        self.assertIn(f"{home}/games/tld {home}/.ssh/k $HOME/x y /from-env {home}/games/tld pc", r.stdout)

    def test_paths_for_the_second_machine(self):
        r = run("import tldenv as t; print(t.rsync_path('$HOME/a b')); print(t.rsync_path('/abs')); print(t.remote_quote('$HOME/a \"b'))")
        self.assertEqual(r.stdout.splitlines(), ["a b", "/abs", '"$HOME/a \\"b"'])

    def test_need_second_explains(self):
        r = run("import tldenv; tldenv.need_second()")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("TLD_SECOND=user@host", r.stderr)


class PrivacyScan(unittest.TestCase):
    def test_catches_and_allows(self):
        sys.path.insert(0, str(TOOLS)); import privacy_scan
        d = pathlib.Path(tempfile.mkdtemp())
        privacy_scan.ROOT = d
        # written in pieces, so this file passes the scan itself
        (d / "bad.py").write_text("p = '/ho" + "me/alice/x'\nip = '192." + "168.1.5'\nid = 7656119" + "8000000000\nclass main" + "script : MonoBehaviour {}\n")
        (d / "ok.py").write_text("p = '$HOME/x'; ip = '127.0.0.1'; doc = '198.51.100.1'; w = 'C:\\\\users\\\\steamuser\\\\Documents'\n")
        (d / "x.dll").write_bytes(b"MZ")
        found = privacy_scan.scan(["bad.py", "ok.py", "x.dll"])
        kinds = sorted({(f[0], f[2]) for f in found})
        self.assertEqual(kinds, [("bad.py", "a LAN or private IP address"), ("bad.py", "a Steam ID"), ("bad.py", "a home folder path"),
                                 ("bad.py", "decompiled game code"), ("x.dll", "a binary file")])


class FasterTests(unittest.TestCase):
    """mplib's build fingerprint and machine lock, suite.py's verdict count and table"""
    def test_fingerprint_follows_the_sources(self):
        d = tempfile.mkdtemp()
        r = run("import mplib, pathlib; R = pathlib.Path(" + repr(d) + "); mplib.ROOT = R; (R / 'src/A/bin').mkdir(parents=True); "
                "(R / 'src/A/x.cs').write_text('a'); a = mplib.source_fingerprint(); (R / 'src/A/bin/y.dll').write_text('b'); "
                "b = mplib.source_fingerprint(); (R / 'src/A/x.cs').write_text('c'); c = mplib.source_fingerprint(); print(a == b, a != c)")
        self.assertEqual(r.stdout.split(), ["True", "True"], r.stderr)

    def test_lock_waits_for_the_holder(self):
        cache = tempfile.mkdtemp()
        env = {"TLD_CACHE": cache}
        holder = subprocess.Popen([sys.executable, "-c", f"import sys, time; sys.path.insert(0, {str(TOOLS)!r}); import mplib; "
                                   "mplib.lock_machines(); print('held', flush=True); time.sleep(5)"],
                                  stdout=subprocess.PIPE, text=True, env=dict({k: v for k, v in os.environ.items() if not k.startswith("TLD_")}, **env))
        self.assertEqual(holder.stdout.readline().strip(), "held")
        r = run("import mplib; mplib.lock_machines(wait=0)", env)
        holder.kill(); holder.wait()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("busy", r.stdout + r.stderr)
        self.assertEqual(run("import mplib; mplib.lock_machines(wait=0); print('ok')", env).stdout.strip(), "ok")

    def test_suite_counts_and_table(self):
        sys.path.insert(0, str(TOOLS)); import suite
        self.assertEqual(suite.count(["PASS  A x", "FAIL  B", "FAILED to", "nothing", "PASS", "SUMMARY", "  PASS  A x", "  FAIL  B"]), (2, 1))
        self.assertEqual(suite.count(["  PASS  only indented", "  FAIL  b"]), (1, 1))
        t = suite.table([{"test": "a.py", "result": "PASS 2/2", "rc": 0, "ok": True, "time": 75, "setup": 50, "body": 25, "reuse": False}])
        self.assertIn("1m15s", t); self.assertIn("1/1 tests passed", t)


class VerifySigpipe(unittest.TestCase):
    """tools/verify.sh must survive a progress consumer that goes away.

    The verification script writes GitHub group/progress lines to its stdout. When the consumer
    of that stdout closes (a CI log pipe, a pager, `head -1`, the admission broker's own pipe),
    the write used to raise SIGPIPE and kill the whole script: the suite stopped part way
    through, at exit code 141, and a run with no failing project still looked like a failure.
    The exit code of every project is the authority, so a lost progress line must cost nothing.

    The harness is hermetic: a copy of the real script, FAKE unit projects and a FAKE dotnet.
    Nothing from the repository's own unit suite, and no real build, is involved.
    """

    # The writer block is located by PARSING the script, not by matching a literal, so the test
    # does not depend on the exact spelling of the writer.
    SAY_RE = re.compile(r"(?m)^say\(\) \{\n(?:[^\n]*\n)*?\}\n")

    # The pre-fix writer, used only as the negative control: it replaces exactly the `say()`
    # block and nothing else.
    INTOLERANT_SAY = """say() {
    printf '%s\\n' "$1"
}
"""

    FAKE_DOTNET = """#!/bin/sh
# A fake dotnet: it records how it was called, then exits with the code the harness asked for.
# $$ is this process (the child verify.sh spawned); $PPID is verify.sh itself.
name=""
prev=""
for a in "$@"; do
  if [ "$prev" = "--project" ]; then name=${a##*/}; name=${name%.csproj}; fi
  prev="$a"
done
{
  echo "--- project $name"
  for a in "$@"; do echo "$a"; done
  echo "CHILD pid=$$ ppid=$PPID"
  grep '^SigIgn:' "/proc/$$/status"
} >> "$FAKE_DOTNET_LOG"
rc=0
if [ -n "$FAKE_DOTNET_RCFILE" ] && [ -f "$FAKE_DOTNET_RCFILE" ]; then
  while read -r n r; do
    if [ "$n" = "$name" ]; then rc="$r"; fi
  done < "$FAKE_DOTNET_RCFILE"
fi
sleep 0.05
echo "fake dotnet ran $name"
exit "$rc"
"""

    def _harness(self, projects, failures=()):
        """A hermetic tree: a copy of tools/verify.sh, fake unit projects, a fake dotnet."""
        root = pathlib.Path(tempfile.mkdtemp(prefix="tld-verify-"))
        tools = root / "tools"
        (tools / "unit").mkdir(parents=True)
        for name in projects:
            (tools / "unit" / (name + ".csproj")).write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n')
        bindir = root / "fakebin"
        bindir.mkdir()
        dotnet = bindir / "dotnet"
        dotnet.write_text(self.FAKE_DOTNET)
        dotnet.chmod(0o755)
        rcfile = root / "rcfile"
        rcfile.write_text("".join("%s %s\n" % (n, r) for n, r in failures))
        log = root / "dotnet.log"
        return {"root": root, "tools": tools, "rcfile": rcfile, "log": log, "state": root / "state"}

    def _install(self, h, script=None):
        shutil.copy2(TOOLS / "verify.sh" if script is None else script, h["tools"] / "verify.sh")

    def _env(self, h, extra=None):
        env = {k: v for k, v in os.environ.items() if not k.startswith("TLD_")}
        env["HOME"] = str(h["root"] / "home")
        env["PATH"] = str(h["root"] / "fakebin") + os.pathsep + env.get("PATH", "")
        env["XDG_STATE_HOME"] = str(h["state"])
        env["FAKE_DOTNET_LOG"] = str(h["log"])
        env["FAKE_DOTNET_RCFILE"] = str(h["rcfile"])
        env.update(extra or {})
        return env

    def _run(self, h, args=(), read_bytes=None, env_extra=None):
        """Run the verification script with a stdout consumer that closes early.

        The script's OWN exit code is read from the process, never from a pipeline, so nothing
        can mask it. `read_bytes` None means the consumer is gone before the first byte;
        otherwise the consumer reads that much and then goes away.
        """
        r, w = os.pipe()
        p = subprocess.Popen([str(h["tools"] / "verify.sh")] + list(args),
                             stdout=w, stderr=subprocess.PIPE, env=self._env(h, env_extra), cwd=str(h["root"]))
        os.close(w)
        seen = b""
        if read_bytes is None:
            os.close(r)
        else:
            seen = os.read(r, read_bytes)
            os.close(r)
        err = p.stderr.read()
        p.stderr.close()
        p.wait(timeout=120)
        self.script_pid = p.pid
        return p.returncode, seen, err.decode("utf-8", "replace")

    def _manifest(self, h):
        state = h["state"] / "tld-revamp" / "verification"
        runs = sorted(state.glob("*")) if state.is_dir() else []
        self.assertTrue(runs, "the verification run did not use the durable state directory")
        return runs[-1] / "manifest.tsv"

    def _rows(self, h):
        lines = self._manifest(h).read_text().splitlines()
        self.assertEqual(lines[0], "project\texit\tlog")
        return [line.split("\t") for line in lines[1:] if line]

    def _calls(self, h):
        """The arguments every fake dotnet call saw, per project."""
        blocks, current = [], None
        for line in h["log"].read_text().splitlines():
            if line.startswith("--- project "):
                current = {"name": line[len("--- project "):], "args": [], "sigign": None, "child": None}
                blocks.append(current)
            elif current is not None and current["sigign"] is None and line.startswith("SigIgn:"):
                current["sigign"] = line.split()[1]
            elif current is not None and current["child"] is None and line.startswith("CHILD "):
                current["child"] = dict(kv.split("=") for kv in line.split()[1:])
            elif current is not None:
                current["args"].append(line)
        return blocks

    def _prop(self, args, name):
        """The value of a --property:Name=VALUE argument, or None."""
        for a in args:
            if a.startswith(name + "="):
                return a.split("=", 1)[1]
        return None

    def _say_block(self, script):
        """The `say()` block of a script, located by parsing."""
        m = self.SAY_RE.search(script)
        self.assertIsNotNone(m, "the script has no say() writer")
        return m

    def test_the_writer_is_tolerant_of_a_closed_consumer(self):
        """The writer is where the tolerance lives, and it is the only say() in the script."""
        script = (TOOLS / "verify.sh").read_text()
        m = self._say_block(script)
        block = m.group(0)
        self.assertIn("trap '' PIPE", block, "the writer must ignore SIGPIPE in its own subshell")
        self.assertIn("|| true", block, "the writer must not fail the run when the line is lost")
        # Only one say(): the mutation below has to hit exactly that one.
        self.assertEqual(len(self.SAY_RE.findall(script)), 1)

    def test_closed_consumer_does_not_kill_a_passing_run(self):
        """The regression itself: a closed consumer must not turn a passing run into a failure."""
        h = self._harness(["AlphaOne", "AlphaTwo", "AlphaThree"])
        self._install(h)
        rc, _, err = self._run(h, args=["Alpha"])
        self.assertEqual(rc, 0, "a passing filtered run must succeed, stderr: " + err)
        rows = self._rows(h)
        self.assertEqual([r[0] for r in rows], sorted(["AlphaOne", "AlphaTwo", "AlphaThree"]))
        self.assertEqual([r[1] for r in rows], ["0", "0", "0"])

    def test_early_ending_consumer_does_not_kill_a_passing_run(self):
        """The consumer reads a little and then goes away mid-run."""
        h = self._harness(["BetaOne", "BetaTwo", "BetaThree"])
        self._install(h)
        rc, seen, err = self._run(h, args=["Beta"], read_bytes=16)
        self.assertEqual(rc, 0, "an early-ending consumer must not fail the run, stderr: " + err)
        self.assertTrue(seen, "the consumer saw no output at all")
        rows = self._rows(h)
        self.assertEqual([r[0] for r in rows], sorted(["BetaOne", "BetaTwo", "BetaThree"]))
        self.assertEqual([r[1] for r in rows], ["0", "0", "0"])

    def test_one_failing_project_exit_7_keeps_its_code(self):
        """A project that exits 7: the run's exit code is 1, the manifest row says 7."""
        h = self._harness(["DeltaOne", "DeltaTwo"], failures=[("DeltaTwo", 7)])
        self._install(h)
        rc, _, err = self._run(h, args=["Delta"])
        self.assertEqual(rc, 1, "one failed project must make the run exit 1, stderr: " + err)
        rows = self._rows(h)
        self.assertEqual([(r[0], r[1]) for r in rows], [("DeltaOne", "0"), ("DeltaTwo", "7")])
        self.assertIn("fake dotnet ran DeltaTwo", pathlib.Path(rows[1][2]).read_text())

    def test_a_failed_project_keeps_its_real_code_and_the_failure_count(self):
        """Failures survive a closed consumer: the real rc, the manifest row and the count."""
        h = self._harness(["GammaOne", "GammaTwo", "GammaThree"],
                          failures=[("GammaOne", 3), ("GammaThree", 5)])
        self._install(h)
        rc, _, err = self._run(h, args=["Gamma"])
        # The exit code of the RUN is the number of failed projects.
        self.assertEqual(rc, 2, "the final failure count must survive, stderr: " + err)
        rows = self._rows(h)
        self.assertEqual([(r[0], r[1]) for r in rows],
                         [("GammaOne", "3"), ("GammaThree", "5"), ("GammaTwo", "0")])
        for name, code, log in rows:
            self.assertTrue(pathlib.Path(log).is_file(), "no log for " + name)
            self.assertIn("fake dotnet ran " + name, pathlib.Path(log).read_text())

    def test_the_failing_projects_are_named_when_the_consumer_stays(self):
        """With an open consumer the FAIL line carries the project's real exit code."""
        h = self._harness(["ZetaOne", "ZetaTwo"], failures=[("ZetaTwo", 7)])
        self._install(h)
        r = subprocess.run([str(h["tools"] / "verify.sh"), "Zeta"], capture_output=True, text=True,
                           env=self._env(h), cwd=str(h["root"]), timeout=120)
        self.assertEqual(r.returncode, 1)
        self.assertIn("FAIL ZetaTwo (exit 7)", r.stdout)
        self.assertIn("PASS ZetaOne (exit 0)", r.stdout)
        self.assertIn("::group::ZetaOne", r.stdout)
        self.assertIn("::endgroup::", r.stdout)

    def test_negative_control_the_pre_fix_writer_reproduces_the_sigpipe(self):
        """The test bites: without the tolerance the very same harness dies with 141."""
        h = self._harness(["EpsOne", "EpsTwo"])
        self._install(h)
        script = (h["tools"] / "verify.sh").read_text()
        m = self._say_block(script)
        mutated = script[:m.start()] + self.INTOLERANT_SAY + script[m.end():]
        self.assertNotEqual(mutated, script, "the mutation did not apply")
        # The mutation touches the say() block and nothing else.
        self.assertEqual(len(self.SAY_RE.findall(mutated)), 1)
        self.assertEqual(mutated.count("say_err()"), script.count("say_err()"))
        self.assertEqual(mutated.count("add_summary()"), script.count("add_summary()"))
        (h["tools"] / "verify.sh").write_text(mutated)
        rc, _, _ = self._run(h, args=["Eps"])
        # The subject is killed BY SIGPIPE. A shell reports that as 141 (128+13); a direct
        # parent (this test) sees the raw signal, -13.
        self.assertIn(rc, (141, -13),
                      "the pre-fix writer must still die with SIGPIPE, got " + str(rc))
        # The same run under a shell reports 141, which is the symptom that was reported.
        # PIPESTATUS[0] is the SUBJECT's own status, so the pipeline does not mask it:
        # `head -c 16` reads a little and then goes away, which closes the pipe.
        shell = subprocess.run(["/bin/bash", "-c",
                                '"$1" Eps | head -c 16 > /dev/null; exit "${PIPESTATUS[0]}"',
                                "bash", str(h["tools"] / "verify.sh")],
                               capture_output=True, text=True, env=self._env(h),
                               cwd=str(h["root"]), timeout=120)
        self.assertEqual(shell.returncode, 141,
                         "the shell must see 141 (128+SIGPIPE), got " + str(shell.returncode))

    def test_the_child_keeps_its_sigpipe_disposition(self):
        """The tolerance lives in the writer, not in the shell: dotnet still gets the default."""
        h = self._harness(["EtaOne", "EtaTwo"])
        self._install(h)
        rc, _, err = self._run(h, args=["Eta"])
        self.assertEqual(rc, 0, err)
        calls = self._calls(h)
        self.assertEqual(len(calls), 2)
        for call in calls:
            # The probe ran in the fake dotnet itself, whose parent is verify.sh.
            self.assertEqual(int(call["child"]["ppid"]), self.script_pid,
                             "the fake dotnet was not spawned directly by verify.sh")
            # SigIgn bit 13 (0x1000) is SIGPIPE. It must be clear: the child inherited the
            # default disposition, exactly as tld-admit's own child does.
            self.assertEqual(int(call["sigign"], 16) & 0x1000, 0,
                             "the child must not inherit an ignored SIGPIPE")

    def test_the_build_paths_are_absolute_unique_and_outside_the_repository(self):
        """Every project gets its own absolute obj/bin path, and the repository stays clean."""
        h = self._harness(["ThetaOne", "ThetaTwo", "ThetaThree"])
        self._install(h)
        rc, _, err = self._run(h, args=["Theta"])
        self.assertEqual(rc, 0, err)
        calls = self._calls(h)
        self.assertEqual(len(calls), 3)
        seen = set()
        for call in calls:
            args = call["args"]
            obj = self._prop(args, "--property:BaseIntermediateOutputPath")
            binp = self._prop(args, "--property:OutputPath")
            self.assertIsNotNone(obj, "no BaseIntermediateOutputPath for " + call["name"])
            self.assertIsNotNone(binp, "no OutputPath for " + call["name"])
            self.assertTrue(os.path.isabs(obj) and os.path.isabs(binp))
            self.assertIn(call["name"], obj)
            self.assertIn(call["name"], binp)
            self.assertNotIn(str(h["root"] / "tools"), obj)
            seen.add((obj, binp))
        self.assertEqual(len(seen), 3, "the per-project paths are not unique")
        self.assertFalse((h["root"] / "tools" / "obj").exists())
        self.assertFalse((h["root"] / "tools" / "bin").exists())
        self.assertFalse((h["tools"] / "unit" / "obj").exists())
        self.assertFalse((h["tools"] / "unit" / "bin").exists())

    def test_a_filter_that_matches_nothing_fails_with_two(self):
        """The no-match behaviour is unchanged, and it is still reported on stderr."""
        h = self._harness(["IotaOne"])
        self._install(h)
        rc, _, err = self._run(h, args=["NoSuchProject"])
        self.assertEqual(rc, 2)
        self.assertIn("no project matched", err)

    def test_the_optional_step_summary_is_written_when_it_exists(self):
        """GITHUB_STEP_SUMMARY is still honoured."""
        h = self._harness(["KappaOne", "KappaTwo"], failures=[("KappaTwo", 4)])
        self._install(h)
        summary = h["root"] / "summary.md"
        rc, _, err = self._run(h, args=["Kappa"], env_extra={"GITHUB_STEP_SUMMARY": str(summary)})
        self.assertEqual(rc, 1, err)
        text = summary.read_text()
        self.assertIn("- PASS KappaOne", text)
        self.assertIn("- FAIL KappaTwo (exit 4)", text)

    def test_an_explicit_artifact_directory_wins(self):
        """TLD_VERIFY_DIR still overrides the durable default."""
        h = self._harness(["LambdaOne"])
        self._install(h)
        explicit = h["root"] / "explicit"
        rc, _, err = self._run(h, args=["Lambda"], env_extra={"TLD_VERIFY_DIR": str(explicit)})
        self.assertEqual(rc, 0, err)
        self.assertTrue((explicit / "manifest.tsv").is_file())
        self.assertFalse((h["state"] / "tld-revamp" / "verification").exists())


if __name__ == "__main__":
    unittest.main()
