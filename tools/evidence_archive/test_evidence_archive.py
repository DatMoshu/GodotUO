"""Tests for tools/evidence_archive: copy, verify, tamper detection, refusals."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run  # noqa: E402


def git(cwd: Path, *args: str) -> None:
    subprocess.run(["git", "-C", str(cwd), *args], check=True, capture_output=True)


@pytest.fixture()
def repo(tmp_path: Path) -> tuple[Path, Path]:
    main = tmp_path / "main"
    main.mkdir()
    git(main, "init", "-q", "-b", "main")
    git(main, "config", "user.email", "test@example.com")
    git(main, "config", "user.name", "t")
    (main / ".gitignore").write_text("build/\n")
    git(main, "add", ".")
    git(main, "commit", "-q", "-m", "init")
    wt = tmp_path / "wt"
    git(main, "worktree", "add", "-q", "-b", "work/x", str(wt))
    (wt / "build" / "worker_a").mkdir(parents=True)
    (wt / "build" / "worker_a" / "smoke.log").write_text("[smoke] OK\n")
    (wt / "build" / "worker_a" / "report.json").write_text('{"ok": true}\n')
    return main, wt


def test_archive_copies_and_verifies(repo, capsys):
    main, wt = repo
    evidence = main / "build" / "director_evidence"
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a1"]) == 0
    dest = evidence / "a1"
    assert (dest / "build" / "worker_a" / "smoke.log").read_text() == "[smoke] OK\n"
    assert (dest / run.MANIFEST).is_file() and (dest / run.SOURCE).is_file()
    assert (wt / "build" / "worker_a" / "smoke.log").is_file()  # the source is untouched
    assert run.main(["--dir", str(evidence), "verify", "a1"]) == 0


def test_verify_detects_a_changed_file(repo):
    main, wt = repo
    evidence = main / "build" / "director_evidence"
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a1"]) == 0
    (evidence / "a1" / "build" / "worker_a" / "report.json").write_text('{"ok": false}\n')
    assert run.main(["--dir", str(evidence), "verify", "a1"]) == 1


def test_refuses_an_evidence_folder_inside_a_worktree(repo):
    main, wt = repo
    assert run.main(["--dir", str(wt / "keep"), "archive", str(wt), "a1"]) == 1
    assert not (wt / "keep").exists()


def test_refuses_an_existing_name_and_paths_outside(repo):
    main, wt = repo
    evidence = main / "build" / "director_evidence"
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a1"]) == 0
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a1"]) == 1
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a2", "--paths", "../main"]) == 2
    assert run.main(["--dir", str(evidence), "archive", str(wt), "../bad"]) == 2


def test_size_limit(repo):
    main, wt = repo
    (wt / "build" / "big.bin").write_bytes(b"\0" * (2 * 1024 * 1024))
    evidence = main / "build" / "director_evidence"
    assert run.main(["--dir", str(evidence), "archive", str(wt), "a1", "--max-mb", "1"]) == 1
    assert not (evidence / "a1").exists()


def test_ci01_planted_failure():
    # CI-01 gate: planted to prove the pooled step turns CI red; reverted in the next commit.
    assert 1 == 2, "CI-01 planted failure"
