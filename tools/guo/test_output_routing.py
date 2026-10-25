"""Legacy command entry points honor an external build folder without launching clients."""
import importlib.util
from pathlib import Path
import sys
from types import SimpleNamespace

import pytest

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))


def module(name, relative):
    spec = importlib.util.spec_from_file_location(name, TOOLS / relative)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def test_mapgen_build_and_reuse_stay_in_configured_output(tmp_path, monkeypatch):
    tool = module("routing_mapgen", "mapgen/run.py")
    root, output = tmp_path / "checkout", tmp_path / "external"
    monkeypatch.setattr(tool, "load_config", lambda given: SimpleNamespace(build=output))
    calls = []

    def compile_cli(command, **kwargs):
        destination = Path(command[command.index("-o") + 1])
        assert destination == output / "mapgen/cli"
        destination.mkdir(parents=True)
        tool.cli_path(root).touch()
        calls.append(command)
        return SimpleNamespace(returncode=0)

    monkeypatch.setattr(tool.subprocess, "run", compile_cli)
    monkeypatch.setattr(tool, "newest_source", lambda: 0)
    first = tool.build(root)
    assert tool.build(root) == first
    assert first.is_relative_to(output) and first.is_file()
    assert len(calls) == 1
    assert not root.exists()


@pytest.mark.parametrize("explicit", [False, True])
def test_postfx_cli_writes_verified_archive_to_selected_destination(tmp_path, monkeypatch, explicit):
    from asset_store import postfx_pack
    output = tmp_path / "external"
    monkeypatch.setattr(postfx_pack, "load_config", lambda root: SimpleNamespace(build=output))
    chosen = tmp_path / "selected.zip" if explicit else output / "asset_packs/jarjar-postfx-1.0.0.zip"
    monkeypatch.setattr(sys, "argv", ["postfx_pack.py", *(["--out", str(chosen)] if explicit else [])])
    assert postfx_pack.main() == 0
    assert chosen.is_file()
    assert postfx_pack.verify(chosen)
    if explicit:
        assert not (output / "asset_packs").exists()
    else:
        assert chosen.parent.is_dir()


def test_content_probe_scratch_and_proof_use_external_build(tmp_path, monkeypatch):
    from asset_store import content_probe
    output = tmp_path / "external"
    monkeypatch.setattr(content_probe, "load_config", lambda root: SimpleNamespace(build=output))
    monkeypatch.setattr(content_probe, "build", lambda destination: destination.mkdir())
    monkeypatch.setattr(sys, "argv", ["content_probe.py", "--godot", "fake-console", "--data", "fake-data",
                                      "--deployment-ui", "--visual", "--compact"])
    calls = []

    def run(command, **kwargs):
        if command[0] == "dotnet":
            if command[2] == "content-lock":
                Path(command[-1]).write_text('{}')
            assert Path(command[3]).is_relative_to(output)
        else:
            env = kwargs["env"]
            assert Path(env["UO_CONTENT_STORE"]).is_relative_to(output)
            assert Path(env["UO_CONTENT_PROOF_IMAGE"]) == output / "asset_packs/runtime-pixels.png"
            assert Path(env["UO_DEPLOYMENT_PROOF_IMAGE"]) == output / "asset_packs/deployment-ui-compact.png"
        calls.append(command)

    monkeypatch.setattr(content_probe.subprocess, "run", run)
    assert content_probe.main() == 0
    assert len(calls) == 7
    assert not list(output.glob("content-probe-*"))


def test_pre_push_snapshot_uses_external_scratch(tmp_path, monkeypatch):
    tool = module("routing_hooks", "git_hooks/check.py")
    root, output = tmp_path / "checkout", tmp_path / "external"
    root.mkdir()
    tip = "a" * 40
    monkeypatch.setattr(tool, "load_config", lambda given: SimpleNamespace(build=output))
    monkeypatch.setattr(tool, "pushed_commits", lambda *args: [])
    snapshots = []

    def git(given, *args):
        if "rev-parse" in args:
            return tip
        if "clone" in args:
            snapshot = Path(args[-1])
            snapshots.append(snapshot)
            assert snapshot.is_relative_to(output / "git_hooks")
            for name in ("docs_lint", "launcher_lint", "privacy_scan"):
                script = snapshot / "tools" / name / "run.py"
                script.parent.mkdir(parents=True)
                script.touch()
        return ""

    monkeypatch.setattr(tool, "git", git)
    monkeypatch.setattr(tool.subprocess, "run", lambda *args, **kwargs: SimpleNamespace(returncode=0))
    assert tool.check(root, f"refs/heads/test {tip} refs/heads/test {'0' * 40}") == 0
    assert snapshots and not snapshots[0].exists()
    assert not (root / "build").exists()
