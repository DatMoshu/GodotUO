import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("guo_release", Path(__file__).with_name("run.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

class ReleaseTests(unittest.TestCase):
    def check_identity(self, version="0.1.0-alpha.1", code=1, tag=None):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "version.json").write_text(json.dumps({"version": version, "android_version_code": code}))
            return release.identity(root, tag)

    def test_matching_tags(self):
        for version in ("0.1.0-alpha.1", "0.1.0-beta.2", "0.1.0-rc.1", "1.0.0"):
            self.assertEqual(self.check_identity(version, tag="v"+version)[0], version)

    def test_mismatched_tag_rejected(self):
        with self.assertRaises(ValueError):
            self.check_identity(tag="v9.0.0")

    def test_invalid_versions_rejected(self):
        for version in ("01.0.0", "1.2", "1.2.3-rc.0", "1.2.3-rc.01", "65536.0.0", "1.2.3/evil"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                self.check_identity(version)

    def test_invalid_android_codes_rejected(self):
        for code in (0, -1, True, "1", 2100000001):
            with self.subTest(code=code), self.assertRaises(ValueError):
                self.check_identity(code=code)

    def test_platform_fields_preserve_prerelease_and_numeric_versions(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            inputs = {
                "godot/GUO/project.godot": '[application]\nconfig/name="GUO"\n',
                "tools/android/export_presets.template.cfg": 'version/code=1\nversion/name=""\n',
                "tools/windows/export_presets.template.cfg": 'application/file_version=""\napplication/product_version=""\n',
            }
            for name, text in inputs.items():
                path=root/name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text(text)
            files=release.expected(root, "1.2.3-rc.4", 17)
            self.assertIn('config/version="1.2.3-rc.4"', files["godot/GUO/project.godot"])
            self.assertIn('version/code=17', files["tools/android/export_presets.template.cfg"])
            self.assertIn('application/file_version="1.2.3.0"', files["tools/windows/export_presets.template.cfg"])
            self.assertIn('<Version>1.2.3-rc.4</Version>', files["tools/msbuild/Version.props"])
            for name, text in files.items():
                path=root/name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text(text)
            self.assertEqual(files, release.expected(root, "1.2.3-rc.4", 17))

if __name__ == "__main__":
    unittest.main()
