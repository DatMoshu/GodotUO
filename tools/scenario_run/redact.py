"""Redaction of logs that leave the repo folder: machine paths, home folders, addresses, the local deny list."""

from __future__ import annotations

import re
from pathlib import Path

_RULES = [
    (re.compile(r"(?<![A-Za-z])[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s)'\"]+"), "~"),
    (re.compile(r"(?<![A-Za-z])[A-Za-z]:[\\/]+[^\s)'\"]+"), "<local path>"),
    (re.compile(r"/(?:home|Users)/[^/\s)'\"]+"), "~"),
    (re.compile(r"(?<![\d.])\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\d])"), "host"),
]


def load_deny(root: Path) -> list[str]:
    """The owner's own values (account names, device serials) from the gitignored privacy deny list."""
    path = root / "tools" / "privacy_scan" / "deny.local.txt"
    if not path.is_file():
        return []
    return [ln.strip() for ln in path.read_text(encoding="utf-8").splitlines() if ln.strip() and not ln.startswith("#")]


def redact(text: str, deny: list[str] | None = None) -> str:
    for pattern, repl in _RULES:
        text = pattern.sub(repl, text)
    for value in deny or []:
        text = re.sub(re.escape(value), "<redacted>", text, flags=re.IGNORECASE)
    return text


def redact_file(source: Path, target: Path, deny: list[str] | None = None) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(redact(source.read_text(encoding="utf-8", errors="replace"), deny), encoding="utf-8")
