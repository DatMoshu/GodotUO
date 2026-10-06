"""The runs registry: one `runs` row and one `run_steps` row per step, in a SQLite file shared by every project.

The file's location is configuration (settings.py), never a path in the repo. The deck reads it read-only.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

SCHEMA = """
CREATE TABLE IF NOT EXISTS runs (
  run_id       TEXT PRIMARY KEY,
  project      TEXT NOT NULL,
  scenario     TEXT NOT NULL,
  driver       TEXT NOT NULL,
  commit_hash  TEXT,
  started      TEXT NOT NULL,
  ended        TEXT,
  ok           INTEGER,
  steps_total  INTEGER,
  steps_failed INTEGER,
  video_path   TEXT,
  summary      TEXT
);
CREATE TABLE IF NOT EXISTS run_steps (
  run_id   TEXT NOT NULL REFERENCES runs(run_id),
  seq      INTEGER NOT NULL,
  step     TEXT NOT NULL,
  kind     TEXT,
  ok       INTEGER,
  skipped  INTEGER NOT NULL DEFAULT 0,
  dur_ms   INTEGER,
  detail   TEXT,
  PRIMARY KEY (run_id, seq)
);
CREATE INDEX IF NOT EXISTS runs_scenario ON runs(project, scenario, started);
"""


def connect(path: Path) -> sqlite3.Connection:
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path, timeout=15)
    db.executescript(SCHEMA)
    return db


def register(path: Path, manifest: dict) -> None:
    """Writes (or replaces) the row of one finished run and its steps, in one transaction."""
    db = connect(path)
    try:
        with db:
            db.execute("DELETE FROM run_steps WHERE run_id=?", (manifest["run_id"],))
            db.execute(
                "INSERT OR REPLACE INTO runs (run_id, project, scenario, driver, commit_hash, started, ended, ok, steps_total, steps_failed, video_path, summary) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
                (manifest["run_id"], manifest.get("project", "guo"), manifest["scenario"], manifest["driver"],
                 manifest.get("commit"), manifest["started"], manifest.get("ended"), int(bool(manifest["ok"])),
                 len(manifest["steps"]), sum(1 for s in manifest["steps"] if s["ok"] is False),
                 manifest.get("video_path"), manifest.get("summary")))
            for seq, s in enumerate(manifest["steps"]):
                db.execute("INSERT INTO run_steps (run_id, seq, step, kind, ok, skipped, dur_ms, detail) VALUES (?,?,?,?,?,?,?,?)",
                           (manifest["run_id"], seq, s["id"], s.get("kind"), None if s["ok"] is None else int(s["ok"]),
                            int(bool(s.get("skipped"))), s.get("dur_ms"), s.get("detail")))
    finally:
        db.close()
