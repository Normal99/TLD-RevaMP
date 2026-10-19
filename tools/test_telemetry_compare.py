#!/usr/bin/env python3
"""Offline comparison tests: py -3 -m unittest tools/test_telemetry_compare.py."""
import contextlib
import copy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import shutil
import unittest
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import telemetry_compare as tc

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "telemetry"


class ComparisonTests(unittest.TestCase):
    def test_deferred_candidates_are_not_reported_as_generation_backlog(self):
        details = {"buildings": 0, "buildingsDeferred": 29, "roads": 0, "items": -1, "sampleAgeS": 1.5}
        self.assertEqual(tc.backlog({"gen": details}), {"buildings": 0, "roads": 0})
        self.assertEqual(tc.backlog({"gen": 0, "genDetails": details}), {"buildings": 0, "roads": 0})
        self.assertEqual(tc.backlog({"gen": {"buildings": 3}}), {"buildings": 3})

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

    def test_one_feedback_folder_includes_local_and_received_marks_and_reports(self):
        source = FIXTURES.parent / "telemetry-state"
        boxes = [p for p in source.rglob("*.json") if "rows" in json.loads(p.read_text(encoding="utf-8"))]
        self.assertGreaterEqual(len(boxes), 2)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "tldrevamp-feedback"
            marks = root / "marks"
            marks.mkdir(parents=True)
            for i, path in enumerate(boxes):
                basename = "mark-session-100-000-wheelbug"
                target = marks / (basename + ("" if i == 0 else f"-from-player-teammate-{i}") + ".json")
                shutil.copyfile(path, target)
                # The same captures also arriving inside reports must still describe just two machines.
                player = root / "players" / f"player-teammate-{i}"
                player.mkdir(parents=True)
                with zipfile.ZipFile(player / "same-report-name.zip", "w") as archive:
                    archive.writestr("blackbox.json", path.read_bytes())
            captures, warnings = tc.load_inputs([root])
            self.assertEqual(warnings, [])
            moments = tc.group_moments(captures)
            self.assertEqual(len(moments), 1)
            self.assertEqual(len(moments[0].machines), len(boxes))
            self.assertIn("host has wheel in slot RL", "\n".join(tc.state_findings(moments[0])))
            self.assertEqual(len({c.source for c in captures}), len(boxes) * 2)
            with contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(tc.main([str(root), "-o", str(Path(directory) / "comparison")]), 0)
            self.assertIn("host has wheel in slot RL", output.getvalue())

    def test_received_filename_does_not_split_machine_identity(self):
        box = next(c for c in self.captures if c.blackbox)
        with tempfile.TemporaryDirectory() as directory:
            marks = Path(directory) / "marks"
            marks.mkdir()
            value = {"head": box.head, "cols": ["st", "ms"], "rows": [[100, 16]]}
            for name in ("local.json", "local-from-player-CON-1.json", "local-from-player-other-2.json"):
                (marks / name).write_text(json.dumps(value), encoding="utf-8")
            captures, warnings = tc.load_inputs([Path(directory)])
            self.assertEqual(warnings, [])
            self.assertEqual(len(tc.group_moments(captures)[0].machines), 1)

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


class RunModeTests(unittest.TestCase):
    def run_folder(self, root, name, fps, car_ms, start=100.0):
        folder = Path(root) / name
        folder.mkdir()
        bench = {"label": name, "plugins": [{"name": "TLD Revamp", "version": "x"}], "scenarios": [
            {"name": "carpark", "status": "ok", "meanFps": fps, "p50Ms": 1000 / fps, "p99Ms": 20.0, "realtimeStart": start, "realtimeEnd": start + 30},
            {"name": "items", "status": "failed", "meanFps": 1.0}]}
        (folder / f"TLDBench-{name}-1.json").write_text(json.dumps(bench), encoding="utf-8")
        ticks = [{"k": "head", "telemetry": {"scriptTimers": True}}]
        for st in (90.0, 105.0, 110.0, 115.0, 120.0, 125.0, 130.0, 160.0):
            ticks.append({"k": "tick", "st": st, "frames": 300, "allocKBps": 10.0, "bound": {"cpu": 200, "gpu": 100},
                          "frame": {"busyMs": 12.0, "gpuMs": 8.0}, "render": {"drawCalls": 2000.0},
                          "scripts": {"frames": 300, "totalMsPerFrame": car_ms + 0.5, "top": [
                              {"m": "carscript.Update", "msPerFrame": car_ms, "callsPerFrame": 16.0, "maxMs": 0.4},
                              {"m": "mainscript.Update", "msPerFrame": 0.5, "callsPerFrame": 1.0, "maxMs": 0.2}]},
                          "fixes": {"ParkedCarSleep.Skipped": 5}})
        (folder / "20261018-solo.jsonl").write_text(chr(10).join(json.dumps(t) for t in ticks), encoding="utf-8")
        return folder

    def test_tick_window_takes_ticks_inside_the_scenario(self):
        ticks = [{"st": s} for s in (100.0, 102.0, 105.0, 130.0, 133.0, 140.0)]
        self.assertEqual([t["st"] for t in tc.tick_window(ticks, 100.0, 130.0)], [105.0, 130.0])  # at least half of the 5 s interval inside

    def test_run_vs_run_scripts_and_frames(self):
        with tempfile.TemporaryDirectory() as root:
            a = self.run_folder(root, "base", 50.0, 2.0)
            b = self.run_folder(root, "fix", 60.0, 1.0)
            out = Path(root) / "out"
            with contextlib.redirect_stdout(io.StringIO()) as text:
                self.assertEqual(tc.main(["--runs", str(a), str(b), "--names", "off", "on", "-o", str(out)]), 0)
            data = json.loads((out / "runs.json").read_text(encoding="utf-8"))
            section = data["report"][0]
            self.assertEqual(section["scenario"], "carpark")
            self.assertEqual(len(data["report"]), 1)   # failed scenarios are left out
            fps = next(m for m in section["metrics"] if m["key"] == "meanFps")
            self.assertAlmostEqual(fps["pct"][1], 20.0)
            car = next(r for r in section["scripts"] if r["m"] == "carscript.Update")
            self.assertAlmostEqual(car["ms"][0], 2.0)
            self.assertAlmostEqual(car["delta"], -1.0)
            share = next(m for m in section["metrics"] if m["key"] == "gpuShare")
            self.assertAlmostEqual(share["values"][0], 100 / 3)
            self.assertIn("carscript.Update", text.getvalue())
            page = (out / "runs.html").read_text(encoding="utf-8")
            ET.fromstring(page.replace("<!doctype html>", "").replace("<meta charset='utf-8'>", "<meta charset='utf-8'/>")
                          .replace("<meta name='viewport' content='width=device-width,initial-scale=1'>", "<meta name='viewport' content='width=device-width,initial-scale=1'/>"))

    def test_only_the_matching_launch_log_is_used(self):
        with tempfile.TemporaryDirectory() as root:
            folder = self.run_folder(root, "a", 50.0, 2.0)
            # another launch's log in the same folder: same clock range, different numbers, mostly outside the scenario
            other = [{"k": "tick", "st": st, "frames": 300, "frame": {"busyMs": 99.0}} for st in (10.0, 15.0, 125.0)]
            (folder / "older-solo.jsonl").write_text(chr(10).join(json.dumps(t) for t in other), encoding="utf-8")
            scenarios, meta = tc.load_run(str(folder))
            self.assertAlmostEqual(scenarios["carpark"]["ticks"]["busyMs"], 12.0)
            self.assertFalse(any("older" in f for f in meta["files"]))

    def test_missing_fix_counter_in_a_repeat_counts_as_zero(self):
        a = {"carpark": {"bench": {}, "ticks": {"frames": 1, "fixes": {"X.Skipped": 10}}, "scripts": {}}}
        b = {"carpark": {"bench": {}, "ticks": {"frames": 1, "fixes": {}}, "scripts": {}}}
        self.assertEqual(tc.merge_repeats([a, b])["carpark"]["fixes"], {"X.Skipped": 5})

    def test_repeats_are_averaged(self):
        with tempfile.TemporaryDirectory() as root:
            a1 = self.run_folder(root, "a1", 50.0, 2.0)
            a2 = self.run_folder(root, "a2", 70.0, 1.0)
            b = self.run_folder(root, "b", 60.0, 1.5)
            out = Path(root) / "out"
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(tc.main(["--runs", f"{a1},{a2}", str(b), "-o", str(out)]), 0)
            section = json.loads((out / "runs.json").read_text(encoding="utf-8"))["report"][0]
            fps = next(m for m in section["metrics"] if m["key"] == "meanFps")
            self.assertEqual(section["runs"], [2, 1])
            self.assertAlmostEqual(fps["values"][0], 60.0)
            self.assertEqual(fps["spread"][0], [50.0, 70.0])


class StateComparisonTests(unittest.TestCase):
    def setUp(self):
        captures, warnings = tc.load_inputs([FIXTURES.parent / "telemetry-state"])
        self.assertEqual(warnings, [])
        self.moment = tc.group_moments(captures)[0]

    def state(self, role):
        machine = next(m for m in self.moment.machines if m.head["role"] == role)
        return tc.selected_box(machine, self.moment).state

    def test_known_desyncs_in_plain_words(self):
        lines = tc.state_findings(self.moment)
        text = "\n".join(lines)
        for expected in ("owner disagreement: host 0, client 1 1", "host has wheel in slot RL", "client 1's slot RL has empty",
                         "object present on host only", "players 0, 1 claim one seat", "position differs by 3.50 m",
                         "fuel mismatch", "engine mismatch", "ignition/key mismatch", "radio tuning mismatch", "time-of-day mismatch", "radio station FM 0 mismatch"):
            self.assertIn(expected, text)
        events = [line for line in lines if line.startswith("Event")]
        self.assertEqual(len(events), 3)
        self.assertIn("99.800 host", events[0])
        self.assertIn("99.850 client 1", events[1])
        self.assertIn("part absent", events[1])
        self.assertIn("99.900 host", events[2])
        page = tc.render_html(self.moment)
        self.assertLess(page.index("owner disagreement"), page.index("<h2>Machines"))
        self.assertIn("coarse:carscript.Update", page)

    def test_stdout_includes_state_and_related_events(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(tc.main([str(FIXTURES.parent / "telemetry-state"), "-o", tmp]), 0)
            self.assertIn("owner disagreement", out.getvalue())
            self.assertIn("host has wheel in slot RL", out.getvalue())
            self.assertIn("attach-refused", out.getvalue())

    def test_coverage_and_budget_do_not_invent_missing_objects(self):
        state = self.state("client")
        state["centerCm"] = [1000000, 0, 1000000]
        self.assertNotIn("object present on host only", "\n".join(tc.state_findings(self.moment)))
        state["centerCm"] = [0, 0, 0]
        state["dropped"] = 1
        text = "\n".join(tc.state_findings(self.moment))
        self.assertNotIn("object present on host only", text)
        self.assertIn("omitted by bounds", text)
        state["dropped"] = 0
        state["failed"] = True
        self.assertNotIn("object present on host only", "\n".join(tc.state_findings(self.moment)))

    def test_sender_clock_corrects_normal_interpolation(self):
        host = self.state("host")["entities"][0]
        client = self.state("client")["entities"][0]
        host.update(owner=0, poseSt=500, posCm=[10000, 0, 0], vel=[20, 0, 0], speed=20)
        client.update(owner=0, poseSt=499.85, posCm=[9700, 0, 0], vel=[20, 0, 0], speed=20)
        self.assertNotIn("position differs", "\n".join(tc.state_findings(self.moment)))
        client["posCm"][0] -= 200
        self.assertIn("sender-clock correction", "\n".join(tc.state_findings(self.moment)))

    def test_stale_pose_is_not_extrapolated_indefinitely(self):
        client = self.state("client")["entities"][0]
        client.update(owner=0, poseSt=490)
        self.assertIn("stale pose or handover", "\n".join(tc.state_findings(self.moment)))

    def test_self_reported_seats_detect_conflict_across_machines(self):
        self.state("host")["players"] = [self.state("host")["players"][0]]
        self.state("client")["players"] = [self.state("client")["players"][1]]
        self.assertIn("each claim this seat on their own machine", "\n".join(tc.state_findings(self.moment)))

    def test_unknown_slot_is_not_called_empty(self):
        self.state("client")["slots"] = []
        text = "\n".join(tc.state_findings(self.moment))
        self.assertIn("contents unknown", text)
        self.assertNotIn("slot RL has empty", text)

    def test_old_files_and_incomplete_pairs_remain_usable(self):
        host_machine = next(m for m in self.moment.machines if m.head["role"] == "host")
        box = tc.selected_box(host_machine, self.moment)
        box.state = {}
        self.assertIn("no state snapshot", "\n".join(tc.findings(self.moment)))
        self.assertIn("recorded frames over", "\n".join(tc.findings(self.moment)))
        for machine in self.moment.machines:
            tc.selected_box(machine, self.moment).state = {}
        self.assertEqual(tc.state_findings(self.moment), [])

    def test_state_and_events_use_the_same_selected_box_as_frames(self):
        host_machine = next(m for m in self.moment.machines if m.head["role"] == "host")
        duplicate = copy.deepcopy(tc.selected_box(host_machine, self.moment))
        duplicate.source = "short-duplicate.json"
        duplicate.records = []
        duplicate.state["entities"][0]["owner"] = 999
        duplicate.events["rows"] = []
        host_machine.captures.append(duplicate)
        self.assertNotIn("host 999", "\n".join(tc.state_findings(self.moment)))

    def test_event_window_and_escaping(self):
        host_machine = next(m for m in self.moment.machines if m.head["role"] == "host")
        box = tc.selected_box(host_machine, self.moment)
        box.events["rows"].extend([dict(st=50, net=41, kind="too-old"), dict(st=99, net=999, kind="unrelated"), dict(st=100, net=41, kind="<script>")])
        page = tc.render_html(self.moment)
        self.assertNotIn("too-old", page)
        self.assertNotIn("unrelated", page)
        self.assertIn("&lt;script&gt;", page)
        self.assertNotIn("<script>", page)


class StateNoiseTests(unittest.TestCase):
    def setUp(self):
        captures, warnings = tc.load_inputs([FIXTURES.parent / "telemetry-state-noise"])
        self.assertEqual(warnings, [])
        self.moment = tc.group_moments(captures)[0]

    def state(self, role):
        machine = next(m for m in self.moment.machines if m.head["role"] == role)
        return tc.selected_box(machine, self.moment).state

    def radio_lines(self):
        return [line for line in tc.state_findings(self.moment) if "radio" in line]

    def test_resting_objects_with_old_poses_are_silent(self):
        # Includes a small positional difference and a capture without velocity.
        text = "\n".join(tc.state_findings(self.moment))
        self.assertNotIn("Resting car", text)
        self.assertNotIn("Resting prop", text)

    def test_moving_object_with_old_pose_is_reported_on_either_machine(self):
        for moving_role in ("host", "client"):
            with self.subTest(moving_role=moving_role):
                for role in ("host", "client"):
                    self.state(role)["entities"][2]["speed"] = .2 if role == moving_role else 0
                lines = [line for line in tc.state_findings(self.moment) if "Moving car" in line]
                self.assertEqual(len(lines), 1)
                self.assertIn("pose clocks are 20.000 s apart; stale pose or handover", lines[0])

    def test_resting_old_pose_is_reported_only_beyond_position_tolerance(self):
        self.state("client")["entities"][0]["posCm"] = [150, 0, 0]
        text = "\n".join(tc.state_findings(self.moment))
        self.assertIn("Resting car: pose clocks", text)
        self.assertNotIn("Resting car: position differs", text)  # No long extrapolation.
        self.assertNotIn("Resting car", "\n".join(tc.state_findings(self.moment, position_m=2)))

    def test_old_pose_without_velocity_still_reports_a_position_difference(self):
        self.state("client")["entities"][1]["posCm"] = [250, 0, 0]
        self.assertIn("Resting prop: pose clocks", "\n".join(tc.state_findings(self.moment)))

    def test_radio_groups_matching_clips_with_capture_skew_correction(self):
        self.assertEqual(self.radio_lines(), [
            "State radio: all 16 stations play the same clips, client 1 is 4.94 s behind the host (capture-time skew corrected)."
        ])

    def test_radio_skew_alone_is_not_a_disagreement(self):
        # Captured three seconds later, with playback three seconds later.
        self.state("client")["st"] = 103
        for station in self.state("client")["stations"]:
            station["time"] = 24.08
        self.assertEqual(self.radio_lines(), [])

    def test_radio_can_be_ahead_and_old_files_need_no_capture_time(self):
        for state in (self.state("host"), self.state("client")):
            state.pop("st")
        for station in self.state("client")["stations"]:
            station["time"] = 25.08
        self.assertEqual(len(self.radio_lines()), 1)
        self.assertIn("client 1 is 4.00 s ahead of the host", self.radio_lines()[0])
        self.assertIn("capture-time skew unavailable", self.radio_lines()[0])

    def test_radio_lists_only_stations_with_different_clips(self):
        self.state("client")["stations"][0]["clip"] = 100
        lines = self.radio_lines()
        self.assertEqual(len(lines), 2)
        self.assertIn("radio station FM 0 mismatch", lines[0])
        self.assertIn("15 compared stations play the same clips", lines[1])
        self.assertNotIn("all 16", "\n".join(lines))

    def test_radio_variable_offsets_are_summarized_without_claiming_one_offset(self):
        self.state("client")["stations"][0]["time"] = 12
        lines = self.radio_lines()
        self.assertEqual(len(lines), 1)
        self.assertIn("range from +4.94 to +9.58 s", lines[0])
        self.assertNotIn("radio station", lines[0])

    def test_radio_missing_station_or_playback_time_does_not_claim_all(self):
        self.state("client")["stations"].pop()
        self.state("client")["stations"][0].pop("time")
        lines = self.radio_lines()
        self.assertEqual(len(lines), 1)
        self.assertIn("14 compared stations", lines[0])
        self.assertNotIn("all", lines[0])

    def test_findings_order_by_importance_with_summaries_then_events(self):
        missing = dict(self.state("host")["entities"][0], net=99, name="Missing object")
        self.state("host")["entities"].append(missing)
        lines = tc.state_findings(self.moment)
        groups = [
            ("seat occupancy conflict", "owner disagreement", "host has Wheel", "object present on host only", "seat mismatch"),
            ("fuel mismatch", "engine mismatch", "ignition mismatch"),
            ("pose clocks", "position differs"),
            ("time-of-day mismatch", "radio:"),
            ("captured +0.500", "network-event ring overwrote"),
            ("Event st",),
        ]
        previous = []
        for group in groups:
            indices = []
            for phrase in group:
                matches = [i for i, line in enumerate(lines) if phrase in line]
                self.assertTrue(matches, phrase)
                indices.extend(matches)
            if previous:
                self.assertLess(max(previous), min(indices))
            previous = indices

    def test_events_omit_unset_fields_and_keep_valid_zero_ids(self):
        events = [line for line in tc.state_findings(self.moment) if line.startswith("Event")]
        self.assertEqual(events, [
            "Event st 98.000 host: resync-sent net 4 from 0 epoch 1 count 2: ok.",
            "Event st 98.100 client 1: resync-applied net 4 to 1 epoch 1 count 2: ok.",
            "Event st 99.900 client 1: test-desync net 4 item 0 from 0: local unbolt; sync suppressed.",
        ])
        machine = next(m for m in self.moment.machines if m.head["role"] == "host")
        tc.selected_box(machine, self.moment).events["rows"].append(
            dict(st=100, kind="attach", net=4, other=1, item=0, slot=0, to=0))
        self.assertEqual(tc.state_findings(self.moment)[-1],
                         "Event st 100.000 host: attach net 4 other 1 item 0 slot 0 to 0.")


if __name__ == "__main__":
    unittest.main()
