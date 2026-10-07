"""muo_shard tests: the profile is checked strictly, the plans match golden files, the scripts hold nothing they should not."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import plans  # noqa: E402
import run  # noqa: E402
import shardprofile as sp  # noqa: E402

REPO = HERE.parents[1]
GUO = HERE / "profiles" / "guo-dev.profile.json"
FULL = HERE / "tests" / "fixtures" / "full.profile.json"
GOLDEN = HERE / "tests" / "golden"


def good() -> dict:
    return json.loads(GUO.read_text(encoding="utf-8"))


def load_tmp(tmp_path: Path, data, name="p.profile.json"):
    return sp.load(write(tmp_path, data, name), root=REPO)


def write(tmp_path: Path, data, name="p.profile.json") -> Path:
    f = tmp_path / name
    f.write_text(data if isinstance(data, str) else json.dumps(data), encoding="utf-8")
    return f


# ------------------------------------------------------------ validate


def test_good_profiles_validate():
    assert sp.load(GUO).id == "guo-dev"
    assert sp.load(FULL).id == "demo-shard"


def test_unknown_key_is_refused(tmp_path):
    data = good()
    data["serice"] = {"memory_max": "4G"}
    with pytest.raises(sp.ProfileError) as e:
        load_tmp(tmp_path, data)
    assert "unknown field 'serice'" in str(e.value)


def test_unknown_nested_key_is_refused(tmp_path):
    data = good()
    data["listen"]["adress"] = "0.0.0.0"
    with pytest.raises(sp.ProfileError) as e:
        load_tmp(tmp_path, data)
    assert "unknown field 'adress'" in str(e.value)


def test_missing_required_key_is_refused(tmp_path):
    data = good()
    del data["listen"]
    with pytest.raises(sp.ProfileError) as e:
        load_tmp(tmp_path, data)
    assert "missing required field 'listen'" in str(e.value)


def test_missing_required_nested_key_is_refused(tmp_path):
    data = good()
    del data["server"]["source"]
    with pytest.raises(sp.ProfileError) as e:
        load_tmp(tmp_path, data)
    assert "missing required field 'source'" in str(e.value)


def test_bad_values_are_refused(tmp_path):
    for mutate, needle in [
        (lambda d: d.update(id="Bad_ID"), "id"),
        (lambda d: d["listen"].update(port=80), "out of range"),
        (lambda d: d["server"]["source"].update(ref="main"), "ref"),
        (lambda d: d.update(version=2), "must be 1"),
        (lambda d: d.update(host_packages=["-oDpkg::Options::=x"]), "host_packages"),
    ]:
        data = good()
        mutate(data)
        with pytest.raises(sp.ProfileError) as e:
            load_tmp(tmp_path, data)
        assert needle in str(e.value)


def test_machine_path_and_credential_are_refused(tmp_path):
    data = good()
    data["name"] = "C:" + "\\Users\\someone\\shard"
    with pytest.raises(sp.ProfileError, match="machine path"):
        load_tmp(tmp_path, data)
    data = good()
    data["service"]["exec_start_pre"] = ["sh", "-c", "export password=hunter2"]
    with pytest.raises(sp.ProfileError, match="credential"):
        load_tmp(tmp_path, data)


def test_files_must_exist_and_stay_in_the_repo(tmp_path):
    data = good()
    data["server"]["patches"] = ["tools/modernuo/patches/nope.patch"]
    with pytest.raises(sp.ProfileError, match="not found"):
        load_tmp(tmp_path, data)
    data = good()
    data["config_overlay"] = "a/../../elsewhere"
    with pytest.raises(sp.ProfileError, match="leaves the repo"):
        load_tmp(tmp_path, data)


def test_unknown_placeholder_is_refused(tmp_path):
    data = good()
    data["service"]["exec_start_pre"] = ["python3", "{repo}/x.py"]
    with pytest.raises(sp.ProfileError, match="unknown placeholder"):
        load_tmp(tmp_path, data)


def test_not_json_and_too_big_are_refused(tmp_path):
    with pytest.raises(sp.ProfileError, match="not UTF-8 JSON"):
        sp.load(write(tmp_path, "{nope"))
    with pytest.raises(sp.ProfileError, match="KiB"):
        sp.load(write(tmp_path, " " * (70 * 1024)))


def test_checker_agrees_with_jsonschema(tmp_path):
    jsonschema = pytest.importorskip("jsonschema")
    schema = json.loads(sp.SCHEMA.read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator.check_schema(schema)
    cases = [good(), json.loads(FULL.read_text(encoding="utf-8"))]
    for mutate in [
        lambda d: d.update(extra=1),
        lambda d: d.pop("listen"),
        lambda d: d["listen"].update(port=99999),
        lambda d: d["server"]["source"].update(kind="svn"),
        lambda d: d["server"].update(patches="x"),
        lambda d: d.update(version=True),
    ]:
        d = good()
        mutate(d)
        cases.append(d)
    for d in cases:
        mine: list[str] = []
        sp.check(d, schema, schema, "", mine)
        theirs = list(jsonschema.Draft202012Validator(schema).iter_errors(d))
        assert bool(mine) == bool(theirs), d


def test_cli_exit_codes(tmp_path, capsys):
    assert run.main(["validate", "--profile", str(GUO)]) == 0
    bad = good()
    bad["nope"] = 1
    assert run.main(["validate", "--profile", str(write(tmp_path, bad))]) == 1
    assert "unknown field 'nope'" in capsys.readouterr().err


def test_guo_dev_pin_is_the_shared_pin():
    ref = run.pinned_ref()
    assert ref and good()["server"]["source"]["ref"] == ref


def test_guo_dev_names_every_patch():
    on_disk = sorted(p.name for p in (REPO / "tools" / "modernuo" / "patches").glob("*.patch"))
    assert sorted(Path(p).name for p in good()["server"]["patches"]) == on_disk


# ------------------------------------------------------------ plans


def text(verb, profile=GUO, **kw) -> str:
    return plans.VERBS[verb](sp.load(profile), **kw)


CASES = [
    ("guo-dev.bootstrap.sh", "bootstrap", GUO),
    ("guo-dev.deploy.sh", "deploy", GUO),
    ("guo-dev.status.sh", "status", GUO),
    ("demo-shard.bootstrap.sh", "bootstrap", FULL),
    ("demo-shard.deploy.sh", "deploy", FULL),
    ("demo-shard.status.sh", "status", FULL),
]


@pytest.mark.parametrize("name,verb,profile", CASES)
def test_plan_matches_golden(name, verb, profile):
    got = text(verb, profile)
    golden = GOLDEN / name
    if os.environ.get("MUO_UPDATE_GOLDEN"):
        golden.write_bytes(got.encode("utf-8"))
    assert golden.read_bytes().decode("utf-8").replace("\r\n", "\n") == got


@pytest.mark.parametrize("name,verb,profile", CASES)
def test_plan_is_valid_bash_and_clean(name, verb, profile, tmp_path):
    got = text(verb, profile)
    assert "\r" not in got
    for needle in ["C:\\", "C:/", "/Users/", "ssh ", "sudo "]:
        assert needle not in got, needle
    assert not re.search(r"(?i)MUO_ADMIN_PASSWORD=\S", got)
    bash = shutil.which("bash")
    if bash:
        f = tmp_path / "s.sh"
        f.write_bytes(got.encode("utf-8"))
        r = subprocess.run([bash, "-n", str(f)], capture_output=True, text=True)
        assert r.returncode == 0, r.stderr


def test_plan_runs_nothing_and_prints_nothing_but_the_script(capsys, monkeypatch):
    called = []
    monkeypatch.setattr(subprocess, "run", lambda *a, **k: called.append(a))
    assert run.main(["plan", "bootstrap", "--profile", str(GUO)]) == 0
    out = capsys.readouterr().out
    assert out.startswith("#!/usr/bin/env bash\n") and called == []


def test_bootstrap_is_idempotent_by_construction():
    s = text("bootstrap")
    assert 'id -u "$SVC_USER"' in s                      # the user is created only when absent
    assert "dpkg -s" in s                                # packages only when missing
    assert "install -d" in s and "mkdir" not in s
    assert '[ -e "$ENV_FILE" ] ||' in s                  # the env file is never overwritten
    assert "useradd" in s and s.index('id -u "$SVC_USER"') < s.index("useradd")
    assert "rm -" not in s and ">>" not in s


def test_bootstrap_packages():
    assert "dotnet-sdk-10.0" in text("bootstrap") and "libdeflate-dev" in text("bootstrap")
    s = text("bootstrap", FULL)
    assert "rsync" in s and "libicu-dev" in s


def test_scripts_hold_no_secret():
    for name, verb, profile in [(c[0], c[1], c[2]) for c in CASES]:
        s = text(verb, profile)
        assert not re.search(r"MUO_ADMIN_PASSWORD=", s)
        assert "hunter2" not in s


def test_deploy_reuses_the_existing_tools():
    s = text("deploy")
    assert '"serverListing.serverName": "GUO Dev"' in s            # tools/modernuo/config template
    assert "\"Endless Journey\"" in s                                # the overlay's expansion.json
    for p in (REPO / "tools" / "modernuo" / "patches").glob("*.patch"):
        assert p.name in s                                         # tools/modernuo/patches
    assert "ignored" not in s and "modernuo.template.json" not in s
    assert "0.0.0.0:2593" in s and "@UO_" not in s
    assert "PIN=" + good()["server"]["source"]["ref"] in s         # the shared pin


def test_deploy_pin_override():
    sha = "f" * 40
    assert "PIN=" + sha in text("deploy", pin=sha)
    with pytest.raises(sp.ProfileError):
        text("deploy", pin="main")


def test_deploy_keeps_saves():
    s = text("deploy")
    assert "rm -rf" not in s and "rm -r " not in s


def test_exec_start_pre_expands_src_dist_and_data_dir():
    s = text("deploy", FULL)
    line = next(x for x in s.splitlines() if x.startswith("ExecStartPre=") and "shard_data" in x)
    assert line == ('ExecStartPre="python3" "/srv/muo/demo-shard/src/tools/shard_data/run.py" "--data" '
                    '"${DEMO_DATA}" "--out" "/srv/muo/demo-shard/dist/Data" "--note" "100%% \\"sure\\" $$HOME"')
    assert "WorkingDirectory=/srv/muo/demo-shard/dist" in s       # every Exec line runs in {dist}


def test_swuo_style_exec_start_pre(tmp_path):
    data = good()
    data["service"]["exec_start_pre"] = ["python3", "{src}/tools/shard_data/run.py", "--data", "{data_dir}"]
    s = plans.deploy(load_tmp(tmp_path, data))
    assert ('ExecStartPre="python3" "/srv/muo/guo-dev/src/tools/shard_data/run.py" "--data" "${MUO_CLIENT_DATA}"') in s


def test_unit_ordering_and_limits():
    unit = plans.unit_text(sp.load(FULL))
    assert unit.index("ExecStartPre=/usr/bin/python3") < unit.index('ExecStartPre="python3"') < unit.index("ExecStart=")
    assert "MemoryMax=6G" in unit and "Restart=always" in unit and "User=demo-svc" in unit
    assert "EnvironmentFile=/etc/muo/demo-shard.env" in unit
    guo = plans.unit_text(sp.load(GUO))
    assert "MemoryMax=4G" in guo and "Restart=on-failure" in guo and "ExecStartPre" not in guo


def test_overlay_values_are_filled_on_the_host_only():
    s = text("deploy", FULL)
    assert "{{MUO_CONTACT}}" in s and "muo-fill.py" in s
    assert "DEMO_DATA" in s and '"clientData.clientVersion"' in s


def test_content_needs_the_repo_on_the_host():
    s = text("deploy", FULL)
    assert 'MUO_REPO:?' in s and "shard_content/run.py" in s and "'Demo Shard'" in s
    assert "MUO_REPO" not in text("deploy")


def test_data_manifest_is_checked_now_and_at_every_start():
    s = text("deploy", FULL)
    assert 'python3 "$DIST/muo-verify-data.py"' in s
    assert "ExecStartPre=/usr/bin/python3" in s


def test_bad_manifest_is_refused(tmp_path):
    root = tmp_path / "r"
    root.mkdir()
    (root / ".git").write_text("gitdir: x")
    data = json.loads(FULL.read_text(encoding="utf-8"))
    shutil.copytree(HERE / "tests" / "fixtures", root / "tools" / "muo_shard" / "tests" / "fixtures")
    m = root / "tools" / "muo_shard" / "tests" / "fixtures" / "data.manifest.json"
    m.write_text('{"files": {}}')
    f = root / "p.profile.json"
    f.write_text(json.dumps(data))
    with pytest.raises(sp.ProfileError, match="data_manifest"):
        plans.deploy(sp.load(f))


def test_submodule_source_reads_the_gitlink(tmp_path):
    git = shutil.which("git")
    if not git:
        pytest.skip("no git")
    sha = "a" * 40
    root = tmp_path / "r"
    root.mkdir()
    subprocess.run([git, "init", "-q", str(root)], check=True)
    (root / ".gitmodules").write_text('[submodule "ext/modernuo"]\n\tpath = ext/modernuo\n\turl = https://example.invalid/m.git\n')
    subprocess.run([git, "-C", str(root), "update-index", "--add", "--cacheinfo", f"160000,{sha},ext/modernuo"], check=True)
    subprocess.run([git, "-C", str(root), "add", ".gitmodules"], check=True)
    subprocess.run([git, "-C", str(root), "-c", "user.name=t", "-c", "user.email=test", "commit", "-qm", "x"], check=True)
    (root / "ext" / "modernuo").mkdir(parents=True)
    data = good()
    data["server"] = {"source": {"kind": "submodule", "path": "ext/modernuo"}}
    data.pop("config_overlay")
    f = root / "p.profile.json"
    f.write_text(json.dumps(data))
    s = plans.deploy(sp.load(f))
    assert f"PIN={sha}" in s and "CLONE_URL=https://example.invalid/m.git" in s


def test_custom_build_command_runs_as_the_service_user(tmp_path):
    data = good()
    data["server"]["build"] = {"command": ["dotnet", "publish", "-c", "Release"], "output": "out/{pin}"}
    s = plans.deploy(load_tmp(tmp_path, data))
    assert "dotnet publish -c Release" in s and "out/" + data["server"]["source"]["ref"] in s
    assert "publish.sh" not in s


def test_status_is_read_only():
    s = text("status")
    assert "set -euo pipefail" in s and "run this as root" not in s
    for word in ["install ", "useradd", "apt-get", "systemctl enable", "systemctl restart", "rm "]:
        assert word not in s
