"""Import a built town (tools/mapgen_districts) into a world project as ordinary map edits.

A town's folder (`run.py town --out DIR`, docs/data_formats.md section 26) holds its land as a
world project of its own (`world/blocks/0/*.json`, the district at its `origin`) and its houses as
native multis (`scene.json` parts, `parts/<name>.json` components). The import moves the town's
blocks so its corner lands on a cell of the target facet, and writes each house's visible
components into those blocks as statics, so the editor shows the town with no shard and no multi
writer. Hidden components (the door slots a shard fills with real doors) are left out and counted.

Nothing is written until every check passes: the facet is known, the corner is on the 8x8 block
grid, the whole town lies inside the facet, every component lands inside the town's own blocks and
no block the town needs is already edited in the project (unless replace).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo.uomap import FACET_SIZE, install_fingerprint  # noqa: E402


class Refused(Exception):
    """The import cannot go ahead; the message says why, in plain words."""


def town_folder(town: Path) -> Path:
    """The town's folder, given the folder or one of its files (district.json, plan.json)."""
    folder = town if town.is_dir() else town.parent
    if not (folder / "district.json").exists():
        raise Refused(f"{town} is not a built town: there is no district.json in {folder} "
                      "(make one with python tools\\mapgen_districts\\run.py town --out DIR)")
    return folder


def block_text(facet: int, bx: int, by: int, land: list[str], statics: list[dict]) -> str:
    """A block file as the editor writes it (Overlay/WorldProject.cs WriteBlock): one row, one static per line."""
    out = ["{", '  "format": 1,', f'  "facet": {facet},', f'  "block": [{bx}, {by}],', '  "land": [']
    out += [f'    "{row}"' + ("," if y < 7 else "") for y, row in enumerate(land)]
    out.append("  ],")
    ordered = sorted(statics, key=lambda s: (s["y"], s["x"], s["z"], int(s["id"], 16)))
    if ordered:
        out.append('  "statics": [')
        out += [f'    {{"id": "{s["id"]}", "x": {s["x"]}, "y": {s["y"]}, "z": {s["z"]}, "hue": "{s["hue"]}"}}'
                + ("," if i < len(ordered) - 1 else "") for i, s in enumerate(ordered)]
        out.append("  ]")
    else:
        out.append('  "statics": []')
    out.append("}")
    return "\n".join(out) + "\n"


def plan_import(town: Path, facet: int, at: tuple[int, int]) -> dict:
    """Everything the import would write, checked, in memory: {"blocks": {(bx, by): text}, ...}."""
    folder = town_folder(town)
    record = json.loads((folder / "district.json").read_text(encoding="utf-8"))
    if record.get("status") != "native-valid":
        raise Refused(f"town {record.get('name')} did not build cleanly (status {record.get('status')!r}); "
                      "build it again before importing it")
    if facet not in FACET_SIZE:
        raise Refused(f"facet {facet} is not a UO map; facets are {', '.join(map(str, sorted(FACET_SIZE)))}")
    width, height = FACET_SIZE[facet]
    x, y = at
    w, h = record["size"]
    ox, oy = record["origin"]
    if x % 8 or y % 8:
        raise Refused(f"the town's corner {x},{y} is not on the 8x8 block grid; "
                      f"use multiples of 8, such as {x - x % 8},{y - y % 8}")
    if x < 0 or y < 0 or x + w > width or y + h > height:
        raise Refused(f"town {record['name']} is {w}x{h} cells and does not fit at {x},{y}: it would cover "
                      f"{x},{y} to {x + w - 1},{y + h - 1}, but map{facet} is {width}x{height} "
                      f"(cells 0,0 to {width - 1},{height - 1}). Place its corner at most at "
                      f"{(width - w) // 8 * 8},{(height - h) // 8 * 8}")

    dbx, dby = (x - ox) // 8, (y - oy) // 8
    blocks: dict[tuple[int, int], dict] = {}
    for path in sorted((folder / "world" / "blocks" / "0").glob("*.json")):
        b = json.loads(path.read_text(encoding="utf-8"))
        bx, by = b["block"]
        blocks[(bx + dbx, by + dby)] = {"land": list(b["land"]), "statics": [dict(s) for s in b["statics"]]}
    expected = (w // 8) * (h // 8)
    if len(blocks) != expected:
        raise Refused(f"town {record['name']} has {len(blocks)} land blocks, not the {expected} its size needs")

    def land_z(cx: int, cy: int) -> int:
        b = blocks.get((cx // 8, cy // 8))
        return int(b["land"][cy % 8].split()[cx % 8].split(":")[1]) if b else 0

    scene = json.loads((folder / "scene.json").read_text(encoding="utf-8"))
    houses = hidden = 0
    for part in scene["parts"]:
        comps = json.loads((folder / "parts" / f"{part['name']}.json").read_text(encoding="utf-8"))
        cx, cy = x + part["centre"][0], y + part["centre"][1]
        base = land_z(cx, cy)  # the multi stands on the land at its centre, as a shard places it
        houses += 1
        for comp in comps:
            item, dx, dy, dz = comp[:4]
            if len(comp) > 4 and comp[4] == 0:
                hidden += 1
                continue
            sx, sy = cx + dx, cy + dy
            block = blocks.get((sx // 8, sy // 8))
            if block is None:
                raise Refused(f"house {part['name']} reaches {sx},{sy}, outside the town's own blocks")
            block["statics"].append({"id": f"0x{item:04X}", "x": sx % 8, "y": sy % 8, "z": base + dz, "hue": "0x0000"})

    texts = {key: block_text(facet, key[0], key[1], b["land"], b["statics"]) for key, b in blocks.items()}
    statics = sum(len(b["statics"]) for b in blocks.values())
    keys = sorted(blocks)
    return {
        "town": record["name"], "folder": folder, "record": record, "facet": facet, "at": [x, y],
        "size": [w, h], "bounds": [x, y, x + w - 1, y + h - 1],
        "block_range": [keys[0][0], keys[0][1], keys[-1][0], keys[-1][1]],
        "blocks": texts, "land_cells": len(blocks) * 64, "statics": statics,
        "houses": houses, "hidden_components": hidden,
    }


def new_project_json(project: Path, town_project: dict, client_data: Path | None, client_version: str) -> str:
    """project.json for a project the import creates: the install's fingerprint when it is here, else the
    town's; `created` is the town's, so the same town imports to the same bytes."""
    base = dict(town_project.get("base") or {})
    if client_data and client_data.is_dir():
        try:
            base = {"client_version": client_version, "fingerprint": install_fingerprint(client_data)}
        except OSError:
            pass
    record = {"format": 1, "name": project.name, "created": town_project.get("created", "1970-01-01T00:00:00+00:00"),
              "base": base}
    return json.dumps(record, indent=2) + "\n"


def import_town(town: Path, project: Path, facet: int, at: tuple[int, int], replace: bool = False,
                client_data: Path | None = None, client_version: str = "") -> dict:
    """Check, then write the town's blocks (and a pin at its centre) into the project. Returns the summary."""
    plan = plan_import(town, facet, at)
    block_dir = project / "blocks" / str(facet)
    taken = [key for key in sorted(plan["blocks"]) if (block_dir / f"{key[0]}_{key[1]}.json").exists()]
    if taken and not replace:
        bx, by = taken[0]
        raise Refused(f"{len(taken)} of the town's {len(plan['blocks'])} blocks are already edited in {project} "
                      f"(the first is blocks/{facet}/{bx}_{by}.json); pick another corner, or pass --replace "
                      "to overwrite them")
    if (project / "project.json").exists():
        created = False
    else:
        town_project = json.loads((plan["folder"] / "world" / "project.json").read_text(encoding="utf-8"))
        text = new_project_json(project, town_project, client_data, client_version)
        project.mkdir(parents=True, exist_ok=True)
        (project / "project.json").write_text(text, encoding="utf-8", newline="\n")
        created = True

    block_dir.mkdir(parents=True, exist_ok=True)
    for (bx, by), text in sorted(plan["blocks"].items()):
        (block_dir / f"{bx}_{by}.json").write_text(text, encoding="utf-8", newline="\n")

    # A pin at the town's centre (section 9 pins.json), so the editor's Pins layer jumps there.
    pins_path = project / "pins.json"
    pins = json.loads(pins_path.read_text(encoding="utf-8")) if pins_path.exists() else {"format": 1, "pins": []}
    x, y = plan["at"]
    w, h = plan["size"]
    plan_ref = plan["record"].get("plan") or {}
    pin = {"name": plan["town"], "facet": facet, "x": x + w // 2, "y": y + h // 2, "z": 0,
           "note": f"town imported from tools/mapgen_districts (preset {plan_ref.get('preset')}, seed {plan_ref.get('seed')})"}
    pins["pins"] = [p for p in pins.get("pins", []) if p.get("name") != pin["name"]] + [pin]
    pins_path.write_text(json.dumps(pins, indent=2) + "\n", encoding="utf-8", newline="\n")

    return {"town": plan["town"], "project": str(project), "project_created": created, "facet": facet,
            "at": plan["at"], "size": plan["size"], "bounds": plan["bounds"], "block_range": plan["block_range"],
            "blocks": len(plan["blocks"]), "blocks_replaced": len(taken), "land_cells": plan["land_cells"],
            "statics": plan["statics"], "houses": plan["houses"], "hidden_components": plan["hidden_components"],
            "pin": [pin["x"], pin["y"]]}
