import tempfile
import unittest
import zipfile
from pathlib import Path

from run import BUILD_FAILED, export_problem


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


if __name__ == "__main__":
    unittest.main()
