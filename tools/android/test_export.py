import tempfile
import unittest
import zipfile
from pathlib import Path

import os

from run import BUILD_FAILED, apk_path, export_failure, export_problem


class ExportProblemTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.apk = Path(self.temp.name) / "GUO.apk"

    def pack(self, *names):
        with zipfile.ZipFile(self.apk, "w") as z:
            for n in names:
                z.writestr(n, b"x")

    def test_a_build_with_the_assembly_passes(self):
        self.pack("classes.dex", "assets/.godot/mono/publish/arm64/GUO.dll")
        self.assertIsNone(export_problem("exported", self.apk))

    def test_a_failed_csharp_build_fails_even_with_an_apk(self):
        self.pack("assets/.godot/mono/publish/arm64/GUO.dll")
        self.assertIn(BUILD_FAILED, export_problem(f"ERROR: {BUILD_FAILED}. Check MSBuild", self.apk))

    def test_an_apk_without_the_assembly_fails(self):
        self.pack("classes.dex", "assets/project.binary")
        self.assertIn("no GUO.dll", export_problem("exported", self.apk))

    def test_a_broken_apk_fails(self):
        self.apk.write_bytes(b"not a zip")
        self.assertIn("not a valid zip", export_problem("", self.apk))


class ApkPathTests(unittest.TestCase):
    def test_a_relative_out_is_read_from_where_run_py_started(self):
        # Not from the Godot project, which is where Godot itself would read it.
        apk = apk_path(Path("build/android/x.apk"))
        self.assertTrue(apk.is_absolute())
        self.assertEqual(apk, Path(os.getcwd()).resolve() / "build" / "android" / "x.apk")

    def test_an_absolute_out_is_kept(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(apk_path(Path(d) / "x.apk"), (Path(d) / "x.apk").resolve())

    def test_a_missing_folder_names_the_path(self):
        apk = Path("C:/nowhere/x.apk")
        message = export_failure(1, 'ERROR: Export: Target folder does not exist or is inaccessible: "build\android"', apk)
        self.assertIn(str(apk), message)

    def test_any_other_failure_names_the_path(self):
        apk = Path("C:/somewhere/x.apk")
        self.assertIn(str(apk), export_failure(1, "", apk))


if __name__ == "__main__":
    unittest.main()
