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


if __name__ == "__main__":
    unittest.main()
