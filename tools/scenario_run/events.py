"""events.jsonl: one JSON object per line, the schema of docs/data_formats.md (Scenario runs)."""

from __future__ import annotations

import json
import re
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

KINDS = {"run_start", "step_start", "action", "expect", "shot", "log", "mark", "warn", "error", "hang", "step_end", "run_end"}


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def make_run_id(scenario_id: str, driver: str, now: datetime | None = None) -> str:
    """<yyyymmdd_hhmmss>_<scenario id>_<driver>, safe as a folder and file name."""
    stamp = (now or datetime.now(timezone.utc)).strftime("%Y%m%d_%H%M%S")
    safe = re.sub(r"[^A-Za-z0-9._-]+", "-", scenario_id).strip("-")
    return f"{stamp}_{safe}_{driver}"


class EventLog:
    """Appends events to a file; thread safe, flushed per line so a killed run keeps everything."""

    def __init__(self, path: Path, run_id: str, driver: str):
        self.path = path
        self.run_id = run_id
        self.driver = driver
        self.frame: int | None = None
        self.events: list[dict] = []
        self._lock = threading.Lock()
        self._file = path.open("a", encoding="utf-8")

    def emit(self, kind: str, step: str | None = None, ok: bool | None = None, detail: dict | None = None,
             dur_ms: int | None = None) -> dict:
        if kind not in KINDS:
            raise ValueError(f"unknown event kind {kind}")
        event = {"ts": utc_now(), "run_id": self.run_id, "step": step, "kind": kind, "driver": self.driver,
                 "ok": ok, "detail": detail or {}, "frame": self.frame}
        if dur_ms is not None:
            event["dur_ms"] = dur_ms
        with self._lock:
            self.events.append(event)
            self._file.write(json.dumps(event, ensure_ascii=False) + "\n")
            self._file.flush()
        return event

    def close(self) -> None:
        with self._lock:
            self._file.close()


class Stopwatch:
    def __init__(self):
        self.start = time.monotonic()

    def ms(self) -> int:
        return int((time.monotonic() - self.start) * 1000)
