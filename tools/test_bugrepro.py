"""Tests for tools/bugrepro.py: the unpack is safe, the world is installed under the neutral name, a client's bundle is refused."""
import pathlib, sys, tempfile, unittest, zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import bugrepro


def make_zip(path, files):
    with zipfile.ZipFile(path, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)


class BugRepro(unittest.TestCase):
    def test_unpack_and_install(self):
        with tempfile.TemporaryDirectory() as d:
            d = pathlib.Path(d)
            make_zip(d / "b.zip", {"REPORT.md": "# Bug report", "repro.json": "{}",
                                   "world/start/TLDsaveData_26_10_27_0158.tlds": "world",
                                   "world/start/TLDsaveData_26_10_27_0158.tlds.leases": "leases",
                                   "world/start/TLDsaveProp_26_10_27_0158.tldsp": "prop"})
            folder = bugrepro.unpack(d / "b.zip", d / "out")
            self.assertTrue((folder / "REPORT.md").is_file())
            saves = d / "Saves"; saves.mkdir()
            names = bugrepro.install(folder, saves)
            self.assertEqual(sorted(names), ["TLDsaveData_bugrepro.tlds", "TLDsaveData_bugrepro.tlds.leases", "TLDsaveProp_bugrepro.tldsp"])
            self.assertEqual((saves / "TLDsaveData_bugrepro.tlds").read_text(), "world")

    def test_a_bundle_without_a_world_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            d = pathlib.Path(d)
            make_zip(d / "b.zip", {"REPORT.md": "x", "repro.json": "{}"})
            folder = bugrepro.unpack(d / "b.zip", d / "out")
            (d / "Saves").mkdir()
            with self.assertRaises(ValueError):
                bugrepro.install(folder, d / "Saves")

    def test_unpack_refuses_names_that_leave_the_folder(self):
        with tempfile.TemporaryDirectory() as d:
            d = pathlib.Path(d)
            for bad in ("../evil.txt", "/abs.txt", "a\\b.txt", "c:x.txt", "a/../../x"):
                make_zip(d / "bad.zip", {bad: "x"})
                with self.assertRaises(ValueError, msg=bad):
                    bugrepro.unpack(d / "bad.zip", d / "out")
            self.assertFalse((d / "evil.txt").exists())

    def test_install_into_a_missing_folder_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(ValueError):
                bugrepro.install(pathlib.Path(d), pathlib.Path(d) / "nope")


COLS = "t st kind net other item slot x y z a b c d e f g h".split()


def drive(t, throttle, f, rpm, speed):
    return [t, t, 26, 1, 0, 0, 0, throttle, 0, 0, 0, 0, 0, 0, 0, f, rpm, speed]


class DriveReport(unittest.TestCase):
    def test_unpack_matches_the_game_packing(self):
        # engine on, driving, 2 of 4 driven wheels grounded, gear 3, clutch 87 %, handbrake 100 %, motor and brake torque
        f = 1 | 8 | 16 | 2 << 5 | 4 << 9 | 4 << 13 | 87 << 17 | 100 << 24 | 1 << 31 | 1 << 32
        c = bugrepro.unpack_car(f)
        self.assertEqual((c["engine"], c["kinematic"], c["grounded_driven"], c["driven_wheels"], c["gear"], c["clutch_pct"], c["handbrake_pct"], c["motor_torque"], c["brake_torque"]),
                         (True, False, 2, 4, 3, 87, 100, True, True))

    def test_a_held_clutch_is_the_suspect(self):
        held = 1 | 8 | 4 << 13 | 100 << 17 | 4 << 5 | 4 << 9 | 1 << 31          # engine on, gear 3, clutch 100 %
        good = 1 | 8 | 5 << 13 | 0 << 17 | 4 << 5 | 4 << 9 | 1 << 31
        rows = [drive(i / 10, 1.0, held, 3000, 0.0) for i in range(20)] + [drive(5 + i / 10, 1.0, good, 3000, 40.0) for i in range(20)]
        text = "\n".join(bugrepro.drive_report({"cols": COLS, "rows": rows}))
        self.assertIn("20 with the throttle past half and the car still", text)
        line = [l for l in text.splitlines() if "clutch applied" in l][0]
        self.assertIn("100%", line)
        self.assertIn("(moving: 0%)", line)

    def test_a_recording_without_car_state_says_so(self):
        rows = [drive(i / 10, 1.0, 0, 0, 0) for i in range(5)]
        self.assertIn("no car state", bugrepro.drive_report({"cols": COLS, "rows": rows})[0])
        self.assertEqual(bugrepro.drive_report({"cols": COLS, "rows": []}), ["no driving rows"])

    def test_a_car_that_always_moved(self):
        rows = [drive(i / 10, 1.0, 1 | 8, 3000, 50.0) for i in range(5)]
        self.assertIn("moved whenever", "\n".join(bugrepro.drive_report({"cols": COLS, "rows": rows})))


if __name__ == "__main__":
    unittest.main()
