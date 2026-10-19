"""The perf lab's report: what it keeps from the probe, what it refuses to share, and the doctor's shard check."""
from __future__ import annotations

import datetime as dt
import json
import socket
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import run  # noqa: E402

WHEN = dt.datetime(2026, 10, 18, 21, 40, tzinfo=dt.timezone.utc)


def probe(window="1920x1080", zoom=1.0, **system):
    return {
        "label": "lab_1080p", "window": window, "zoom": zoom, "optimized": True, "postfx": "Classic (0 pass(es), --postfx)",
        "system": {"gpu": "Some GPU", "driver_version": "1.2.3", "cpu": "Some CPU", "cpu_threads": 16, "os": "Windows",
                   "os_version": "10.0.26200", "screen": "3840x2160", **system},
        "scenes": [
            {"scene": "login", "fps": 600.0, "mean_ms": 1.6, "draw_calls": 25, "land_layers": 3, "at": ""},
            {"scene": "britain-bank", "fps": 20.0, "mean_ms": 50.0, "p95_ms": 60.0, "p99_ms": 65.0, "draw_calls": 8000,
             "batcher_items": 1600, "world_prepare_ms": 20.0, "at": "1434,1699,0 map 0"},
        ],
    }


def test_report_keeps_only_the_shared_fields():
    rep = run.build_report([("1080p", "1920,1080", probe())], "abc1234", WHEN)
    assert rep["format"] == run.FORMAT
    assert rep["when"] == "2026-10-18T21:40Z"
    assert rep["system"]["gpu"] == "Some GPU"
    assert set(rep["system"]) == set(run.SYSTEM_FIELDS)
    login, bank = run.scenes(rep["runs"][0])
    assert bank["fps"] == 20.0 and bank["draw_calls"] == 8000
    assert "at" not in bank and "land_layers" not in login
    assert login["p95_ms"] is None
    assert rep["runs"][0]["window_asked"] == "1920x1080"


def test_table_names_every_scene_and_the_machine():
    text = run.table(run.build_report([("1080p", "1920,1080", probe())], "abc1234", WHEN))
    assert "| britain-bank | 20.0 | 50.0 |" in text
    assert "Some GPU" in text and "Some CPU" in text
    assert "asked" not in text


def test_window_note_says_why():
    small = run.build_report([("1440p-far", "2560,1440", probe(window="1920x1080", screen="1920x1080"))], "x", WHEN)
    assert "larger than the screen" in run.table(small)
    login_only = probe(window="640x480")
    login_only["scenes"] = login_only["scenes"][:1]
    assert "did not run" in run.table(run.build_report([("1080p", "1920,1080", login_only)], "x", WHEN))


@pytest.mark.parametrize("bad", ["C:\\Games\\uo", "D:/games/uo", "/mnt/games/uo", "\\\\server\\share"])
def test_paths_are_refused(bad):
    rep = run.build_report([("1080p", "1920,1080", probe(gpu=bad))], "x", WHEN)
    assert run.privacy_problems(rep, set())


def test_local_names_are_refused_as_words_only():
    rep = run.build_report([("1080p", "1920,1080", probe(cpu="Ryzen built by kaiju"))], "x", WHEN)
    assert run.privacy_problems(rep, {"kaiju"})
    # A name inside a longer word is not that name.
    assert not run.privacy_problems(rep, {"kai"})


def test_a_clean_report_is_written(tmp_path):
    rep = run.build_report([("1080p", "1920,1080", probe())], "abc1234", WHEN)
    path = run.write_report(tmp_path, rep, {"nobody-here"})
    assert json.loads(path.read_text(encoding="utf-8"))["guo"] == "abc1234"
    assert path.with_suffix(".md").exists()


def test_a_report_naming_someone_is_not_written(tmp_path):
    rep = run.build_report([("1080p", "1920,1080", probe(gpu="guoprobe's card"))], "x", WHEN)
    with pytest.raises(SystemExit):
        run.write_report(tmp_path, rep, {"guoprobe"})
    assert not list(tmp_path.iterdir())


def test_shard_up_sees_a_listener_without_connecting(monkeypatch):
    srv = socket.socket()
    srv.bind(("127.0.0.1", 0))
    srv.listen(1)
    srv.settimeout(0.2)
    port = srv.getsockname()[1]
    try:
        assert run.shard_up("127.0.0.1", port)
        # Nothing connected to find that out (the shard bans a silent connection).
        with pytest.raises(socket.timeout):
            srv.accept()
    finally:
        srv.close()
    assert not run.shard_up("127.0.0.1", port)


def test_a_remote_shard_is_not_checked():
    assert run.shard_up("shard.example.org", 2593) is None


def test_parse_listening_reads_netstat_and_ss():
    windows = ("  TCP    0.0.0.0:2593           0.0.0.0:0              LISTENING       62360\n"
               "  TCP    127.0.0.1:50001        127.0.0.1:2593         ESTABLISHED     1\n"
               "  TCP    [::]:445               [::]:0                 LISTENING       4")
    linux = "LISTEN 0      512          0.0.0.0:2593      0.0.0.0:*\nLISTEN 0 4096 [::]:22 [::]:*"
    mac = "tcp4       0      0  *.2593                 *.*                    LISTEN"
    assert run.parse_listening(windows) == {2593, 445}
    assert run.parse_listening(linux) == {2593, 22}
    assert run.parse_listening(mac) == {2593}


def test_a_two_preset_report_fits_a_discord_message(tmp_path):
    many = probe()
    many["scenes"] = [dict(many["scenes"][1], scene=n) for n in ("login", "open-field", "britain-bank", "dense-forest", "dungeon", "walk")]
    for s in many["scenes"]:
        s.update({k: 1234.5 for k in run.SCENE_FIELDS if k != "scene"})  # as wide as the bank at 2.5x
    rep = run.build_report([("1080p", "1920,1080", many), ("1440p-far", "2560,1440", many)], "96ce0ff2-dirty", WHEN)
    assert len(run.write_report(tmp_path, rep, set()).read_text(encoding="utf-8")) < 2000
