"""After a run: the manifest (run.json), summary.md, and the copy that leaves the repo folder.

The repo-local folder (build/runs/<run_id>/) keeps everything. The shared folder gets run.json, events.jsonl,
summary.md, the stills and the logs, every text file redacted first. The registry gets one row per run.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path

from redact import redact, redact_file
import registry


def git_commit(root: Path) -> str:
    try:
        r = subprocess.run(["git", "-C", str(root), "rev-parse", "--short", "HEAD"], capture_output=True, text=True, timeout=15)
        return r.stdout.strip() if r.returncode == 0 else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def render_summary(manifest: dict, title: str, description: str = "") -> str:
    status = "PASS" if manifest["ok"] else "FAIL"
    steps = manifest["steps"]
    failed = [s for s in steps if s["ok"] is False]
    dur = sum(s.get("dur_ms") or 0 for s in steps) / 1000
    lines = [f"# Run: {manifest['scenario']}", "",
             f"**Status:** {status}", f"**Driver:** {manifest['driver']}", f"**Scenario:** {title}",
             f"**Started:** {manifest['started']}", f"**Duration:** {int(dur // 60)}m {int(dur % 60)}s", ""]
    if description:
        lines += [description, ""]
    lines += [f"**Commit:** {manifest.get('commit') or 'unknown'}", f"**Build:** {manifest.get('build', 'debug')}", ""]
    if manifest.get("aborted"):
        lines += [f"**The run stopped early:** {manifest['aborted']}", ""]
    lines += ["## Results", "", "| Step | Status | Duration | Notes |", "|---|---|---|---|"]
    for s in steps:
        mark = "skipped" if s.get("skipped") else ("ok" if s["ok"] else "FAILED")
        lines.append(f"| {s['id']} | {mark} | {(s.get('dur_ms') or 0) / 1000:.1f}s | {(s.get('detail') or '').replace('|', '/')} |")
    lines += ["", f"**Steps passed:** {len(steps) - len(failed)}/{len(steps)}", f"**Steps failed:** {len(failed)}", ""]
    if failed:
        lines += ["## Failed steps", ""]
        for s in failed:
            lines += [f"### {s['id']}", "", f"- **Failure:** {s.get('detail') or 'see events.jsonl'}", ""]
    lines += ["## Video", "", f"{manifest.get('video_note', 'No video: this run recorded stills and events only.')}", "",
              "## Event log", "", "`events.jsonl`, one JSON object per line; `frame` counts the stills so far.", ""]
    return "\n".join(lines)


def write_manifest(run_dir: Path, manifest: dict) -> None:
    (run_dir / "run.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def copy_shared(run_dir: Path, shared_root: Path, run_id: str, deny: list[str]) -> Path:
    """Copies the run's shareable files into <shared_root>/<run_id>/, redacting text on the way."""
    target = shared_root / run_id
    target.mkdir(parents=True, exist_ok=True)
    for name in ("run.json", "events.jsonl", "summary.md", "editor.log", "client.log"):
        if (run_dir / name).is_file():
            redact_file(run_dir / name, target / name, deny)
    for still in sorted((run_dir / "shots").glob("*.png")) if (run_dir / "shots").is_dir() else []:
        (target / "shots").mkdir(exist_ok=True)
        shutil.copy2(still, target / "shots" / still.name)
    return target


def register_run(db_path: Path, manifest: dict, deny: list[str] | None = None) -> None:
    """The registry is shared with every project, so its text is redacted like the shared copy's."""
    clean = dict(manifest)
    clean["summary"] = redact(manifest.get("summary") or "", deny)
    clean["steps"] = [{**st, "detail": redact(st.get("detail") or "", deny)} for st in manifest["steps"]]
    registry.register(db_path, clean)


__all__ = ["git_commit", "render_summary", "write_manifest", "copy_shared", "register_run", "redact"]
