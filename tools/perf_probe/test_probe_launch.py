"""The pinned renderer must be an engine argument, not a client argument."""
import sys
from types import SimpleNamespace

import pytest

import run


def test_renderer_is_passed_before_engine_separator(tmp_path, monkeypatch):
    cfg = SimpleNamespace(build=tmp_path, client_data=tmp_path, client_version="test",
                          shard_host="localhost", shard_port=2593,
                          shard_owner="", shard_owner_password="",
                          shard_gm_accounts=[], shard_gm_password="",
                          godot_console_exe=tmp_path / "godot-console", godot_project=tmp_path)
    monkeypatch.setattr(run, "load_config", lambda: cfg)
    monkeypatch.setattr(run, "no_activate", lambda: {})
    monkeypatch.setattr(run, "build_client", lambda *a, **kw: None)
    commands = []
    monkeypatch.setattr(run.subprocess, "run", lambda cmd, **kwargs:
                        commands.append(cmd) or SimpleNamespace(returncode=0))
    monkeypatch.setattr(sys, "argv", ["run.py", "--label", "synthetic", "--renderer", "gl_compatibility"])
    assert run.main() == 0
    cmd = commands[0]
    assert cmd[cmd.index("--rendering-method") + 1] == "gl_compatibility"
    assert cmd.index("--rendering-method") < cmd.index("--")


def test_exported_build_runs_instead_of_the_editor(tmp_path, monkeypatch):
    cfg = SimpleNamespace(build=tmp_path, client_data=tmp_path, client_version="test",
                          shard_host="localhost", shard_port=2593,
                          shard_owner="", shard_owner_password="",
                          shard_gm_accounts=[], shard_gm_password="",
                          godot_console_exe=tmp_path / "godot-console", godot_project=tmp_path)
    exe = tmp_path / "GUO.exe"
    exe.touch()
    monkeypatch.setattr(run, "load_config", lambda: cfg)
    monkeypatch.setattr(run, "no_activate", lambda: {})
    monkeypatch.setattr(run, "build_client", lambda *a, **kw: pytest.fail("an export is never rebuilt"))
    commands = []
    monkeypatch.setattr(run.subprocess, "run", lambda cmd, **kwargs:
                        commands.append(cmd) or SimpleNamespace(returncode=0))
    monkeypatch.setattr(sys, "argv", ["run.py", "--label", "synthetic", "--exe", str(exe)])
    assert run.main() == 0
    assert commands[0][0] == str(exe) and "--path" not in commands[0][:commands[0].index("--")]
