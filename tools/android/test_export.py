import tempfile
import unittest
import zipfile
from pathlib import Path

import os

import subprocess
import sys

from run import BUILD_FAILED, apk_path, device_args, export_done, export_failure, export_problem, wait_for_export


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


class DeviceArgsTests(unittest.TestCase):
    class _Cfg:
        android_client_data = "/sdcard/Android/data/org.guo.client/files/uo"
        android_account = ""

    class _Paths:
        pass

    def paths(self):
        p = self._Paths()
        p.cfg = self._Cfg()
        return p

    def test_the_data_folder_is_baked_by_default(self):
        self.assertEqual(device_args(self.paths(), "--x"),
                         "-- --play --client-data /sdcard/Android/data/org.guo.client/files/uo --silent --x")

    def test_no_client_data_leaves_it_out(self):
        self.assertEqual(device_args(self.paths(), "--x", client_data=False), "-- --play --silent --x")


class ExportHangTests(unittest.TestCase):
    # The line as Godot 4.7.2 writes it, console colours and all.
    DONE = "\x1b[92m[ DONE ]\x1b[39m \x1b[1mexport\x1b[22m\x1b[39m\x1b[0m\n"

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.log = Path(self.temp.name) / "export.log"

    def sleeper(self, seconds):
        proc = subprocess.Popen([sys.executable, "-c", f"import time; time.sleep({seconds})"])
        self.addCleanup(lambda: proc.poll() is None and proc.kill())
        return proc

    def test_done_is_found_through_the_colours(self):
        self.assertTrue(export_done("[  99% ] export | Verifying APK...\n" + self.DONE))
        self.assertFalse(export_done("[  99% ] export | Verifying APK...\n"))

    def test_a_godot_that_exits_is_waited_for(self):
        self.log.write_text("", encoding="utf-8")
        code, stopped = wait_for_export(self.sleeper(0), self.log, grace=5, limit=30, poll=0.05)
        self.assertEqual((code, stopped), (0, None))

    def test_a_godot_that_hangs_after_done_is_stopped(self):
        self.log.write_text(self.DONE, encoding="utf-8")
        proc = self.sleeper(60)
        code, stopped = wait_for_export(proc, self.log, grace=0.2, limit=30, poll=0.05)
        self.assertIsNone(code)
        self.assertIn("DONE", stopped)
        self.assertIsNotNone(proc.poll())

    def test_a_godot_that_never_finishes_is_stopped_at_the_limit(self):
        self.log.write_text("", encoding="utf-8")
        proc = self.sleeper(60)
        code, stopped = wait_for_export(proc, self.log, grace=5, limit=0.3, poll=0.05)
        self.assertIsNone(code)
        self.assertIn("had not exited", stopped)
        self.assertIsNotNone(proc.poll())


if __name__ == "__main__":
    unittest.main()
