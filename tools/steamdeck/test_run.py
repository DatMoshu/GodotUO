"""run.py --dry-run: every command prints what it would run and touches nothing.

No Deck, no Godot export, no ssh and no file write: the test makes every one
of those raise, then runs each command with --dry-run. The configuration is
the repo's, with the Deck's values replaced, so a machine with a Deck in its
config.local.bat and one without get the same results.
"""
from __future__ import annotations

import dataclasses
import shutil
import subprocess
import tarfile
from pathlib import Path

import pytest

import run

ROOT = Path(__file__).resolve().parents[2]
LAUNCHERS = ROOT / "launchers" / "steamdeck"
COMMANDS = ["doctor", "templates", "preset", "export", "push", "run", "stop", "awake",
            "release", "log", "screenshot", "shortcut", "smoke"]


def forbidden(what):
    def call(*args, **kwargs):
        raise AssertionError(f"a dry run called {what}")
    return call


def guarded_open(file, mode="r", *args, **kwargs):
    if any(m in mode for m in "wax+"):
        raise AssertionError(f"a dry run opened {file} for writing")
    return open(file, mode, *args, **kwargs)


@pytest.fixture
def deck(monkeypatch, tmp_path):
    """main(argv) under guards, with the Deck's settings given as keywords."""
    monkeypatch.setattr(run, "DRY_RUN", False)  # restored after the test
    monkeypatch.delenv("GODOT_CONSOLE", raising=False)
    base = run.load_config()

    for name in ("run", "Popen", "call", "check_call", "check_output"):
        monkeypatch.setattr(subprocess, name, forbidden(f"subprocess.{name}"))
    monkeypatch.setattr(shutil, "rmtree", forbidden("shutil.rmtree"))
    monkeypatch.setattr(shutil, "copyfileobj", forbidden("shutil.copyfileobj"))
    for name in ("mkdir", "write_text", "write_bytes", "unlink", "touch"):
        monkeypatch.setattr(Path, name, forbidden(f"Path.{name}"))
    monkeypatch.setattr(run, "open", guarded_open, raising=False)
    monkeypatch.setattr(tarfile, "open", forbidden("tarfile.open"))

    def main(*argv, **deck_values):
        values = {"deck_host": "", "deck_ssh_key": None, "deck_known_hosts": None, "deck_account": "",
                  "root": tmp_path, "build_dir": None, "godot_home_setting": None, **deck_values}
        cfg = dataclasses.replace(base, **values)
        monkeypatch.setattr(run, "load_config", lambda: cfg)
        return run.main([*argv, "--dry-run"])

    return main


def lines(text: str, needle: str) -> list[str]:
    return [line for line in text.splitlines() if needle in line]


@pytest.mark.parametrize("command", COMMANDS)
def test_every_command_dry_runs_with_no_deck_and_no_config(deck, capsys, command):
    assert deck(command) == 0
    out = capsys.readouterr().out
    assert "nothing is exported, sent over ssh or written" in out
    if command not in ("templates", "preset", "export"):
        assert "UO_DECK_HOST is not set" in out


@pytest.mark.parametrize("command", ["doctor", "push", "run", "stop", "awake", "release", "log",
                                     "screenshot", "shortcut", "smoke"])
def test_deck_commands_show_the_placeholder_address(deck, capsys, command):
    deck(command)
    out = capsys.readouterr().out
    ssh = lines(out, "$ ssh ")
    assert ssh, out
    assert all(f"deck@{run.HOST_PLACEHOLDER}" in line for line in ssh)
    assert all("-o BatchMode=yes" in line for line in ssh)


def test_configured_values_reach_the_ssh_line(deck, capsys, tmp_path):
    key = tmp_path / "deck_key"
    deck("log", deck_host="steamdeck-lan", deck_ssh_key=key, deck_user="player")
    out = capsys.readouterr().out
    assert "UO_DECK_HOST is not set" not in out
    (line,) = lines(out, "$ ssh ")
    assert "player@steamdeck-lan" in line
    assert f"-i {key}" in line or f"-i '{key}'" in line
    assert "cat \"$HOME/GUO/guo.log\"" in line


def test_export_prints_the_headless_godot_command(deck, capsys):
    deck("export")
    out = capsys.readouterr().out
    (line,) = lines(out, "--headless")
    assert "--export-debug Linux" in line and run.EXE_NAME in line
    assert "would write" in out and "export_presets.cfg" in out
    assert "export.log" in out


def test_export_prints_both_solution_commands_and_the_add(deck, capsys):
    # With no GUO.sln a real export tries "dotnet new sln --format sln" and,
    # when that fails on an SDK older than 9.0.200, the same line without
    # --format; a dry run prints both, then the "dotnet sln add".
    deck("export")
    out = capsys.readouterr().out
    new = lines(out, "$ dotnet new sln -n GUO")
    assert len(new) == 2
    assert new[0].endswith("--format sln")
    assert "--format" not in new[1]
    (add,) = lines(out, "$ dotnet sln ")
    assert "GUO.csproj" in add


def test_push_without_rsync_streams_a_tar_and_shows_guo_sh(deck, capsys, monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda name: None)
    deck("push", "--host", "shard-lan", "--port", "2600")
    out = capsys.readouterr().out
    assert len(lines(out, "tar -xf - -C")) == 2  # the export, then guo.sh on its own
    assert "rsync" not in "".join(lines(out, "$ "))
    assert "--host shard-lan --port 2600" in out
    assert lines(out, "chmod +x")


def test_push_with_rsync_prints_the_rsync_line(deck, capsys, monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda name: f"/usr/bin/{name}")
    deck("push")
    out = capsys.readouterr().out
    (line,) = lines(out, "rsync -az")
    assert "--delete" in line and "-e 'ssh -o BatchMode=yes" in line
    assert len(lines(out, "tar -xf - -C")) == 1  # guo.sh only


def test_run_starts_a_user_unit_and_holds_the_deck_awake(deck, capsys):
    deck("run", "--args=--sound", "--wait", "30")
    out = capsys.readouterr().out
    (start,) = lines(out, f"--unit={run.UNIT}")
    assert "systemd-run --user --collect --quiet" in start and "guo.sh\" --sound" in start
    assert lines(out, "systemd-inhibit --what=sleep:idle")
    assert "would follow guo.log for 30s" in out


def test_screenshot_prints_the_ssh_and_the_scp(deck, capsys, tmp_path):
    shot = tmp_path / "shot.png"
    deck("screenshot", "--out", str(shot))
    out = capsys.readouterr().out
    assert lines(out, "spectacle -b -n -o /tmp/guo_screen.png")
    (scp,) = lines(out, "$ scp ")
    assert ":/tmp/guo_screen.png" in scp and shot.name in scp
    assert f"would save the screenshot as {shot}" in out


def test_smoke_prints_its_whole_sequence_in_order(deck, capsys):
    deck("smoke", "--host", "shard-lan")
    out = capsys.readouterr().out.splitlines()

    def first(needle: str) -> int:
        return next(i for i, line in enumerate(out) if needle in line)

    order = [first("--headless"), first("tar -xf - -C"), first("systemd-inhibit"),
             first(f"--unit={run.UNIT}"), first("cat \"$HOME/GUO/guo.log\""), first("smoke_guo.log"),
             first("spectacle"), first("$ scp "), first("smoke's sequence ends here")]
    assert order == sorted(order)
    assert "--login-probe-stay --postfx off --silent" in out[first(f"--unit={run.UNIT}")]


def test_smoke_can_skip_export_and_push(deck, capsys):
    deck("smoke", "--no-export", "--no-push")
    out = capsys.readouterr().out
    assert not lines(out, "--headless") and not lines(out, "tar -xf")
    assert lines(out, f"--unit={run.UNIT}")


@pytest.mark.parametrize("bat", sorted(LAUNCHERS.glob("*.bat")), ids=lambda p: p.stem)
def test_each_launcher_passes_its_flags_through(bat):
    command = bat.stem
    assert command in COMMANDS
    assert f'run.py" {command} %*' in bat.read_text(encoding="utf-8")
    sh = bat.with_suffix(".sh").read_text(encoding="utf-8")
    assert f'run.py" {command} "$@"' in sh


def test_release_export_dry_run_uses_release_template_and_fresh_output(deck,capsys):
    assert deck("export","--release")==0
    out=capsys.readouterr().out
    assert "--export-release Linux" in out
    assert "steamdeck-release" in out
