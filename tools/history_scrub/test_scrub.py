"""Synthetic merge/boundary regression; contains no real personal data."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("scrub", Path(__file__).with_name("run.py"))
scrub = importlib.util.module_from_spec(spec); spec.loader.exec_module(scrub)


class ScrubTest(unittest.TestCase):
    def test_unpublished_merge_and_boundary(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def git(*args):
                return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.DEVNULL).decode().strip()
            git("init", "-b", "main")
            git("config", "user.name", "Synthetic test")
            git("config", "user.email", "test@example.invalid")
            (root / "base.txt").write_text("safe baseline\n")
            git("add", "."); git("commit", "-m", "baseline")
            baseline = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/main", baseline)
            (root / "private.txt").write_text("synthetic-private-token\n")
            git("add", "."); git("commit", "-m", "synthetic-private-token in message")
            git("checkout", "-b", "side")
            (root / "side.txt").write_text("synthetic-private-token side\n")
            git("add", "."); git("commit", "-m", "side")
            git("checkout", "main")
            (root / "main.txt").write_text("safe\n")
            git("add", "."); git("commit", "-m", "main")
            git("merge", "--no-ff", "side", "-m", "merge")
            tip = git("rev-parse", "main")
            result = scrub.rewrite(root, root / "build/scrub", ["main"], [(b"synthetic-private-token", b"redacted")], publish=True, expected=tip)
            self.assertTrue(result["verified"])
            self.assertEqual(git("rev-parse", "main"), tip)
            self.assertEqual(git("rev-parse", "origin/main"), baseline)
            self.assertEqual(git("show", "refs/scrub/main:private.txt"), "redacted")
            self.assertEqual(len(git("show", "-s", "--format=%P", "refs/scrub/main").split()), 2)
            git("merge-base", "--is-ancestor", baseline, "refs/scrub/main")
            self.assertTrue((root / "build/scrub/commit-map.txt").is_file())


if __name__ == "__main__":
    unittest.main()
