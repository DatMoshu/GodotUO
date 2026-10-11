"""run.py import: a hamlet from tools/mapgen_districts laid on a world project (no game data needed).

    python -m pytest tools/world/test_town_import.py

The town is built once, with a fixed preset and seed, by tools/mapgen_districts' own command.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

import town_import

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
AT = (6144, 2560)          # the empty east strip of map0, where mapgen_districts puts its maps


@pytest.fixture(scope="module")
def town(tmp_path_factory) -> Path:
    out = tmp_path_factory.mktemp("town") / "hamlet_4"
    subprocess.run([sys.executable, str(TOOLS / "mapgen_districts" / "run.py"), "town", "--out", str(out),
                    "--preset", "hamlet", "--seed", "4"], check=True, capture_output=True)
    return out


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def tree(root: Path) -> dict[str, bytes]:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


def test_the_import_matches_the_plan(town, tmp_path):
    project = tmp_path / "project"
    summary = town_import.import_town(town, project, 0, AT)

    plan, scene, record = load(town / "plan.json"), load(town / "scene.json"), load(town / "district.json")
    w, h = plan["size"]
    houses = [lot for lot in plan["lots"] if lot["house"]]
    visible = hidden = 0
    for part in scene["parts"]:
        for comp in load(town / "parts" / f"{part['name']}.json"):
            if len(comp) > 4 and comp[4] == 0:
                hidden += 1
            else:
                visible += 1
    town_statics = sum(len(load(p)["statics"]) for p in (town / "world" / "blocks" / "0").glob("*.json"))

    files = sorted((project / "blocks" / "0").glob("*.json"))
    blocks = [load(p) for p in files]
    assert len(files) == (w // 8) * (h // 8) == summary["blocks"] == 81
    assert summary["houses"] == len(houses) == len(scene["parts"]) == record["houses"]
    assert summary["hidden_components"] == hidden
    assert sum(len(b["statics"]) for b in blocks) == visible + town_statics == summary["statics"]
    assert sum(len(row.split()) for b in blocks for row in b["land"]) == w * h == summary["land_cells"]

    # bounds: every block inside the town's cells and inside map0
    x, y = AT
    assert summary["bounds"] == [x, y, x + w - 1, y + h - 1]
    for b in blocks:
        bx, by = b["block"]
        assert b["facet"] == 0 and x <= bx * 8 and bx * 8 + 7 <= x + w - 1 and y <= by * 8 and by * 8 + 7 <= y + h - 1
        assert bx * 8 + 7 < 7168 and by * 8 + 7 < 4096
        for s in b["statics"]:
            assert 0 <= s["x"] < 8 and 0 <= s["y"] < 8

    # the land is the town's, moved: block 0,0 of the town is the corner block
    corner = load(project / "blocks" / "0" / f"{x // 8}_{y // 8}.json")
    assert corner["land"] == load(town / "world" / "blocks" / "0" / "0_0.json")["land"]

    # the project is an ordinary one: tools/world reads its blocks, and the pin points at the town
    assert load(project / "project.json")["format"] == 1
    pins = load(project / "pins.json")["pins"]
    assert [(p["name"], p["x"], p["y"]) for p in pins] == [(record["name"], x + w // 2, y + h // 2)]


def test_a_house_static_lands_where_the_multi_puts_it(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT)
    part = load(town / "scene.json")["parts"][0]
    item, dx, dy, dz = next(c for c in load(town / "parts" / f"{part['name']}.json") if len(c) == 4)
    sx, sy = AT[0] + part["centre"][0] + dx, AT[1] + part["centre"][1] + dy
    block = load(project / "blocks" / "0" / f"{sx // 8}_{sy // 8}.json")
    assert {"id": f"0x{item:04X}", "x": sx % 8, "y": sy % 8, "z": dz, "hue": "0x0000"} in block["statics"]


def test_two_runs_give_identical_output(town, tmp_path):
    a, b = tmp_path / "a" / "project", tmp_path / "b" / "project"
    town_import.import_town(town, a, 0, AT)
    town_import.import_town(town, b, 0, AT)
    assert tree(a) == tree(b)
    assert len(tree(a)) == 81 + 2  # the blocks, project.json, pins.json


@pytest.mark.parametrize("facet, at, words", [
    (0, (7120, 0), "does not fit at 7120,0"),
    (0, (0, 4040), "does not fit at 0,4040"),
    (4, (1400, 0), "map4 is 1448x1448"),
    (0, (-8, 0), "does not fit"),
    (0, (6145, 2560), "not on the 8x8 block grid"),
    (9, (0, 0), "facet 9 is not a UO map"),
])
def test_a_town_that_does_not_fit_is_refused_and_nothing_is_written(town, tmp_path, facet, at, words):
    project = tmp_path / "project"
    with pytest.raises(town_import.Refused, match=words):
        town_import.import_town(town, project, facet, at)
    assert not project.exists()


def test_blocks_already_edited_are_refused_unless_replace(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT)
    before = tree(project)
    with pytest.raises(town_import.Refused, match="81 of the town's 81 blocks are already edited"):
        town_import.import_town(town, project, 0, AT)
    assert tree(project) == before
    summary = town_import.import_town(town, project, 0, AT, replace=True)
    assert summary["blocks_replaced"] == 81 and tree(project) == before


def test_not_a_town_is_refused(tmp_path):
    with pytest.raises(town_import.Refused, match="not a built town"):
        town_import.import_town(tmp_path, tmp_path / "project", 0, AT)


def test_the_command_line_refuses_in_plain_words(town, tmp_path):
    project = tmp_path / "project"
    cmd = [sys.executable, str(HERE / "run.py"), "import", "--town", str(town), "--project", str(project)]
    refused = subprocess.run([*cmd, "--at", "7120,0"], capture_output=True, text=True)
    assert refused.returncode == 1
    assert "import refused: town hamlet_4 is 72x72 cells and does not fit at 7120,0" in refused.stdout
    assert not project.exists()
    done = subprocess.run([*cmd, "--at", f"{AT[0]},{AT[1]}"], capture_output=True, text=True)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "imported hamlet_4: 81 blocks" in done.stdout
