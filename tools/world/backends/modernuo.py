"""ModernUO backend for world objects (ADR-0014): the neutral model as ModernUO's own files.

Writes, under <export>/shard/:

  Data/Spawns/guo/<project>.json            spawners as ModernUO SpawnerDto records
                                            (Engines/Spawners/Json/SpawnerDto.cs)
  Data/Decoration/<Map>/guo-<project>.cfg   items in ModernUO's decoration format
                                            (Commands/Object Creation/Decorate.cs)
  guo_objects.json                          the manifest: what the sync applies
  APPLY.txt                                 how to apply it on a shard without GUO's bridge

and reads them back for verify.
"""

from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path

from guo.worldobjects import WorldObjects

NAME = "modernuo"

# Decorate walks these folders only (Decorate.cs); TerMur has none.
DECORATE_FOLDERS = {"Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno"}


def spawner_record(s) -> dict:
    rec = {
        "$type": "Spawner",
        "guid": s.id,
        "name": f"GUO {s.entries[0]['name']}",
        "location": [s.x, s.y, s.z],
        "map": s.map,
        "count": s.count,
        "minDelay": s.min_delay,
        "maxDelay": s.max_delay,
        "team": s.team,
        "homeRange": s.home_range,
        "walkingRange": s.walking_range,
        "entries": [{"name": e["name"], "maxCount": e["max"], "probability": e["probability"]} for e in s.entries],
    }
    # Backend-only fields a read kept in extra go back out unchanged.
    for k, v in (s.extra or {}).items():
        rec.setdefault(k, v)
    return rec


def item_header(i) -> str:
    props = dict(i.props or {})
    if i.hue:
        props["Hue"] = f"0x{i.hue:X}"
    head = f"{i.type} 0x{i.item_id:04X}"
    if props:
        head += " (" + "; ".join(f"{k}={v}" for k, v in sorted(props.items())) + ")"
    return head


def export(objects: WorldObjects, project_name: str, out: Path) -> list[Path]:
    """Writes the ModernUO files and the manifest; returns the files written."""
    root = out / "shard"
    written = []
    spawns = root / "Data" / "Spawns" / "guo" / f"{project_name}.json"
    if objects.spawners:
        spawns.parent.mkdir(parents=True, exist_ok=True)
        recs = [spawner_record(s) for s in sorted(objects.spawners, key=lambda s: (s.map, s.y, s.x, s.id))]
        spawns.write_text(json.dumps(recs, indent=2) + "\n", encoding="utf-8")
        written.append(spawns)

    by_map = defaultdict(lambda: defaultdict(list))
    for i in sorted(objects.items, key=lambda i: (i.map, i.y, i.x, i.id)):
        by_map[i.map][item_header(i)].append(i)
    for map_name, groups in sorted(by_map.items()):
        cfg = root / "Data" / "Decoration" / map_name / f"guo-{project_name}.cfg"
        cfg.parent.mkdir(parents=True, exist_ok=True)
        lines = [f"# GUO world objects from project {project_name} (tools/world, ADR-0014)", ""]
        for head, items in groups.items():
            lines.append(head)
            lines += [f"{i.x} {i.y} {i.z}" for i in items]
            lines.append("")
        cfg.write_text("\n".join(lines), encoding="utf-8")
        written.append(cfg)

    manifest = {
        "format": 1,
        "backend": NAME,
        "project": project_name,
        "spawners": [{"id": s.id, "map": s.map, "location": [s.x, s.y, s.z]} for s in objects.spawners],
        "items": [{"id": i.id, "map": i.map, "location": [i.x, i.y, i.z], "item_id": f"0x{i.item_id:04X}",
                   "hue": f"0x{i.hue:04X}", "type": i.type,
                   "props": {**(i.props or {}), **({"Hue": f"0x{i.hue:X}"} if i.hue else {})}}
                  for i in objects.items],
        "files": [p.relative_to(root).as_posix() for p in written],
    }
    (root / "guo_objects.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    written.append(root / "guo_objects.json")

    no_decorate = sorted({i.map for i in objects.items} - DECORATE_FOLDERS)
    apply = [
        f"GUO world objects for ModernUO, from project {project_name}.",
        "",
        "With GUO's bridge (tools/editor_shard, private instance):",
        "  python tools\\editor_shard\\run.py start --objects <this export folder>",
        "  The bridge syncs at boot: adds, updates, moves and deletes, by id.",
        "",
        "On a ModernUO shard without the bridge:",
        "  1. Copy shard\\Data\\ into the shard's Distribution\\Data\\.",
        f"  2. As a GM (Developer): [ImportSpawners Data/Spawns/guo/{project_name}.json" if objects.spawners else "",
        "  3. As a GM (Developer): [Decorate" if objects.items else "",
        "  This ADDS new objects and replaces a spawner at the same spot. It cannot",
        "  move or delete: an object moved or removed in the editor stays where it",
        "  was until a GM removes it ([remove). Items already present are skipped.",
    ]
    if no_decorate:
        apply.append(f"  [Decorate has no folder for {', '.join(no_decorate)}: those items need the bridge or [add.")
    (root / "APPLY.txt").write_text("\n".join(l for l in apply if l is not None) + "\n", encoding="utf-8")
    written.append(root / "APPLY.txt")
    return written


_HEAD = re.compile(r"^(\w+)\s+(0x[0-9A-Fa-f]+|\d+)\s*(?:\((.*)\))?\s*$")


def read_back(out: Path, project_name: str) -> tuple[list[dict], list[tuple]]:
    """Parses the written files the way ModernUO does: spawner records, and (map, type, id, props, x, y, z) items."""
    root = out / "shard"
    spawns = root / "Data" / "Spawns" / "guo" / f"{project_name}.json"
    spawners = json.loads(spawns.read_text(encoding="utf-8")) if spawns.is_file() else []
    items = []
    for cfg in sorted((root / "Data" / "Decoration").glob(f"*/guo-{project_name}.cfg")):
        head = None
        for line in cfg.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line:
                head = None
                continue
            if line.startswith("#"):
                continue
            if head is None:
                m = _HEAD.match(line)
                if not m:
                    raise ValueError(f"{cfg}: bad header {line!r}")
                props = {}
                for part in (m.group(3) or "").split(";"):
                    if "=" in part:
                        k, v = part.split("=", 1)
                        props[k.strip()] = v.strip()
                head = (m.group(1), int(m.group(2), 0), props)
                continue
            x, y, z = (int(v) for v in line.split()[:3])
            items.append((cfg.parent.name, head[0], head[1], head[2], x, y, z))
    return spawners, items
