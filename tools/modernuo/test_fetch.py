"""launchers/shard/fetch.bat and fetch.sh, run for real on a throwaway clone (SF3b).

Each script is copied into a scratch tree with a stub common.bat/common.sh and
one toy patch (it edits a.txt and adds b.txt, as patches/0001 adds a file), and
pointed at a local upstream through UO_SHARD_REPO/REF/SRC. The rule, as SF3
gave tools/muo_shard/plans.py: a patch that applies is applied; one that comes
off in reverse is already applied; one that does neither is fatal, with git's
reason. Before the pin moves, a patch that will not come off in reverse has its
files reset to HEAD (a file it adds is removed). The real ModernUO checkout is
never touched. fetch.sh needs bash, fetch.bat needs Windows.

    python tools/modernuo/test_fetch.py
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
LAUNCHERS = ROOT / "launchers" / "shard"

GIT = shutil.which("git")
GITC = [GIT or "git", "-c", "user.name=t", "-c", "user.email=test", "-c", "core.autocrlf=false"]

COMMON_SH = 'UO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"\n'
COMMON_BAT = '@echo off\r\nfor %%I in ("%~dp0..\\..") do set "UO_ROOT=%%~fI"\r\nexit /b 0\r\n'

PATCHED_A = "one\nTWO\n"
NEW_B = "new\n"


def _bash() -> str | None:
    # On Windows prefer Git's bash: the one on PATH can be WSL's, which cannot read these paths.
    if os.name == "nt" and GIT:
        for cand in (Path(GIT).parents[1] / "bin" / "bash.exe", Path(GIT).parent / "bash.exe"):
            if cand.is_file():
                return str(cand)
    return shutil.which("bash")


def git(*args: str, cwd: Path | None = None) -> str:
    r = subprocess.run(GITC + list(args), cwd=cwd, check=True, capture_output=True, text=True)
    return r.stdout.strip()


class Scratch:
    """An upstream with two commits (old, pin), a clone of it, and a scratch launcher tree."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        up = tmp / "upstream"
        up.mkdir()
        git("init", "-q", str(up))
        (up / "a.txt").write_bytes(b"one\ntwo\n")
        (up / "c.txt").write_bytes(b"old\n")
        git("add", "-A", cwd=up)
        git("commit", "-qm", "old", cwd=up)
        self.old = git("rev-parse", "HEAD", cwd=up)
        (up / "c.txt").write_bytes(b"pin\n")
        git("commit", "-qam", "pin", cwd=up)
        self.pin = git("rev-parse", "HEAD", cwd=up)

        # the patch: a.txt two -> TWO, and a new b.txt
        (up / "a.txt").write_bytes(PATCHED_A.encode())
        (up / "b.txt").write_bytes(NEW_B.encode())
        git("add", "-A", cwd=up)
        self.patch = subprocess.run(GITC + ["-C", str(up), "diff", "--cached"], check=True, capture_output=True).stdout
        git("reset", "-q", "--hard", cwd=up)
        (up / "b.txt").unlink(missing_ok=True)

        self.root = tmp / "root"
        (self.root / "launchers" / "shard").mkdir(parents=True)
        (self.root / "launchers" / "_shared").mkdir(parents=True)
        (self.root / "tools" / "modernuo" / "patches").mkdir(parents=True)
        (self.root / "tools" / "modernuo" / "patches" / "0001-x.patch").write_bytes(self.patch)
        for name in ("fetch.sh", "fetch.bat"):
            shutil.copyfile(LAUNCHERS / name, self.root / "launchers" / "shard" / name)
        (self.root / "launchers" / "_shared" / "common.sh").write_bytes(COMMON_SH.encode())
        (self.root / "launchers" / "_shared" / "common.bat").write_bytes(COMMON_BAT.encode())

        self.upstream = up
        self.src = tmp / "src"
        git("clone", "-q", "--no-checkout", str(up), str(self.src))
        git("config", "core.autocrlf", "false", cwd=self.src)

    def at(self, commit: str) -> "Scratch":
        git("checkout", "-q", "--detach", commit, cwd=self.src)
        return self

    def write(self, rel: str, text: str) -> None:
        (self.src / rel).write_bytes(text.encode())

    def read(self, rel: str) -> str | None:
        f = self.src / rel
        return f.read_bytes().decode() if f.exists() else None

    def head(self) -> str:
        return git("rev-parse", "HEAD", cwd=self.src)

    def run(self, kind: str) -> subprocess.CompletedProcess:
        env = dict(os.environ)
        env.update(
            UO_SHARD_REPO=self.upstream.as_posix(),
            UO_SHARD_REF=self.pin,
            UO_SHARD_SRC=self.src.as_posix() if kind == "sh" else str(self.src),
            LC_ALL="C",
            LANG="C",
            GIT_CONFIG_COUNT="1",
            GIT_CONFIG_KEY_0="core.autocrlf",
            GIT_CONFIG_VALUE_0="false",
        )
        script = self.root / "launchers" / "shard" / f"fetch.{kind}"
        cmd = [_bash(), script.as_posix()] if kind == "sh" else ["cmd", "/d", "/c", str(script)]
        return subprocess.run(cmd, cwd=self.tmp, env=env, capture_output=True, text=True)


class FetchCases:
    kind = ""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.s = Scratch(Path(self._tmp.name))

    def tearDown(self):
        self._tmp.cleanup()

    def out(self, r) -> str:
        return r.stdout + r.stderr

    def test_patch_that_applies_is_applied(self):
        s = self.s.at(self.s.pin)
        r = s.run(self.kind)
        self.assertEqual(r.returncode, 0, self.out(r))
        self.assertEqual((s.read("a.txt"), s.read("b.txt")), (PATCHED_A, NEW_B))
        self.assertIn("[shard] Done.", r.stdout)

    def test_patch_already_applied_is_skipped(self):
        s = self.s.at(self.s.pin)
        s.write("a.txt", PATCHED_A)
        s.write("b.txt", NEW_B)
        r = s.run(self.kind)
        self.assertEqual(r.returncode, 0, self.out(r))
        self.assertIn("already applied, skipping", r.stdout)
        self.assertEqual((s.read("a.txt"), s.read("b.txt")), (PATCHED_A, NEW_B))

    def test_patch_that_neither_applies_nor_reverses_is_fatal(self):
        # an older copy of the patch is on: before SF3b this said "already applied" and went on
        s = self.s.at(self.s.pin)
        s.write("a.txt", "one\nTwo, the old way\n")
        s.write("b.txt", "old new\n")
        r = s.run(self.kind)
        self.assertNotEqual(r.returncode, 0, self.out(r))
        self.assertNotIn("already applied, skipping", r.stdout)
        self.assertNotIn("[shard] Done.", r.stdout)
        self.assertIn("FATAL: 0001-x.patch does not apply to the checkout at " + s.pin[:9], r.stdout)
        self.assertRegex(r.stderr, r"patch failed|already exists in working directory")  # git's own reason
        self.assertEqual((s.read("a.txt"), s.read("b.txt")), ("one\nTwo, the old way\n", "old new\n"))

    def test_pin_move_takes_a_clean_patch_off_and_puts_it_back(self):
        s = self.s.at(self.s.old)
        s.write("a.txt", PATCHED_A)
        s.write("b.txt", NEW_B)
        r = s.run(self.kind)
        self.assertEqual(r.returncode, 0, self.out(r))
        self.assertNotIn("resetting its files", r.stdout)
        self.assertEqual(s.head(), s.pin)
        self.assertEqual((s.read("a.txt"), s.read("b.txt"), s.read("c.txt")), (PATCHED_A, NEW_B, "pin\n"))

    def test_pin_move_resets_the_files_of_a_patch_that_will_not_reverse(self):
        s = self.s.at(self.s.old)
        s.write("a.txt", "one\nTwo, the old way\n")
        s.write("b.txt", "old new\n")
        r = s.run(self.kind)
        self.assertEqual(r.returncode, 0, self.out(r))
        self.assertIn("0001-x.patch does not come off in reverse; resetting its files", r.stdout)
        self.assertEqual(s.head(), s.pin)
        self.assertEqual((s.read("a.txt"), s.read("b.txt"), s.read("c.txt")), (PATCHED_A, NEW_B, "pin\n"))


@unittest.skipUnless(GIT and _bash(), "needs git and bash")
class FetchShTest(FetchCases, unittest.TestCase):
    kind = "sh"


@unittest.skipUnless(GIT and os.name == "nt", "needs git and Windows")
class FetchBatTest(FetchCases, unittest.TestCase):
    kind = "bat"


if __name__ == "__main__":
    unittest.main()
