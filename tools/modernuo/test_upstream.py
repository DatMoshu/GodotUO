"""The ModernUO patch list stays complete and every patch applies (MU1).

UPSTREAM.md names every file in patches/ (what fetch.bat applies) and in
upstream/ (the versions held for an upstream bundle). Each of those applies
cleanly to the pinned ModernUO (UO_SHARD_REF), checked against a throwaway
index so the checkout's working tree is never touched. The upstream versions
carry nothing GUO-specific. The apply checks are skipped when no ModernUO
checkout holding the pin is around (a fresh clone before fetch.bat).

    python tools/modernuo/test_upstream.py
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from guo.config import main_checkout  # noqa: E402

PATCHES = sorted((HERE / "patches").glob("*.patch"))
UPSTREAM = sorted((HERE / "upstream").glob("*.patch"))
UPSTREAM_MD = HERE / "UPSTREAM.md"


def shard_ref() -> str:
    if os.environ.get("UO_SHARD_REF"):
        return os.environ["UO_SHARD_REF"]
    text = (ROOT / "launchers" / "_shared" / "config.bat").read_text(encoding="utf-8")
    match = re.search(r'set "UO_SHARD_REF=([0-9a-f]+)"', text)
    if not match:
        raise AssertionError("UO_SHARD_REF is not in config.bat")
    return match.group(1)


def shard_src() -> Path | None:
    """A ModernUO checkout holding the pin: UO_SHARD_SRC, this checkout's, or the main checkout's."""
    candidates = []
    if os.environ.get("UO_SHARD_SRC"):
        candidates.append(Path(os.environ["UO_SHARD_SRC"]))
    candidates.append(ROOT / "tools" / "modernuo" / "src")
    main = main_checkout(ROOT)
    if main:
        candidates.append(main / "tools" / "modernuo" / "src")
    ref = shard_ref()
    for src in candidates:
        if not (src / ".git").exists():
            continue
        found = subprocess.run(
            ["git", "-C", str(src), "cat-file", "-e", f"{ref}^{{commit}}"],
            capture_output=True,
        )
        if found.returncode == 0:
            return src
    return None


def apply_check(src: Path, ref: str, patches: list[Path]) -> subprocess.CompletedProcess:
    """git apply --check of patches, in order, on the pin, in a temporary index."""
    with tempfile.TemporaryDirectory(prefix="guo-mu1-") as tmp:
        env = dict(os.environ, GIT_INDEX_FILE=str(Path(tmp) / "index"))
        subprocess.run(["git", "-C", str(src), "read-tree", ref], env=env, check=True)
        return subprocess.run(
            ["git", "-C", str(src), "apply", "--cached", "--check", *map(str, patches)],
            env=env,
            capture_output=True,
            text=True,
        )


class PatchListTests(unittest.TestCase):
    def test_every_patch_is_listed(self):
        listed = UPSTREAM_MD.read_text(encoding="utf-8")
        for patch in PATCHES + UPSTREAM:
            rel = patch.relative_to(HERE).as_posix()
            with self.subTest(patch=rel):
                self.assertIn(f"`{rel}`", listed, f"{rel} is not in UPSTREAM.md")

    def test_every_listed_patch_exists(self):
        listed = UPSTREAM_MD.read_text(encoding="utf-8")
        for rel in sorted(set(re.findall(r"`((?:patches|upstream)/[^`]+\.patch)`", listed))):
            with self.subTest(patch=rel):
                self.assertTrue((HERE / rel).is_file(), f"UPSTREAM.md lists {rel}, which does not exist")

    def test_upstream_versions_are_not_guo_specific(self):
        self.assertTrue(UPSTREAM, "upstream/ holds no patches")
        for patch in UPSTREAM:
            text = patch.read_text(encoding="utf-8")
            added = [line for line in text.splitlines() if line.startswith("+") and not line.startswith("+++")]
            with self.subTest(patch=patch.name):
                for line in added:
                    self.assertNotRegex(line, r"GUO|UO_SHARD_", f"GUO-specific line in {patch.name}: {line}")
                    self.assertNotIn("﻿", line, f"{patch.name} adds a byte-order mark")


class ApplyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ref = shard_ref()
        cls.src = shard_src()
        if cls.src is None:
            raise unittest.SkipTest(f"no ModernUO checkout holding {cls.ref[:9]} (run launchers\\shard\\fetch.bat)")

    def test_patches_apply_in_order(self):
        result = apply_check(self.src, self.ref, PATCHES)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_each_upstream_version_applies_alone(self):
        for patch in UPSTREAM:
            with self.subTest(patch=patch.name):
                result = apply_check(self.src, self.ref, [patch])
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_upstream_versions_apply_together(self):
        result = apply_check(self.src, self.ref, UPSTREAM)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
