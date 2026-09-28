"""The decor database: schema, writing and the queries the decorator reads.

build/decorate/decor.sqlite is derived from client data, so it stays in build/
and is never committed. Rebuilt whole by `run.py mine`.
"""
from __future__ import annotations

import sqlite3
from pathlib import Path

SCHEMA_VERSION = 1

SCHEMA = """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);

-- one building: a cluster of wall statics on a facet, or a client multi
CREATE TABLE building (
  id INTEGER PRIMARY KEY, source TEXT NOT NULL,          -- 'statics' | 'multi'
  facet INTEGER, ref TEXT NOT NULL,                      -- 'x,y' of its corner on the facet, or the multi id
  x0 INTEGER, y0 INTEGER, x1 INTEGER, y1 INTEGER,        -- world (statics) or multi-local bounds
  storeys TEXT NOT NULL,                                 -- JSON list of floor z, bottom up
  rooms INTEGER NOT NULL, furnishings INTEGER NOT NULL);

-- a room: a floor region bounded by walls, split at doors, on one storey
CREATE TABLE room (
  id INTEGER PRIMARY KEY, building_id INTEGER NOT NULL REFERENCES building(id),
  storey INTEGER NOT NULL, z INTEGER NOT NULL,           -- storey index (0 = ground) and its floor z
  x0 INTEGER, y0 INTEGER, w INTEGER NOT NULL, h INTEGER NOT NULL,
  area INTEGER NOT NULL, fill REAL NOT NULL, shape TEXT NOT NULL,   -- rect | near-rect | L-or-irregular
  enclosed INTEGER NOT NULL, doors INTEGER NOT NULL, windows INTEGER NOT NULL, open_edges INTEGER NOT NULL,
  type TEXT NOT NULL, type_score REAL NOT NULL, items INTEGER NOT NULL,
  kinds TEXT NOT NULL,                                   -- JSON {kind: count}
  cells TEXT NOT NULL);                                  -- JSON [[x, y], ...] relative to (x0, y0)

-- a door or window of a room, relative to the room's corner, and the room side it is on
CREATE TABLE opening (
  room_id INTEGER NOT NULL REFERENCES room(id), kind TEXT NOT NULL,  -- door | window
  x INTEGER NOT NULL, y INTEGER NOT NULL, side TEXT NOT NULL);

-- one furnishing item as placed in a room
CREATE TABLE furnishing (
  id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL REFERENCES room(id),
  item INTEGER NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, flags INTEGER NOT NULL,
  x INTEGER NOT NULL, y INTEGER NOT NULL, z_above INTEGER NOT NULL,   -- room-relative cell; z above the floor
  against TEXT NOT NULL,                                 -- wall sides next to it, NESW order ('' none)
  place TEXT NOT NULL,                                   -- corner | wall | centre | open | on-wall
  near_door INTEGER NOT NULL, near_window INTEGER NOT NULL,
  facing TEXT NOT NULL,                                  -- N/E/S/W the item faces, '' unknown
  on_item INTEGER,                                       -- the surface item it stands on (stacking)
  group_id INTEGER REFERENCES grp(id));

-- a furniture group: furnishing connected cell to cell in one room (a bed, a table with
-- chairs and what stands on it, a forge with its anvil)
CREATE TABLE grp (
  id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL REFERENCES room(id), template_id INTEGER NOT NULL
  REFERENCES template(id), x INTEGER NOT NULL, y INTEGER NOT NULL);

-- a distinct group layout: items at offsets from its corner. What the decorator places.
CREATE TABLE template (
  id INTEGER PRIMARY KEY, key TEXT UNIQUE NOT NULL,
  items TEXT NOT NULL,                                   -- JSON [[item, dx, dy, z_above], ...]
  w INTEGER NOT NULL, h INTEGER NOT NULL, cells INTEGER NOT NULL, pieces INTEGER NOT NULL,
  against TEXT NOT NULL,                                 -- the wall sides it most often stands against
  on_wall INTEGER NOT NULL,                              -- 1: hangs on a wall cell (painting, sconce)
  kinds TEXT NOT NULL,                                   -- JSON {kind: count}
  uses INTEGER NOT NULL, room_types TEXT NOT NULL);      -- JSON {room type: uses}

-- per item: how it is placed everywhere
CREATE TABLE item (
  item INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, flags INTEGER NOT NULL,
  height INTEGER NOT NULL, placed INTEGER NOT NULL,
  against_n INTEGER NOT NULL, against_e INTEGER NOT NULL, against_s INTEGER NOT NULL, against_w INTEGER NOT NULL,
  corner INTEGER NOT NULL, centre INTEGER NOT NULL, on_wall INTEGER NOT NULL, stacked INTEGER NOT NULL,
  near_door INTEGER NOT NULL, near_window INTEGER NOT NULL,
  facing TEXT NOT NULL,                                  -- the most common facing
  z_above TEXT NOT NULL);                                -- JSON {z: count}

-- kinds found together in one room, and kinds side by side (Chebyshev distance 1)
CREATE TABLE cooccur (a TEXT NOT NULL, b TEXT NOT NULL, rooms INTEGER NOT NULL, adjacent INTEGER NOT NULL,
  PRIMARY KEY (a, b));

-- per room type: how many, how big, how full
CREATE TABLE room_type (
  type TEXT PRIMARY KEY, rooms INTEGER NOT NULL, ground INTEGER NOT NULL, upper INTEGER NOT NULL,
  mean_area REAL NOT NULL, min_area INTEGER NOT NULL, max_area INTEGER NOT NULL,
  density REAL NOT NULL,                                 -- furnishing cells per room cell, median
  kinds TEXT NOT NULL);                                  -- JSON {kind: rooms having it}

CREATE INDEX room_type_ix ON room(type);
CREATE INDEX furn_room_ix ON furnishing(room_id);
CREATE INDEX grp_tpl_ix ON grp(template_id);
"""


def create(path: Path) -> sqlite3.Connection:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    if tmp.exists():
        tmp.unlink()
    con = sqlite3.connect(tmp)
    con.executescript(SCHEMA)
    con.execute("INSERT INTO meta VALUES ('schema_version', ?)", (str(SCHEMA_VERSION),))
    return con


def finish(con: sqlite3.Connection, path: Path) -> None:
    con.commit()
    con.close()
    tmp = path.with_suffix(".tmp")
    if path.exists():
        path.unlink()
    tmp.rename(path)


def open_ro(path: Path) -> sqlite3.Connection:
    if not path.exists():
        raise FileNotFoundError(f"{path}: no decor database; run `python tools/decorate/run.py mine` first")
    con = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    con.row_factory = sqlite3.Row
    return con


def counts(con: sqlite3.Connection) -> dict[str, int]:
    tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
    return {t: con.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0] for t in tables}
