"""Tests for tools/launcher_lint: run with `python tools/launcher_lint/test_launcher_lint.py`."""
from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import run as lint  # noqa: E402

GOOD_BAT = ('@echo off\r\nrem Prints hello; nothing else happens.\r\nrem args: --loud\r\n'
            'call "%~dp0..\\_shared\\common.bat" || exit /b 1\r\necho hello\r\nexit /b 0\r\n')
GOOD_SH = ('#!/usr/bin/env bash\n# Prints hello; nothing else happens.\n# args: --loud\nset -euo pipefail\n'
           '. "$(dirname "$0")/../_shared/common.sh" || exit 1\necho hello\n')
WIN_SH = ('#!/usr/bin/env bash\n# Prints hello; nothing else happens.\n# args: --loud\nset -euo pipefail\n'
          'echo "Windows only" >&2\nexit 2\n')


def git(root: Path, *args: str) -> None:
    subprocess.run(["git", *args], cwd=root, check=True, capture_output=True)


class LauncherLintTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="launcher-lint-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        shared = self.root / "launchers" / "_shared"
        shared.mkdir(parents=True)
        (shared / "config.bat").write_bytes(b'@echo off\r\nif not defined UO_A set "UO_A=1"\r\n')
        (shared / "config.sh").write_bytes(b': "${UO_ROOT:=x}"\n: "${UO_A:=1}"\n')
        (shared / "config.local.sh.example").write_bytes(b"")
        (shared / "common.sh").write_bytes(b"")
        (self.root / "launchers" / "dev").mkdir()
        git(self.root, "init", "--quiet")

    def put(self, name: str, text: str, executable: bool = True) -> None:
        path = self.root / "launchers" / "dev" / name
        path.write_bytes(text.encode())
        git(self.root, "add", str(path))
        if name.endswith(".sh"):
            git(self.root, "update-index", f"--chmod={'+' if executable else '-'}x", str(path))

    def problems(self) -> list[str]:
        launchers, general = lint.collect(self.root)
        return [p for l in launchers for p in l.problems] + general

    def test_a_good_pair_passes_and_is_listed(self):
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", GOOD_SH)
        launchers, general = lint.collect(self.root)
        self.assertEqual(general, [])
        self.assertEqual(launchers[0].problems, [])
        self.assertEqual(launchers[0].description, "Prints hello; nothing else happens.")
        self.assertEqual(launchers[0].args, "--loud")
        self.assertFalse(launchers[0].windows_only)

    def test_windows_only_stub_passes(self):
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", WIN_SH)
        launchers, _ = lint.collect(self.root)
        self.assertEqual(launchers[0].problems, [])
        self.assertTrue(launchers[0].windows_only)

    def test_missing_twin_either_way(self):
        self.put("only_bat.bat", GOOD_BAT)
        self.put("only_sh.sh", GOOD_SH)
        found = self.problems()
        self.assertTrue(any("only_bat.sh is missing" in p for p in found))
        self.assertTrue(any("only_sh.bat is missing" in p for p in found))

    def test_banner_without_description_fails(self):
        self.put("hello.bat", GOOD_BAT.replace("rem Prints hello; nothing else happens.\r\nrem args: --loud\r\n",
                                               "REM =====\r\nREM  Prints hello.\r\n"))
        self.put("hello.sh", GOOD_SH)
        self.assertTrue(any("first line after @echo off" in p for p in self.problems()))

    def test_description_must_be_a_sentence_and_match(self):
        self.put("hello.bat", GOOD_BAT.replace("nothing else happens.", "nothing else happens"))
        self.put("hello.sh", GOOD_SH)
        self.assertTrue(any("full stop" in p for p in self.problems()))
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", GOOD_SH.replace("nothing else", "something else"))
        self.assertTrue(any("differs from the .bat's rem line" in p for p in self.problems()))

    def test_args_lines_must_agree(self):
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", GOOD_SH.replace("# args: --loud\n", ""))
        self.assertTrue(any("args:" in p for p in self.problems()))

    def test_line_endings(self):
        self.put("hello.bat", GOOD_BAT.replace("\r\n", "\n"))
        self.put("hello.sh", GOOD_SH.replace("\n", "\r\n"))
        found = self.problems()
        self.assertTrue(any("must be CRLF" in p for p in found))
        self.assertTrue(any("must be LF" in p for p in found))

    def test_executable_bit_and_untracked(self):
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", GOOD_SH, executable=False)
        self.assertTrue(any("not executable in git" in p for p in self.problems()))
        (self.root / "launchers" / "dev" / "new.bat").write_bytes(GOOD_BAT.encode())
        (self.root / "launchers" / "dev" / "new.sh").write_bytes(GOOD_SH.encode())
        self.assertTrue(any("not in git" in p for p in self.problems()))

    def test_strict_mode_and_common(self):
        self.put("hello.bat", GOOD_BAT.replace('call "%~dp0..\\_shared\\common.bat" || exit /b 1\r\n', ""))
        self.put("hello.sh", GOOD_SH.replace("set -euo pipefail\n", "").replace(
            '. "$(dirname "$0")/../_shared/common.sh" || exit 1\n', ""))
        found = self.problems()
        self.assertTrue(any("never calls _shared\\common.bat" in p for p in found))
        self.assertTrue(any("set -euo pipefail" in p for p in found))
        self.assertTrue(any("never sources _shared/common.sh" in p for p in found))

    def test_stdin_readers_fail(self):
        self.put("hello.bat", GOOD_BAT.replace("echo hello\r\n", "set /p NAME=Name? \r\npause\r\n"))
        self.put("hello.sh", GOOD_SH.replace("echo hello\n", "read -r name\n"))
        found = [p for p in self.problems() if "reads stdin" in p]
        self.assertEqual(len(found), 3)

    def test_a_comment_mentioning_pause_is_fine(self):
        self.put("hello.bat", GOOD_BAT.replace("echo hello\r\n", "REM  no pause here: agents have no stdin\r\necho hello\r\n"))
        self.put("hello.sh", GOOD_SH.replace("echo hello\n", "# never read stdin\necho hello\n"))
        self.assertEqual(self.problems(), [])

    def test_config_keys_must_match(self):
        self.put("hello.bat", GOOD_BAT)
        self.put("hello.sh", GOOD_SH)
        (self.root / "launchers" / "_shared" / "config.sh").write_bytes(b': "${UO_B:=1}"\n')
        found = self.problems()
        self.assertTrue(any("config.sh lacks UO_A" in p for p in found))
        self.assertTrue(any("config.sh has UO_B" in p for p in found))

    def test_the_repository_passes(self):
        root = HERE.parents[1]
        if shutil.which("git") is None or not (root / ".git").exists():
            self.skipTest("not a git checkout")
        launchers, general = lint.collect(root)
        self.assertEqual([f"{l.key}: {p}" for l in launchers for p in l.problems] + general, [])


if __name__ == "__main__":
    unittest.main()
