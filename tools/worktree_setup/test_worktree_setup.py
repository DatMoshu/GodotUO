import tempfile
import unittest
from pathlib import Path

import run


class CopyLocalFilesTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.main = Path(tmp.name) / "main"
        self.tree = Path(tmp.name) / "tree"
        self.tree.mkdir()
        for rel in run.LOCAL_FILES:
            (self.main / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.main / rel).write_text(f"main {rel.name}", encoding="utf-8")

    def test_claude_settings_are_a_local_file(self):
        self.assertIn(Path(".claude/settings.local.json"), run.LOCAL_FILES)

    def test_missing_files_are_copied_from_the_main_checkout(self):
        rows = run.copy_local_files(self.tree, self.main)
        for rel in run.LOCAL_FILES:
            self.assertEqual((self.tree / rel).read_text(encoding="utf-8"), f"main {rel.name}")
        self.assertTrue(all(status == "ok" and "copied" in detail for status, _, detail in rows))

    def test_an_existing_file_is_never_overwritten(self):
        own = self.tree / ".claude/settings.local.json"
        own.parent.mkdir(parents=True)
        own.write_text("mine", encoding="utf-8")
        rows = run.copy_local_files(self.tree, self.main)
        self.assertEqual(own.read_text(encoding="utf-8"), "mine")
        self.assertIn(("ok", str(Path(".claude/settings.local.json")), "present"), rows)

    def test_the_main_checkout_copies_nothing(self):
        rows = run.copy_local_files(self.tree, None)
        self.assertEqual({status for status, _, _ in rows}, {"--"})
        self.assertFalse((self.tree / ".claude").exists())


if __name__ == "__main__":
    unittest.main()
