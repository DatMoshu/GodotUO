"""Run-folder retention: keep the newest runs, delete the older folders, leave history and video masters alone.

A folder is a run folder only when it holds a run.json whose run_id is the folder's own name; anything else
(a stray folder, a link, a half-written run) is never touched. Registry rows are history and stay. A folder
that holds a video file stays too (a master kept locally because GUO_RUNS_VIDEO_DIR was not reachable) and
counts toward the kept total. The video folder itself is never read here.
"""

from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass, field
from pathlib import Path

VIDEO_SUFFIXES = {".mp4", ".webm", ".mkv", ".avi", ".mov"}


@dataclass
class Run:
    path: Path
    run_id: str
    scenario: str
    project: str
    started: str
    has_video: bool


@dataclass
class Result:
    kept: list[Path] = field(default_factory=list)
    deleted: list[Path] = field(default_factory=list)


def scan(root: Path) -> list[Run]:
    """The genuine run folders directly under root; everything else is ignored."""
    runs: list[Run] = []
    if not root.is_dir():
        return runs
    for d in sorted(root.iterdir()):
        if d.is_symlink() or os.path.isjunction(d) or not d.is_dir():
            continue
        try:
            m = json.loads((d / "run.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(m, dict) or m.get("run_id") != d.name:
            continue
        video = any(p.suffix.lower() in VIDEO_SUFFIXES for p in d.rglob("*") if p.is_file())
        runs.append(Run(d, d.name, str(m.get("scenario") or ""), str(m.get("project") or "guo"),
                        str(m.get("started") or ""), video))
    return runs


def prune_group(runs: list[Run], keep: int, dry_run: bool) -> Result:
    """Keeps the newest `keep` of one group (newest by started, then by run id); deletes the rest."""
    res = Result()
    ordered = sorted(runs, key=lambda r: (r.started, r.run_id), reverse=True)
    for i, r in enumerate(ordered):
        if i < keep or r.has_video:
            res.kept.append(r.path)
            continue
        if not dry_run:
            shutil.rmtree(r.path)
        res.deleted.append(r.path)
    return res


def prune(local_root: Path, shared_root: Path | None, keep_local: int, keep_shared: int, dry_run: bool = False) -> dict[str, Result]:
    """Local folders are grouped by scenario, shared folders by project. Returns a Result per group."""
    out: dict[str, Result] = {}
    groups: dict[str, list[Run]] = {}
    for r in scan(local_root):
        groups.setdefault(r.scenario, []).append(r)
    for scenario, runs in sorted(groups.items()):
        out[f"local {scenario}"] = prune_group(runs, keep_local, dry_run)
    if shared_root is not None:
        groups = {}
        for r in scan(shared_root):
            groups.setdefault(r.project, []).append(r)
        for project, runs in sorted(groups.items()):
            out[f"shared {project}"] = prune_group(runs, keep_shared, dry_run)
    return out
