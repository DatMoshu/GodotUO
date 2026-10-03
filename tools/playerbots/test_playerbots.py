"""Integration checks for non-destructive deployment and patch failure handling."""
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest

from run import configure, copy_plan, pending_patches


class PlayerBotsTests(unittest.TestCase):
    def test_copy_plan_preserves_owner_edits(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, target = root / "source", root / "target"
            source.mkdir()
            target.mkdir()
            (source / "bot.cs").write_text("upstream")
            (target / "bot.cs").write_text("owner change")
            with self.assertRaisesRegex(RuntimeError, "Refusing to overwrite"):
                copy_plan(source, target)
            self.assertEqual((target / "bot.cs").read_text(), "owner change")

    def test_copy_plan_accepts_identical_rerun(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source, target = root / "source", root / "target"
            source.mkdir()
            target.mkdir()
            (source / "bot.cs").write_text("upstream")
            (target / "bot.cs").write_text("upstream")
            self.assertEqual(len(copy_plan(source, target)), 1)

    def test_patch_conflict_does_not_apply_earlier_patch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            (root / "file.txt").write_text("original\n")
            good = root / "good.patch"
            good.write_text("--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-original\n+changed\n")
            bad = root / "bad.patch"
            bad.write_text("--- a/missing.txt\n+++ b/missing.txt\n@@ -1 +1 @@\n-no\n+yes\n")
            with self.assertRaises(subprocess.CalledProcessError):
                pending_patches(root, [good, bad])
            self.assertEqual((root / "file.txt").read_text(), "original\n")

    def test_patch_rerun_recognizes_already_applied(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            (root / "file.txt").write_text("changed\n")
            patch = root / "patch.diff"
            patch.write_text("--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-original\n+changed\n")
            self.assertEqual(pending_patches(root, [patch]), [])

    def test_configuration_is_loopback_and_preserves_edits(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            cfg = SimpleNamespace(client_data=root, playerbots_port=2640)
            dist = root / "Distribution"
            configure(cfg, dist)
            path = dist / "Configuration/modernuo.json"
            data = json.loads(path.read_text())
            self.assertEqual(data["listeners"], ["127.0.0.1:2640"])
            self.assertEqual(data["settings"]["pingServer.enabled"], "False")
            data["settings"]["guo.playerbots.population"] = "12"
            path.write_text(json.dumps(data))
            configure(cfg, dist)
            self.assertEqual(json.loads(path.read_text())["settings"]["guo.playerbots.population"], "12")


if __name__ == "__main__":
    unittest.main()
