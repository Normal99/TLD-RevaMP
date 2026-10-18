#!/usr/bin/env python3
"""Offline comparison tests: py -3 -m unittest tools/test_telemetry_compare.py."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import telemetry_compare as tc

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "telemetry"


class ComparisonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.captures, cls.warnings = tc.load_inputs([FIXTURES])
        cls.moments = tc.group_moments(cls.captures)

    def test_fixture_grouping(self):
        self.assertEqual(self.warnings, [])
        self.assertEqual(len(self.captures), 9)
        self.assertEqual(len(self.moments), 2)
        for moment in self.moments:
            self.assertEqual(moment.sid, "a1b2c3d4e5f60718")
            self.assertEqual(len(moment.machines), 3)
            self.assertEqual({m.label for m in moment.machines}, {"host", "client 1", "client 2"})
            self.assertTrue(all(len(m.captures) == 3 for m in moment.machines))
        self.assertLessEqual(abs(self.moments[0].st - 100), 2)
        self.assertLessEqual(abs(self.moments[1].st - 200), 2)

    def test_session_time_alignment_ignores_file_clock(self):
        moment = self.moments[0]
        local_times = []
        for machine in moment.machines:
            pairs, synced = tc.window(machine, moment)
            self.assertTrue(synced)
            self.assertTrue(all(moment.st - 30 <= x <= moment.st + 5 for x, _ in pairs))
            frame = next((x, r) for x, r in pairs if r.get("k") == "frame" and r["st"] == 99)
            self.assertEqual(frame[0], 99)
            tick = next(r for _, r in pairs if r.get("k") == "tick" and r["st"] == 100)
            local_times.append(tick["t"])
        self.assertEqual(len(set(local_times)), 3)

    def test_gc_and_backlog_differences(self):
        text = "\n".join(tc.findings(self.moments[0]))
        self.assertIn("only client 1 had frames over 50 ms", text)
        self.assertIn("client 1: 3 recorded frames over 50 ms", text)
        self.assertIn("all with a garbage collection", text)
        self.assertIn("40 terrain generation backlog on 3 of 3 slow frames", text)
        self.assertIn("phase GC.Collect", text)
        self.assertIn("Setting quality differs", text)
        self.assertIn("Hardware gpu differs", text)
        self.assertNotIn("timeline not synced", text)

    def test_network_differences(self):
        text = "\n".join(tc.findings(self.moments[1]))
        self.assertIn("Network connection 1 ping (ms) differs", text)
        self.assertIn("client 2 450", text)
        self.assertIn("Network connection 1 local loss from quality (%) differs", text)
        self.assertIn("client 2 12", text)
        self.assertIn("Network sent bytes/frame differs", text)
        self.assertIn("Network sent bytes (cumulative) differs", text)
        self.assertNotIn("garbage collection", text)

    def test_html_is_well_formed_self_contained_and_escaped(self):
        moment = self.moments[0]
        page = tc.render_html(moment, ["<script src='remote'> & a warning"])
        root = ET.fromstring(page)
        self.assertEqual(root.tag, "html")
        self.assertIsNotNone(root.find("head/meta[@name='viewport']"))
        svgs = root.findall(".//{http://www.w3.org/2000/svg}svg")
        self.assertGreater(len(svgs), 10)
        self.assertIn("color-scheme: light dark", page)
        self.assertIn("@media(max-width: 600px)", page)
        self.assertIn("&lt;script", page)
        self.assertIn("slow-frame phases (stacked)", page)
        self.assertIn("generation backlog", page)
        self.assertIn("network", page)
        for element in root.iter():
            self.assertNotIn(element.tag, ("script", "link", "img", "iframe"))
            for attr, value in element.attrib.items():
                self.assertNotIn(attr, ("src", "href", "{http://www.w3.org/1999/xlink}href"))
        self.assertNotIn("@import", page)
        self.assertNotIn("url(", page)

    def test_zip_and_loose_duplicate_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "host-report.zip"
            with zipfile.ZipFile(report, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.write(FIXTURES / "host-100.json", "blackbox.json")
                archive.write(FIXTURES / "host.jsonl", "sessions/play.jsonl")
                archive.writestr("breakdown.json", '{"frames":2101}')
                archive.writestr("errors.json", '{"warnings":[]}')
                archive.writestr("fixes.json", '{}')
                archive.writestr("../../ignored.txt", "not extracted")
            captures, warnings = tc.load_inputs([report, FIXTURES, report])
            moments = tc.group_moments(captures)
            self.assertEqual(warnings, [])
            self.assertEqual(len(moments[0].machines), 3)
            host = next(m for m in moments[0].machines if m.label == "host")
            frames = tc.frame_records(tc.window(host, moments[0])[0])
            self.assertEqual(len(frames), len({x for x, _ in frames}))
            self.assertFalse((Path(directory) / "ignored.txt").exists())

    def test_cli(self):
        with tempfile.TemporaryDirectory() as directory:
            run = subprocess.run([sys.executable, str(Path(tc.__file__)), str(FIXTURES), "-o", directory],
                                 capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertEqual(len(list(Path(directory).glob("*.html"))), 2)
            self.assertIn("3 machine(s)", run.stdout)
            self.assertIn("only client 1", run.stdout)


class SoloCaptureTests(unittest.TestCase):
    def load(self, *extra):
        captures, warnings = tc.load_inputs([FIXTURES.parent / "telemetry-solo", *extra])
        self.assertEqual(warnings, [])
        return tc.group_moments(captures)

    def test_real_structure_local_marks_share_one_machine(self):
        moments = self.load()
        self.assertEqual(len(moments), 2)
        self.assertTrue(all(len(m.machines) == 1 for m in moments))
        self.assertEqual(len(moments[0].machines[0].captures), 4)
        pairs, synced = tc.window(moments[0].machines[0], moments[0])
        self.assertTrue(synced)
        frames = tc.frame_records(pairs)
        self.assertEqual(len(frames), 6)
        self.assertTrue(all(r["k"] == "frame" for _, r in frames))
        self.assertNotIn("timeline not synced", " ".join(tc.findings(moments[0])))

    def test_report_zip_reads_box_and_sessions_without_duplicates(self):
        fixture = FIXTURES.parent / "telemetry-solo"
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.zip"
            with zipfile.ZipFile(report, "w") as archive:
                archive.write(fixture / "report-blackbox.json", "blackbox.json")
                archive.write(fixture / "play.jsonl", "sessions/play.jsonl")
            captures, warnings = tc.load_inputs([report])
            self.assertEqual(warnings, [])
            self.assertEqual(len(captures), 2)
            moments = self.load(report)
            moment = moments[1]
            frames = tc.frame_records(tc.window(moment.machines[0], moment)[0])
            self.assertEqual(len(frames), 5)
            slow = [r for _, r in frames if r["ms"] > 50]
            self.assertEqual(len(slow), 1)
            self.assertEqual([slow[0]["gc0"], slow[0]["gc1"], slow[0]["gc2"]], [0, 0, 0])
            self.assertTrue(tc.gc(slow[0]))
            text = " ".join(tc.findings(moment))
            self.assertIn("1 recorded frames over 50 ms out of 5", text)
            self.assertIn("all with a garbage collection", text)
            self.assertNotIn("sampleAgeS generation", text)

    def test_repeated_millisecond_timestamps_are_separate_frames(self):
        head = {"sid": None, "stSource": "local", "start": "same", "momentSt": 100}
        captures = tc.parse_text(json.dumps({"head": head, "cols": ["st", "ms", "GC.Collect"],
                                            "rows": [[99, 16, 0], [99, 80, 50]]}), "box", "one", [])
        moment = tc.group_moments(captures)[0]
        self.assertEqual(len(tc.frame_records(tc.window(moment.machines[0], moment)[0])), 2)

    def test_local_clocks_on_different_machines_are_not_synced(self):
        captures = [tc.Capture({"sid": "same", "stSource": source, "role": role, "start": role},
                               [{"k": "frame", "st": 99, "ms": 80}], role, role, 100, True)
                    for role, source in (("host", "host"), ("client", "local"))]
        moment = tc.group_moments(captures)[0]
        self.assertTrue(all(not tc.window(m, moment)[1] for m in moment.machines))
        self.assertIn("timeline not synced", " ".join(tc.findings(moment)))


class RobustnessTests(unittest.TestCase):
    def box(self, rows, head=None, cols=None, owner="report"):
        warnings = []
        text = json.dumps({"head": head or {"sid": "test", "role": "client", "start": "a", "momentSt": 100},
                           "cols": cols or ["st", "ms"], "rows": rows})
        captures = tc.parse_text(text, "snapshot.json", owner, warnings)
        return tc.group_moments(captures)[0], warnings

    def test_old_files_without_st_use_order_not_t(self):
        moment, _ = self.box([[9000, 16], [9010, 80], [9020, 20]], cols=["t", "ms"])
        pairs, synced = tc.window(moment.machines[0], moment)
        self.assertFalse(synced)
        self.assertEqual([x for x, _ in pairs], [-2, -1, 0])
        text = "\n".join(tc.findings(moment))
        self.assertIn("session time unavailable", text)
        self.assertNotIn("timeline not synced", text)
        self.assertIn("One machine only", text)
        self.assertIn("not seconds", tc.render_html(moment))

    def test_one_local_clock_with_st_uses_seconds(self):
        moment, _ = self.box([[99, 16]], {"sid": "test", "stSource": "local", "momentSt": 100})
        self.assertTrue(tc.window(moment.machines[0], moment)[1])

    def test_missing_st_is_unavailable_even_in_shared_host_session(self):
        captures = [tc.Capture({"sid": "same", "stSource": "host", "role": role, "start": role},
                               [{"k": "frame", "ms": 80}], role, role, 100, True)
                    for role in ("host", "client")]
        moment = tc.group_moments(captures)[0]
        text = " ".join(tc.findings(moment))
        self.assertIn("session time unavailable", text)
        self.assertNotIn("timeline not synced", text)

    def test_unknown_sid_does_not_merge_unrelated_machines(self):
        captures = [tc.Capture({"role": "solo", "start": start}, [{"k": "tick", "st": 100}], start, start) for start in ("a", "b")]
        self.assertEqual(len(tc.group_moments(captures)), 2)

    def test_different_sessions_and_no_chaining_of_moments(self):
        captures = [tc.Capture({"sid": sid, "role": "host", "start": "a"}, [], "x", "x", st, True)
                    for sid, st in (("a", 100), ("a", 102), ("a", 104), ("b", 100))]
        moments = tc.group_moments(captures)
        self.assertEqual(len(moments), 3)
        self.assertEqual([m.st for m in moments if m.sid == "a"], [101, 104])

    def test_missing_fields_bad_rows_and_nonfinite_numbers(self):
        moment, warnings = self.box([[99, 80], [100], "bad", [101, None], [102, float("nan")], [103, "bad"]])
        self.assertTrue(warnings)
        page = tc.render_html(moment)
        ET.fromstring(page)
        self.assertNotIn('cx="nan', page)
        self.assertNotIn('cy="nan', page)
        self.assertIn("unknown", page)
        self.assertIn("1 recorded frames over 50 ms", page)

    def test_hitch_only_rate_limit_and_save_flags(self):
        records = [{"k": "hitch", "st": 99, "ms": 80, "saving": True, "stream": True, "rebase": True,
                    "phases": {"Physics.Simulate": 30}, "gen": {"terrain": -1}},
                   {"k": "tick", "st": 100, "hitches50": 5, "hitchesDropped": 4}]
        moment = tc.group_moments([tc.Capture({"sid": "test"}, records, "log", "log", 100)])[0]
        text = "\n".join(tc.findings(moment))
        for expected in ("lower bound", "4 hitch details", "all with saving", "all with streaming", "all with a floating-origin rebase"):
            self.assertIn(expected, text)
        self.assertNotIn("-1 terrain", text)

    def test_truncated_zip_and_log_keep_good_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "broken.zip").write_bytes(b"PK\x03\x04partial")
            (root / "good.jsonl").write_text('{"k":"head","sid":"ok"}\n{"k":"tick","st":10,"maxms":20}\n{"k":', encoding="utf-8")
            captures, warnings = tc.load_inputs([root])
            self.assertEqual(len(captures), 1)
            self.assertEqual(len(warnings), 2)
            self.assertIn("truncated", " ".join(warnings))
            moment = tc.group_moments(captures)[0]
            self.assertEqual(moment.st, 10)
            self.assertIn("no per-frame data", " ".join(tc.findings(moment)))

    def test_corrupt_zip_member_does_not_discard_good_member(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.zip"
            with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
                archive.writestr("bad.jsonl", '{"k":"tick","st":999}')
                archive.writestr("good.jsonl", '{"k":"head","sid":"ok"}\n{"k":"tick","st":100}')
            data = path.read_bytes().replace(b'"st":999', b'"st":888', 1)
            path.write_bytes(data)
            captures, warnings = tc.load_inputs([path])
            self.assertEqual(len(captures), 1)
            self.assertEqual(len(warnings), 1)
            self.assertIn("bad.jsonl", warnings[0])

    def test_no_inputs_fails_cleanly(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(tc.main([directory]), 1)
            self.assertIn("No usable telemetry", err.getvalue())


if __name__ == "__main__":
    unittest.main()
