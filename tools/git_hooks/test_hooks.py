"""Exercise opt-in installation and real checkers in disposable repositories."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from check import check, git
from install import install, MARKER

ROOT = Path(__file__).resolve().parents[2]


class HookTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "build/git_hooks"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="tests-", dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        git(self.root, "init", "--quiet")
        git(self.root, "config", "user.name", "Fixture")
        git(self.root, "config", "user.email", "test@example.com")
        git(self.root, "config", "commit.gpgsign", "false")
        for tool in ("docs_lint", "launcher_lint", "privacy_scan"):
            shutil.copytree(ROOT / "tools" / tool, self.root / "tools" / tool,
                            ignore=shutil.ignore_patterns("deny.local.txt", "__pycache__"))
        (self.root / "README.md").write_text("# Fixture\n", encoding="utf-8")
        (self.root / ".gitignore").write_text("build/\ntools/privacy_scan/deny.local.txt\n", encoding="utf-8")
        self.commit()

    def commit(self):
        git(self.root, "add", ".")
        git(self.root, "commit", "--quiet", "-m", "Fixture")
        return git(self.root, "rev-parse", "HEAD")

    def updates(self, tip=None):
        return f"refs/heads/test {tip or git(self.root, 'rev-parse', 'HEAD')} refs/heads/test {'0' * 40}\n"

    def test_install_remove_and_existing_hook_preservation(self):
        install(self.root)
        hook = self.root / ".git/hooks/pre-push"
        self.assertIn(MARKER, hook.read_text(encoding="utf-8"))
        self.assertNotIn(b"\r\n", hook.read_bytes())
        install(self.root, uninstall=True)
        self.assertFalse(hook.exists())
        hook.write_text("#!/bin/sh\n# somebody else's hook\n", encoding="utf-8")
        before = hook.read_bytes()
        with self.assertRaises(ValueError):
            install(self.root)
        self.assertEqual(hook.read_bytes(), before)

    def test_existing_hooks_manager_is_preserved(self):
        git(self.root, "config", "core.hooksPath", "custom-hooks")
        with self.assertRaises(ValueError):
            install(self.root)
        self.assertEqual(git(self.root, "config", "--get", "core.hooksPath"), "custom-hooks")

    def test_dirty_working_copy_does_not_change_committed_checks(self):
        tip = git(self.root, "rev-parse", "HEAD")
        (self.root / "README.md").write_text("[broken](missing.md)\n", encoding="utf-8")
        before = git(self.root, "status", "--porcelain")
        with patch.dict(os.environ, {"GIT_DIR": str(self.root / ".git"), "GIT_INDEX_FILE": str(self.root / ".git/index")}):
            self.assertEqual(check(self.root, self.updates(tip)), 0)
        self.assertEqual(git(self.root, "rev-parse", "HEAD"), tip)
        self.assertEqual(git(self.root, "status", "--porcelain"), before)
        self.assertIn("missing.md", (self.root / "README.md").read_text())

    def test_committed_broken_link_fails_despite_working_fix(self):
        (self.root / "README.md").write_text("[broken](missing.md)\n", encoding="utf-8")
        tip = self.commit()
        (self.root / "README.md").write_text("# Fixed but not committed\n", encoding="utf-8")
        self.assertEqual(check(self.root, self.updates(tip)), 1)

    def test_private_deny_list_is_used_without_committing_it(self):
        (self.root / "README.md").write_text("# FIXTURE_DENIED_TOKEN\n", encoding="utf-8")
        tip = self.commit()
        (self.root / "tools/privacy_scan/deny.local.txt").write_text("FIXTURE_DENIED_TOKEN\n", encoding="utf-8")
        self.assertEqual(check(self.root, self.updates(tip)), 1)
        self.assertNotIn("deny.local.txt", git(self.root, "ls-files"))

    def test_deletion_and_malformed_input(self):
        self.assertEqual(check(self.root, f"(delete) {'0' * 40} refs/heads/test {'1' * 40}\n"), 0)
        with self.assertRaises(ValueError):
            check(self.root, "not a ref update\n")

    def test_multiple_pushed_tips_include_failing_branch(self):
        clean = git(self.root, "rev-parse", "HEAD")
        (self.root / "README.md").write_text("[broken](missing.md)\n", encoding="utf-8")
        bad = self.commit()
        updates = self.updates(clean) + f"refs/heads/other {bad} refs/heads/other {'0' * 40}\n"
        self.assertEqual(check(self.root, updates), 1)

    def test_intermediate_commit_with_program_or_machine_path_is_blocked(self):
        # A clean tip does not excuse an earlier pushed commit: GitHub publishes them all.
        base = git(self.root, "rev-parse", "HEAD")
        (self.root / "leak.dll").write_bytes(b"MZ junk C:" + bytes([92]) + b"Users" + bytes([92]) + b"someone")
        self.commit()
        git(self.root, "rm", "--quiet", "leak.dll")
        git(self.root, "commit", "--quiet", "-m", "remove")
        clean_tip = git(self.root, "rev-parse", "HEAD")
        self.assertEqual(check(self.root, f"refs/heads/test {clean_tip} refs/heads/test {base}\n"), 1)
        # The same push without the leaking commits passes.
        self.assertEqual(check(self.root, f"refs/heads/test {base} refs/heads/test {'0' * 40}\n"), 0)


if __name__ == "__main__":
    unittest.main()
