"""One-session capture: the step list, the spec, collecting shots, the tour manifest. No shard needed."""
import importlib.util
import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent


def _load(name):
    spec = importlib.util.spec_from_file_location(f"ab_{name}", HERE / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


session = _load("one_session")
tour = _load("tour")


@dataclass(frozen=True)
class Place:
    name: str
    x: int
    y: int
    z: int | None = None
    light: int = 0
    say: tuple = ()

    @property
    def go(self):
        return f"[go {self.x} {self.y}" + ("" if self.z is None else f" {self.z}")


STREET = Place("street", 1602, 1591)
NIGHT = Place("night", 1602, 1591, light=12)
ROOM = Place("room", 1434, 1686, z=0, say=("vendor buy",))


def test_every_place_sets_light_jumps_waits_then_shoots():
    assert session.steps([STREET, ROOM], 6000).split("|") == [
        "set:HighlightGameObjects=False",
        "say:[globallight 0", "say:[go 1602 1591", "wait:6000", "hide", "shot:street",
        "say:[globallight 0", "say:[go 1434 1686 0", "wait:6000", "say:vendor buy", "hide", "shot:room",
    ]


def test_settings_come_before_the_first_jump():
    listed = session.steps([STREET], 100, before=("LightingSpeedMode=True",)).split("|")
    assert listed.index("set:LightingSpeedMode=True") < listed.index("say:[go 1602 1591")


def test_hours_are_set_per_place_before_the_jump():
    listed = session.steps([STREET, NIGHT], 100, hours=session.hours_for(12, 22, 12)).split("|")
    assert listed[listed.index("shot:street") + 1] == "set:LocalLightingTestHour=22"
    assert "set:LocalLightingTestHour=12" in listed[:listed.index("shot:street")]


def test_no_hours_asked_for_means_no_clock():
    assert session.hours_for(None, None, 12) is None
    assert "LocalLightingTest" not in session.steps([STREET], 100, hours=None)


def test_spec_starts_at_the_first_place():
    assert session.spec_for(Path("out"), [ROOM], "shot:room", 1.5) == f"out;{session.SESSION};1434;1686;0;1.5;shot:room"
    assert session.spec_for(Path("out"), [STREET], "x") == f"out;{session.SESSION};1602;1591;;0;x"


def test_collect_moves_shots_and_reports_the_missing(tmp_path):
    out, root = tmp_path / "out", tmp_path / "root"
    (out / session.SESSION).mkdir(parents=True)
    (out / session.SESSION / "cuo_street.png").write_bytes(b"png")
    assert session.collect(out, root, "cuo", "cuo", [STREET, ROOM]) == ["room"]
    assert (root / "street" / "cuo.png").read_bytes() == b"png"
    assert not (out / session.SESSION / "cuo_street.png").exists()


def test_clear_removes_only_that_clients_leftovers(tmp_path):
    folder = tmp_path / session.SESSION
    folder.mkdir()
    for leftover in ("cuo.done", "cuo_street.png", "guo-default_street.png"):
        (folder / leftover).write_bytes(b"")
    session._clear(tmp_path, "cuo")
    assert sorted(p.name for p in folder.iterdir()) == ["guo-default_street.png"]


def test_the_password_does_not_stay_in_classicuos_log(tmp_path):
    log = tmp_path / "cuo.log"
    log.write_text("ARG: username, VALUE: me\nARG: password, VALUE: hunter2\nok\n", encoding="utf-8")
    session.scrub_password(log)
    text = log.read_text(encoding="utf-8")
    assert "hunter2" not in text and "VALUE: me" in text


def test_manifest_is_what_the_shard_reads():
    plan = tour.manifest([ROOM, STREET], ["Guoprobe"], 5000, 2000, 15000)
    assert json.loads(json.dumps(plan)) == {
        "characters": ["Guoprobe"], "start_ms": 15000, "settle_ms": 5000, "capture_ms": 2000,
        "stops": [{"name": "room", "x": 1434, "y": 1686, "z": 0, "light": 0},
                  {"name": "street", "x": 1602, "y": 1591, "z": None, "light": 0}],
    }


def test_client_argument():
    assert tour.parse_client(r"his|C:\x|C:\x\a.exe -a 1") == ("his", r"C:\x", [r"C:\x\a.exe", "-a", "1"])
    assert tour.parse_client(r'his|C:\x|"C:\a b\c.exe" -a')[2] == [r"C:\a b\c.exe", "-a"]
    with pytest.raises(ValueError):
        tour.parse_client("only-a-name")


def test_stamp_wait_sees_a_stamp_and_gives_up_on_a_dead_client(tmp_path):
    stamp = tmp_path / tour.stamp_name(3, "street")
    assert stamp.name == "03_street.ready"
    start = time.time()
    assert tour.wait_for_stamp(stamp, 30, alive=lambda: False) is False
    assert time.time() - start < 5
    stamp.write_text("")
    assert tour.wait_for_stamp(stamp, 1) is True
