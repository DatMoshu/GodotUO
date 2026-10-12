"""run.py import: a town laid on a world project.

    python -m pytest tools/world/test_town_import.py

Most tests use a small synthetic town written here in the layout tools/mapgen_districts gives a
built town (docs/data_formats.md section 26), so they need no game data. One test builds the real
hamlet (preset hamlet, seed 4) with mapgen_districts itself; that build reads the install
(tiledata and its fingerprint), so it is skipped where there is none.
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
SIZE = 16                  # the synthetic town: 2x2 blocks

# Two houses as multi components [item, dx, dy, dz(, 0 when hidden)] around their centres. The second
# stands on land at z 5, so its statics are lifted by 5. Each has one hidden door slot.
HOUSES = {
    "lot1": {"centre": [4, 4], "components": [[0x0066, -1, -1, 7], [0x0066, 0, -1, 7], [0x0067, 1, -1, 7],
                                              [0x04A9, 0, 0, 7], [0x06A5, 0, 1, 7, 0], [0x0066, -1, 1, 7]]},
    "lot2": {"centre": [11, 11], "components": [[0x0066, -1, -1, 7], [0x0067, 1, 1, 7], [0x04A9, 0, 0, 7],
                                                [0x06A5, 1, 0, 7, 0], [0x05D1, 0, 0, 27]]},
}
TOWN_STATICS = [(0, 0, {"id": "0x0CE3", "x": 7, "y": 7, "z": 0, "hue": "0x0000"})]  # a tree in block 0,0


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=1), encoding="utf-8")


def make_town(root: Path, name: str = "mini_1", status: str = "native-valid") -> Path:
    """A built town's folder, as mapgen_districts' `town` writes it, small enough to check by hand."""
    folder = root / name
    write_json(folder / "district.json", {
        "format": 1, "kind": "uo-district", "generator": "guo-town", "name": name, "origin": [0, 0],
        "size": [SIZE, SIZE], "plan": {"schema": "guo.mapgen.town/1", "preset": "synthetic", "seed": 1},
        "houses": len(HOUSES), "status": status, "problems": []})
    write_json(folder / "plan.json", {
        "schema": "guo.mapgen.town/1", "name": name, "preset": "synthetic", "seed": 1, "size": [SIZE, SIZE],
        "lots": [{"id": "lot1", "house": {"centre": HOUSES["lot1"]["centre"]}},
                 {"id": "lot2", "house": {"centre": HOUSES["lot2"]["centre"]}},
                 {"id": "lot3", "house": None}]})
    write_json(folder / "world" / "project.json", {"format": 1, "name": name, "created": "1970-01-01T00:00:00+00:00",
                                                   "base": {}})
    for by in range(SIZE // 8):
        for bx in range(SIZE // 8):
            z = 5 if (bx, by) == (1, 1) else 0
            land = [" ".join(f"{3 + (x + y) % 4:04X}:{z}" for x in range(8)) for y in range(8)]
            statics = [s for (sbx, sby, s) in TOWN_STATICS if (sbx, sby) == (bx, by)]
            write_json(folder / "world" / "blocks" / "0" / f"{bx}_{by}.json",
                       {"format": 1, "facet": 0, "block": [bx, by], "land": land, "statics": statics})
    write_json(folder / "scene.json", {"format": 1, "kind": "scene", "name": name, "bounds": [0, 0, SIZE - 1, SIZE - 1],
                                       "parts": [{"name": n, "centre": h["centre"], "components": len(h["components"])}
                                                 for n, h in HOUSES.items()], "tour": []})
    for n, h in HOUSES.items():
        write_json(folder / "parts" / f"{n}.json", h["components"])
    return folder


@pytest.fixture
def town(tmp_path) -> Path:
    return make_town(tmp_path / "towns")


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def tree(root: Path) -> dict[str, bytes]:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


def check_matches_plan(town: Path, project: Path, summary: dict, at: tuple[int, int]) -> None:
    """The import holds exactly what the town's plan, scene and parts say, inside the map."""
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
    assert len(files) == (w // 8) * (h // 8) == summary["blocks"]
    assert summary["houses"] == len(houses) == len(scene["parts"]) == record["houses"]
    assert summary["hidden_components"] == hidden
    assert sum(len(b["statics"]) for b in blocks) == visible + town_statics == summary["statics"]
    assert sum(len(row.split()) for b in blocks for row in b["land"]) == w * h == summary["land_cells"]

    x, y = at
    assert summary["bounds"] == [x, y, x + w - 1, y + h - 1]
    for b in blocks:
        bx, by = b["block"]
        assert b["facet"] == 0 and x <= bx * 8 and bx * 8 + 7 <= x + w - 1 and y <= by * 8 and by * 8 + 7 <= y + h - 1
        assert bx * 8 + 7 < 7168 and by * 8 + 7 < 4096
        for s in b["statics"]:
            assert 0 <= s["x"] < 8 and 0 <= s["y"] < 8

    # the land is the town's, moved: the town's block 0,0 is the corner block
    corner = load(project / "blocks" / "0" / f"{x // 8}_{y // 8}.json")
    assert corner["land"] == load(town / "world" / "blocks" / "0" / "0_0.json")["land"]

    assert load(project / "project.json")["format"] == 1
    pins = load(project / "pins.json")["pins"]
    assert [(p["name"], p["x"], p["y"]) for p in pins] == [(record["name"], x + w // 2, y + h // 2)]


def test_the_import_matches_the_plan(town, tmp_path):
    project = tmp_path / "project"
    summary = town_import.import_town(town, project, 0, AT)
    check_matches_plan(town, project, summary, AT)
    assert (summary["blocks"], summary["houses"], summary["hidden_components"], summary["statics"]) == (4, 2, 2, 10)


def test_house_statics_land_where_the_multi_puts_them(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT)
    for house in HOUSES.values():
        cx, cy = AT[0] + house["centre"][0], AT[1] + house["centre"][1]
        base = 5 if house["centre"] == [11, 11] else 0   # the land under the centre
        for comp in house["components"]:
            item, dx, dy, dz = comp[:4]
            sx, sy = cx + dx, cy + dy
            statics = load(project / "blocks" / "0" / f"{sx // 8}_{sy // 8}.json")["statics"]
            entry = {"id": f"0x{item:04X}", "x": sx % 8, "y": sy % 8, "z": base + dz, "hue": "0x0000"}
            assert (entry in statics) == (len(comp) == 4), (comp, entry)


def test_blocks_are_written_as_the_editor_writes_them(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT)
    text = (project / "blocks" / "0" / f"{AT[0] // 8}_{AT[1] // 8}.json").read_text(encoding="utf-8")
    assert text.startswith('{\n  "format": 1,\n  "facet": 0,\n  "block": [768, 320],\n  "land": [\n    "')
    statics = load(project / "blocks" / "0" / f"{AT[0] // 8}_{AT[1] // 8}.json")["statics"]
    assert statics == sorted(statics, key=lambda s: (s["y"], s["x"], s["z"], int(s["id"], 16)))


def test_two_runs_give_identical_output(town, tmp_path):
    a, b = tmp_path / "a" / "project", tmp_path / "b" / "project"
    town_import.import_town(town, a, 0, AT)
    town_import.import_town(town, b, 0, AT)
    assert tree(a) == tree(b)
    assert len(tree(a)) == 4 + 2  # the blocks, project.json, pins.json


@pytest.mark.parametrize("facet, at, words", [
    (0, (7160, 0), "does not fit at 7160,0"),
    (0, (0, 4088), "does not fit at 0,4088"),
    (4, (1440, 0), "map4 is 1448x1448"),
    (0, (-8, 0), "does not fit"),
    (0, (6145, 2560), "not on the 8x8 block grid"),
    (9, (0, 0), "facet 9 is not a UO map"),
])
def test_a_town_that_does_not_fit_is_refused_and_nothing_is_written(town, tmp_path, facet, at, words):
    project = tmp_path / "project"
    with pytest.raises(town_import.Refused, match=words):
        town_import.import_town(town, project, facet, at)
    assert not project.exists()


def test_the_refusal_names_the_furthest_corner_that_fits(town, tmp_path):
    with pytest.raises(town_import.Refused, match="at most at 7152,4080"):
        town_import.import_town(town, tmp_path / "project", 0, (7160, 4088))


def test_blocks_already_edited_are_refused_unless_replace(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT)
    before = tree(project)
    with pytest.raises(town_import.Refused, match="4 of the town's 4 blocks are already edited"):
        town_import.import_town(town, project, 0, AT)
    assert tree(project) == before
    summary = town_import.import_town(town, project, 0, AT, replace=True)
    assert summary["blocks_replaced"] == 4 and tree(project) == before


def test_a_town_that_did_not_build_cleanly_is_refused(tmp_path):
    town = make_town(tmp_path / "towns", status="invalid")
    with pytest.raises(town_import.Refused, match="did not build cleanly"):
        town_import.import_town(town, tmp_path / "project", 0, AT)


def test_not_a_town_is_refused(tmp_path):
    with pytest.raises(town_import.Refused, match="not a built town"):
        town_import.import_town(tmp_path, tmp_path / "project", 0, AT)


def test_a_new_project_takes_the_towns_base_without_an_install(town, tmp_path):
    project = tmp_path / "project"
    town_import.import_town(town, project, 0, AT, client_data=tmp_path / "no_install")
    assert load(project / "project.json") == {"format": 1, "name": "project",
                                              "created": "1970-01-01T00:00:00+00:00", "base": {}}


def test_the_command_line_refuses_in_plain_words(town, tmp_path):
    project = tmp_path / "project"
    cmd = [sys.executable, str(HERE / "run.py"), "import", "--town", str(town), "--project", str(project)]
    refused = subprocess.run([*cmd, "--at", "7160,0"], capture_output=True, text=True)
    assert refused.returncode == 1, refused.stdout + refused.stderr
    assert "import refused: town mini_1 is 16x16 cells and does not fit at 7160,0" in refused.stdout
    assert not project.exists()
    done = subprocess.run([*cmd, "--at", f"{AT[0]},{AT[1]}"], capture_output=True, text=True)
    assert done.returncode == 0, done.stdout + done.stderr
    assert "imported mini_1: 4 blocks, 10 statics (2 houses)" in done.stdout


# --- the real town, built by tools/mapgen_districts -----------------------------------------------

def install_with_tiledata() -> bool:
    sys.path.insert(0, str(TOOLS))
    from guo.config import load_config
    data = load_config().client_data
    return bool(data) and (Path(data) / "tiledata.mul").is_file()


@pytest.mark.skipif(not install_with_tiledata(),
                    reason="needs the UO install: mapgen_districts town reads tiledata and the install fingerprint")
def test_the_real_hamlet_imports_as_planned(tmp_path):
    town = tmp_path / "hamlet_4"
    build = subprocess.run([sys.executable, str(TOOLS / "mapgen_districts" / "run.py"), "town", "--out", str(town),
                            "--preset", "hamlet", "--seed", "4"], capture_output=True, text=True)
    if build.returncode != 0:
        pytest.fail(f"mapgen_districts town failed (exit {build.returncode}):\n{build.stdout}\n{build.stderr}")
    project = tmp_path / "project"
    summary = town_import.import_town(town, project, 0, AT)
    check_matches_plan(town, project, summary, AT)
    assert summary["blocks"] == 81
    again = tmp_path / "again" / "project"
    town_import.import_town(town, again, 0, AT)
    assert tree(project) == tree(again)
