"""Release package completeness regressions; no engine, network or account."""
import hashlib
import json
import subprocess
from pathlib import Path
from types import SimpleNamespace
import pytest
import run


def package(tmp_path):
    data=tmp_path/"data_GUO_linuxbsd_x86_64";data.mkdir()
    for name, content in {"GUO.dll":b"MZmanaged", "GodotSharp.dll":b"MZapi",
                          "GUO.deps.json":b'{"targets":{"net8.0":{}}}',
                          "GUO.runtimeconfig.json":b'{"runtimeOptions":{"tfm":"net8.0"}}'}.items():
        (data/name).write_bytes(content)
    exe=tmp_path/"GUO.x86_64";exe.write_bytes(b"\x7fELFtemplate")
    pck=tmp_path/"GUO.pck";pck.write_bytes(b"GDPCpack")
    return SimpleNamespace(out_dir=tmp_path,exe=exe,pck=pck,release=True)


def test_complete_package_records_every_hash(tmp_path):
    p=package(tmp_path);result=run.artifact_manifest(p)
    assert result["qualified"] and result["configuration"]=="Release"
    assert len(result["files"])==6
    for name, receipt in result["files"].items():
        assert receipt["sha256"]==hashlib.sha256((tmp_path/name).read_bytes()).hexdigest()
        assert receipt["bytes"]==(tmp_path/name).stat().st_size


@pytest.mark.parametrize("name",["GUO.dll","GodotSharp.dll","GUO.deps.json","GUO.runtimeconfig.json"])
def test_missing_managed_artifact_rejects(tmp_path,name):
    p=package(tmp_path);next(tmp_path.glob("data_*"),None).joinpath(name).unlink()
    with pytest.raises(ValueError):run.artifact_manifest(p)


@pytest.mark.parametrize("name,value",[("GUO.dll",b""),("GUO.dll",b"bad"),
    ("GUO.deps.json",b"not JSON"),("GUO.runtimeconfig.json",b"{}")])
def test_empty_corrupt_or_invalid_metadata_rejects(tmp_path,name,value):
    p=package(tmp_path);next(tmp_path.glob("data_*")).joinpath(name).write_bytes(value)
    with pytest.raises(ValueError):run.artifact_manifest(p)


def test_multiple_managed_roots_reject(tmp_path):
    p=package(tmp_path);(tmp_path/"data_stale").mkdir()
    with pytest.raises(ValueError,match="exactly one"):run.artifact_manifest(p)


def test_zero_exit_stock_template_is_failure(tmp_path,monkeypatch):
    out=tmp_path/"fresh";exe=out/"GUO.x86_64";pck=out/"GUO.pck"
    tool=tmp_path/"engine";tool.write_bytes(b"engine")
    p=SimpleNamespace(out_dir=out,exe=exe,pck=pck,release=True,template_bin=tool,
                      godot_console=lambda:tool,project=tmp_path,data_dir=lambda:None)
    calls=[]
    monkeypatch.setattr(run,"ensure_solution",lambda p:calls.append("solution"))
    monkeypatch.setattr(run,"render_preset",lambda p,e:calls.append("preset"))
    def fake_export(command,**kwargs):
        calls.append(command);exe.write_bytes(b"\x7fELFtemplate");pck.write_bytes(b"GDPCpack")
        return subprocess.CompletedProcess(command,0)
    monkeypatch.setattr(run,"run",fake_export)
    monkeypatch.setattr(run,"DRY_RUN",False)
    assert run.export(p)==1
    assert calls[:2]==["solution","preset"]
    assert "--export-release" in calls[2]
    receipt=json.loads((out/"artifact-manifest.json").read_text())
    assert receipt["qualified"] is False and receipt["exporter_exit"]==0


def test_release_existing_output_preserved(tmp_path,monkeypatch):
    p=package(tmp_path);marker=tmp_path/"keep";marker.write_text("prior evidence")
    p.template_bin=p.exe;p.godot_console=lambda:p.exe
    monkeypatch.setattr(run,"ensure_solution",lambda p:pytest.fail("must refuse before preparation"))
    monkeypatch.setattr(run,"DRY_RUN",False)
    assert run.export(p)==1 and marker.read_text()=="prior evidence"


def test_clean_worktree_solution_generated_and_project_added(tmp_path,monkeypatch):
    p=SimpleNamespace(project=tmp_path,solution=tmp_path/"GUO.sln")
    calls=[]
    def fake(command,**kwargs):
        calls.append([str(v) for v in command])
        if command[:3]==["dotnet","new","sln"]:p.solution.write_text("solution")
        return subprocess.CompletedProcess(command,0)
    monkeypatch.setattr(run,"DRY_RUN",False);monkeypatch.setattr(run.subprocess,"run",fake)
    run.ensure_solution(p)
    assert p.solution.exists()
    assert calls[0][-2:]==["--format","sln"]
    assert calls[1]==["dotnet","sln",str(p.solution),"add",str(tmp_path/"GUO.csproj")]


def test_dry_release_refuses_same_existing_output_without_mutation(tmp_path,monkeypatch):
    p=package(tmp_path);before={f.name:f.read_bytes() for f in tmp_path.iterdir() if f.is_file()}
    monkeypatch.setattr(run,"ensure_solution",lambda p:pytest.fail("must not prepare existing output"))
    assert run.export_dry(p,p.exe)==1
    assert {f.name:f.read_bytes() for f in tmp_path.iterdir() if f.is_file()}==before
