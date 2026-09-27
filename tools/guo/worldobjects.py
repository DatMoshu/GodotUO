"""The world objects of a world project: spawners and placed items (ADR-0014).

The neutral model the editor writes to <project>/shard/objects.json
(docs/data_formats.md section 12). Server formats never appear here; the
backend adapters under tools/world/backends/ translate at export.
"""

from __future__ import annotations

import json
import uuid
from dataclasses import dataclass, field
from pathlib import Path

MAP_NAMES = ["Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur"]


@dataclass
class Spawner:
    id: str
    map: str
    x: int
    y: int
    z: int
    count: int = 1
    min_delay: str = "00:05:00"
    max_delay: str = "00:10:00"
    home_range: int = 2
    walking_range: int = -1
    team: int = 0
    entries: list[dict] = field(default_factory=list)   # {name, max, probability}
    extra: dict = field(default_factory=dict)


@dataclass
class PlacedItem:
    id: str
    map: str
    x: int
    y: int
    z: int
    item_id: int
    hue: int = 0
    type: str = "Static"
    props: dict = field(default_factory=dict)
    extra: dict = field(default_factory=dict)


@dataclass
class WorldObjects:
    spawners: list[Spawner] = field(default_factory=list)
    items: list[PlacedItem] = field(default_factory=list)

    def __len__(self) -> int:
        return len(self.spawners) + len(self.items)


def objects_path(project: Path) -> Path:
    return project / "shard" / "objects.json"


def load(project: Path) -> WorldObjects:
    """Reads and checks a project's objects; raises ValueError on anything malformed."""
    path = objects_path(project)
    if not path.is_file():
        return WorldObjects()
    j = json.loads(path.read_text(encoding="utf-8"))
    out = WorldObjects()
    seen = set()

    def check(o: dict, what: str) -> None:
        uuid.UUID(o["id"])
        if o["id"] in seen:
            raise ValueError(f"{what} {o['id']}: id used twice")
        seen.add(o["id"])
        if o["map"] not in MAP_NAMES:
            raise ValueError(f"{what} {o['id']}: unknown map {o['map']!r}")
        if not (-128 <= int(o["z"]) <= 127):
            raise ValueError(f"{what} {o['id']}: z {o['z']} out of range")

    for s in j.get("spawners", []):
        check(s, "spawner")
        if not s.get("entries"):
            raise ValueError(f"spawner {s['id']}: no entries")
        out.spawners.append(Spawner(
            id=s["id"], map=s["map"], x=int(s["x"]), y=int(s["y"]), z=int(s["z"]),
            count=int(s.get("count", 1)), min_delay=s.get("min_delay", "00:05:00"),
            max_delay=s.get("max_delay", "00:10:00"), home_range=int(s.get("home_range", 2)),
            walking_range=int(s.get("walking_range", -1)), team=int(s.get("team", 0)),
            entries=[{"name": e["name"], "max": int(e.get("max", 1)), "probability": int(e.get("probability", 100))}
                     for e in s["entries"]],
            extra=s.get("extra") or {},
        ))
    for i in j.get("items", []):
        check(i, "item")
        out.items.append(PlacedItem(
            id=i["id"], map=i["map"], x=int(i["x"]), y=int(i["y"]), z=int(i["z"]),
            item_id=int(i["item_id"], 16), hue=int(i.get("hue", "0x0"), 16), type=i.get("type", "Static"),
            props=i.get("props") or {}, extra=i.get("extra") or {},
        ))
    return out
