"""ServUO backend for world objects (ADR-0014): the neutral model as ServUO's own files.

Written from the file formats, not from ServUO's code (GPL-3.0; nothing is
copied). Under <export>/shard/:

  XmlSpawner/guo-<project>.xml             spawners as XmlSpawner points (a DataSet: <Spawns><Points>...)
  Data/Decoration/<Map>/guo-<project>.cfg  items in the RunUO-lineage decoration format
  guo_objects.json                         the manifest
  APPLY.txt                                the GM commands that load them

ServUO reads neither file at boot: a GM loads them with [XmlLoad and
[Decorate. [XmlLoad replaces a spawner with the same UniqueId, so a changed or
moved spawner is replaced; neither command deletes. Deletions and item moves
go through the tag-based GM-command apply (tools/world apply-commands), as on
any shard without GUO's bridge.
"""
from __future__ import annotations

import json
import xml.etree.ElementTree as ET
from pathlib import Path

from guo.worldobjects import WorldObjects

from . import modernuo  # the decoration cfg format is the same RunUO-lineage format

NAME = "servuo"
DECORATE_FOLDERS = modernuo.DECORATE_FOLDERS


def minutes(hms: str) -> str:
    h, m, s = (float(p) for p in hms.split(":"))
    return f"{h * 60 + m + s / 60:g}"


def entries(s) -> str:
    """XmlSpawner's entry list: name:MX=<max>:SB=0:RT=0:TO=0:KL=0, joined with :OBJ=.
    XmlSpawner has no per-entry probability; it is kept in the model's extra."""
    return ":OBJ=".join(f"{e['name']}:MX={e['max']}:SB=0:RT=0:TO=0:KL=0" for e in s.entries)


def point(s) -> dict:
    r = max(s.home_range, 0)
    return {
        "Name": f"GUO {s.entries[0]['name']}",
        "UniqueId": s.id,
        "Map": s.map,
        "X": str(s.x - r), "Y": str(s.y - r), "Width": str(2 * r), "Height": str(2 * r),
        "CentreX": str(s.x), "CentreY": str(s.y), "CentreZ": str(s.z),
        "Range": str(r),
        "MaxCount": str(s.count),
        "MinDelay": minutes(s.min_delay), "MaxDelay": minutes(s.max_delay),
        "Team": str(s.team),
        "IsGroup": "False", "IsRunning": "True", "IsHomeRangeRelative": "True",
        "Objects2": entries(s),
    }


def export(objects: WorldObjects, project_name: str, out: Path) -> list[Path]:
    root = out / "shard"
    written = []
    if objects.spawners:
        spawns = ET.Element("Spawns")
        for s in sorted(objects.spawners, key=lambda s: (s.map, s.y, s.x, s.id)):
            p = ET.SubElement(spawns, "Points")
            for k, v in point(s).items():
                ET.SubElement(p, k).text = v
        ET.indent(spawns)
        xml = root / "XmlSpawner" / f"guo-{project_name}.xml"
        xml.parent.mkdir(parents=True, exist_ok=True)
        ET.ElementTree(spawns).write(xml, encoding="utf-8", xml_declaration=True)
        written.append(xml)

    # The decoration cfg is written exactly as for ModernUO (same format, same folders).
    deco = [p for p in modernuo.export(WorldObjects(items=objects.items), project_name, out) if "Decoration" in p.parts]
    written += deco

    manifest = {
        "format": 1, "backend": NAME, "project": project_name,
        "spawners": [{"id": s.id, "map": s.map, "location": [s.x, s.y, s.z]} for s in objects.spawners],
        "items": [{"id": i.id, "map": i.map, "location": [i.x, i.y, i.z], "item_id": f"0x{i.item_id:04X}",
                   "hue": f"0x{i.hue:04X}", "type": i.type} for i in objects.items],
        "files": [p.relative_to(root).as_posix() for p in written],
    }
    (root / "guo_objects.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    written.append(root / "guo_objects.json")
    no_decorate = sorted({i.map for i in objects.items} - DECORATE_FOLDERS)
    apply = [f"GUO world objects for ServUO, from project {project_name}.", "",
             "1. Copy shard\\XmlSpawner\\ and shard\\Data\\ into the ServUO folder (beside ServUO.exe).",
             f"2. As a GM (Administrator): [XmlLoad guo-{project_name}.xml" if objects.spawners else "",
             "3. As a GM: [Decorate" if objects.items else "",
             "[XmlLoad replaces a spawner with the same UniqueId (so moves and changes apply);",
             "[Decorate skips items already present. Neither deletes: for deletions and item moves use",
             "  python tools\\world\\run.py apply-commands --host H --port P",
             "which removes only what it placed itself."]
    if no_decorate:
        apply.append(f"[Decorate has no folder for {', '.join(no_decorate)}: those items need apply-commands.")
    (root / "APPLY.txt").write_text("\n".join(l for l in apply if l) + "\n", encoding="utf-8")
    written.append(root / "APPLY.txt")
    return written


def read_back(out: Path, project_name: str) -> tuple[list[dict], list[tuple]]:
    """The spawners as their XmlSpawner points (dicts), and the items as the cfg reads (shared).
    Parses only the file this export just wrote (stdlib ElementTree; expat does not
    resolve external entities), never a file from elsewhere."""
    xml = out / "shard" / "XmlSpawner" / f"guo-{project_name}.xml"
    spawners = [{c.tag: c.text or "" for c in p} for p in ET.parse(xml).getroot().findall("Points")] if xml.is_file() else []
    _, items = modernuo.read_back(out, project_name)
    return spawners, items


def spawner_record(s) -> dict:
    return point(s)


def record_key(r: dict) -> str:
    return r["UniqueId"]
