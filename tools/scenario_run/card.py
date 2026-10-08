"""Build the Discord card for a finished run. Reads run.json, writes card.json; sends nothing.

No network, no Discord call, no token. Sending is a separate step the integrator takes after the owner has watched
the run. Every text field is redacted before it is written.
"""

from __future__ import annotations

import json
from pathlib import Path

from redact import redact

BODY_LIMIT = 900


def find_run(root: Path, shared: str, run_id: str) -> Path | None:
    """The run's folder: the repo-local one, else the shared copy. None when neither holds a run.json."""
    if not run_id or run_id != Path(run_id).name:
        return None
    for base in (root / "build" / "runs", Path(shared) if shared else None):
        if base is not None and (base / run_id / "run.json").is_file():
            return base / run_id
    return None


def _duration(manifest: dict) -> str:
    secs = int(sum(s.get("dur_ms") or 0 for s in manifest.get("steps", [])) / 1000)
    return f"{secs // 60}m {secs % 60}s"


def _stills(run_dir: Path) -> list[str]:
    shots = run_dir / "shots"
    return sorted(p.name for p in shots.glob("*.png")) if shots.is_dir() else []


def _failed_line(failed: list[str], room: int) -> str:
    """'Failed: a, b, +N more', cut to fit in `room` characters (at least one id is always named)."""
    shown: list[str] = []
    for i, sid in enumerate(failed):
        rest = len(failed) - i - 1
        tail = f", +{rest} more" if rest else ""
        candidate = "Failed: " + ", ".join(shown + [sid])
        if shown and len(candidate) + len(tail) > room:
            return "Failed: " + ", ".join(shown) + f", +{len(failed) - i} more"
        shown.append(sid)
    return "Failed: " + ", ".join(shown)


def build_card(manifest: dict, run_dir: Path, deny: list[str] | None = None) -> dict:
    status = "PASS" if manifest.get("ok") else "FAIL"
    steps = manifest.get("steps", [])
    failed = [s["id"] for s in steps if s.get("ok") is False]
    stills = _stills(run_dir)
    title = manifest.get("title") or manifest.get("scenario") or manifest.get("run_id", "")
    head = [f"{status}: {title}", f"Scenario: {manifest.get('scenario', '')}  Driver: {manifest.get('driver', '')}",
            f"Duration: {_duration(manifest)}  Commit: {manifest.get('commit') or 'unknown'}"]
    lines = list(head)
    if manifest.get("aborted"):
        lines.append(f"Stopped early: {str(manifest['aborted'])[:120]}")
    if stills:
        lines.append(f"Stills: {len(stills)}")
    lines = [redact(ln, deny) for ln in lines]
    joined = "\n".join(lines)
    failed_line = _failed_line([redact(f, deny) for f in failed], BODY_LIMIT - len(joined) - 1) if failed else ""
    # Cut the head to leave room for the failed line, so the cut never clips its "+N more" tail.
    if failed_line:
        head_text = joined[:max(BODY_LIMIT - len(failed_line) - 1, 0)].rstrip("\n")
        text = head_text + "\n" + failed_line if head_text else failed_line
    else:
        text = joined[:BODY_LIMIT]
    card = {
        "run_id": manifest.get("run_id", ""), "title": title, "scenario": manifest.get("scenario", ""),
        "driver": manifest.get("driver", ""), "status": status, "failed_steps": failed,
        "duration": _duration(manifest), "commit": manifest.get("commit") or "unknown", "stills": stills,
        "text": text,
    }
    text = card.pop("text")
    card = _redact_all(card, deny)
    card["text"] = text
    if len(card["text"]) > BODY_LIMIT:
        raise ValueError(f"card text is {len(card['text'])} characters, over the {BODY_LIMIT} limit")
    return card


def _redact_all(value, deny):
    if isinstance(value, str):
        return redact(value, deny)
    if isinstance(value, list):
        return [_redact_all(v, deny) for v in value]
    if isinstance(value, dict):
        return {k: _redact_all(v, deny) for k, v in value.items()}
    return value


def write_card(run_dir: Path, card: dict) -> Path:
    path = run_dir / "card.json"
    path.write_text(json.dumps(card, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path
