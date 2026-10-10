"""SC15 tests: the ClassicUO reference is taken without typing into ClassicUO's window."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scenario_run"))

import scenario as sc  # noqa: E402


def _load():
    spec = importlib.util.spec_from_file_location("side_by_side_run", HERE / "run.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


sbs = _load()


def test_the_placing_scenario_validates_and_logs_in_without_the_login_gump():
    place = sbs.ab.Place("despise-mouth", 5401, 629)
    scen = sbs.place_scenario(place)
    assert sc.validate(scen) == []
    launch = scen["steps"][0]["do"]
    # Upstream's autologin switches, never ui.fill into the 16-character password box.
    assert "--autologin" in launch["args"]
    assert launch["args"][launch["args"].index("--password") + 1] == "$password"
    assert launch["settings"] == {"autologin": True}     # a run home of its own: the owner's settings.json is left alone
    assert not any(s["do"]["kind"].startswith("ui.") for s in scen["steps"])
    go = next(s for s in scen["steps"] if s["id"] == "go")
    assert go["do"]["text"] == "[go 5401 629"
    assert go["expect"]["world.position"] == {"x": 5401, "y": 629, "tolerance": 3}


def test_a_place_with_a_floor_checks_z_too():
    scen = sbs.place_scenario(sbs.ab.Place("britain-interior", 1434, 1686, z=0))
    go = next(s for s in scen["steps"] if s["id"] == "go")
    assert go["do"]["text"] == "[go 1434 1686 0"
    assert go["expect"]["world.position"]["z"] == 0
    assert sc.validate(scen) == []


def test_nothing_left_calls_the_keyboard_or_the_foreground():
    source = (HERE / "run.py").read_text(encoding="utf-8")
    assert "send_keys" not in source
    assert "ab.focus" not in source


def test_dump_without_cuo_only_refuses_before_starting_anything(capsys):
    with pytest.raises(SystemExit) as stop:
        sbs.main(["--at", "5401", "629", "--dump", "despise"])
    assert "--cuo-only" in str(stop.value.code)
