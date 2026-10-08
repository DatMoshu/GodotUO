"""muo_shard tests: the profile is checked strictly, the plans match golden files, the scripts hold nothing they should not."""

from __future__ import annotations

import io
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


# ------------------------------------------------------------ M2: admin, backup, restore, reset, secrets

import hostsecrets  # noqa: E402

M2_CASES = [
    ("guo-dev.admin.sh", lambda p: plans.admin(p), GUO),
    ("guo-dev.backup.sh", lambda p: plans.backup(p), GUO),
    ("guo-dev.backup-named.sh", lambda p: plans.backup(p, "before-wipe"), GUO),
    ("guo-dev.restore.sh", lambda p: plans.restore(p, "20260101_040000"), GUO),
    ("guo-dev.reset.sh", lambda p: plans.reset(p), GUO),
    ("demo-shard.backup.sh", lambda p: plans.backup(p), FULL),
    ("demo-shard.reset.sh", lambda p: plans.reset(p), FULL),
]


@pytest.mark.parametrize("name,fn,profile", M2_CASES)
def test_m2_plan_matches_golden(name, fn, profile):
    got = fn(sp.load(profile))
    golden = GOLDEN / name
    if os.environ.get("MUO_UPDATE_GOLDEN"):
        golden.write_bytes(got.encode("utf-8"))
    assert golden.read_bytes().decode("utf-8").replace("\r\n", "\n") == got


@pytest.mark.parametrize("name,fn,profile", M2_CASES)
def test_m2_plan_is_valid_bash_and_clean(name, fn, profile, tmp_path):
    got = fn(sp.load(profile))
    assert "\r" not in got and not re.search(r"(?i)PASSWORD=\S", got)
    for needle in ["C:\\", "C:/", "/Users/", "ssh ", "sudo "]:
        assert needle not in got, needle
    bash = shutil.which("bash")
    if bash:
        f = tmp_path / "s.sh"
        f.write_bytes(got.encode("utf-8"))
        assert subprocess.run([bash, "-n", str(f)], capture_output=True, text=True).returncode == 0


def test_m2_cli_verbs(capsys):
    assert run.main(["plan", "restore", "seed", "--profile", str(GUO)]) == 0
    assert "NAME=seed" in capsys.readouterr().out
    assert run.main(["plan", "backup", "--name", "x1", "--profile", str(GUO)]) == 0
    assert 'muo-backup.sh" x1' in capsys.readouterr().out
    assert run.main(["plan", "reset", "--profile", str(GUO)]) == 0
    assert "NAME=seed" in capsys.readouterr().out
    with pytest.raises(SystemExit):
        run.main(["plan", "restore", "--profile", str(GUO)])             # no NAME
    with pytest.raises(SystemExit):
        run.main(["plan", "status", "--name", "n", "--profile", str(GUO)])


@pytest.mark.parametrize("bad", ["../x", "a b", "-rf", "x;y", "", "a" * 70])
def test_snapshot_names_are_checked(bad):
    prof = sp.load(GUO)
    with pytest.raises(sp.ProfileError):
        plans.restore(prof, bad)
    if bad:
        with pytest.raises(sp.ProfileError):
            plans.backup(prof, bad)


def test_reset_restores_the_seed_and_needs_one(tmp_path):
    assert "NAME=seed" in plans.reset(sp.load(GUO))
    data = good()
    del data["seed"]
    with pytest.raises(sp.ProfileError) as e:
        plans.reset(load_tmp(tmp_path, data))
    assert "no 'seed'" in str(e.value)


def test_restore_stops_the_shard_and_keeps_what_was_there():
    s = plans.restore(sp.load(GUO), "seed")
    assert s.index("systemctl stop") < s.index("tar -C") < s.rindex("systemctl start")
    assert ".pre-restore" in s and "listening on tcp/$PORT; stop the shard first" in s


def test_backup_timer_follows_the_profile(tmp_path):
    assert "OnCalendar=*-*-* 04:00:00" in plans.backup(sp.load(GUO))
    data = good()
    del data["backup"]["on_calendar"]
    s = plans.backup(load_tmp(tmp_path, data))
    assert "OnCalendar" not in s and "enable --now" not in s
    assert "KEEP=14" in s and "INCLUDE=(Saves)" in s


def test_admin_needs_the_host_values_and_prints_no_password():
    s = plans.admin(sp.load(GUO))
    assert ': "${MUO_ADMIN_USER:?' in s and ': "${MUO_ADMIN_PASSWORD:?' in s
    assert s.count("MUO_ADMIN_PASSWORD") == 2          # the check, nothing that would print it


def test_deploy_warns_when_global_json_is_rewritten():
    s = plans.deploy(sp.load(GUO))
    assert "warning: global.json names SDK $pinned, building with the installed SDK $sdk" in s
    assert s.index("pinned=") < s.index("sed -i")


def _sandbox_helper(tmp_path: Path, keep: int):
    """The backup helper with its host paths moved into tmp_path and a stub tar, runnable without root."""
    prof = sp.load(GUO)
    text = plans._backup_helper(prof).replace("/srv/muo/guo-dev/dist", (tmp_path / "dist").as_posix())
    text = text.replace("install -d -m 0750", "mkdir -p").replace("/var/backups/muo/guo-dev", (tmp_path / "bk").as_posix()).replace("KEEP=14", f"KEEP={keep}")
    (tmp_path / "dist" / "Saves").mkdir(parents=True)
    (tmp_path / "bk").mkdir()
    (tmp_path / "bin").mkdir()
    stub = tmp_path / "bin" / "tar"
    stub.write_bytes(b'#!/usr/bin/env bash\nwhile [ $# -gt 0 ]; do if [ "$1" = -cf ]; then echo snap > "$2"; exit 0; fi; shift; done\nexit 1\n')
    stub.chmod(0o755)
    helper = tmp_path / "muo-backup.sh"
    helper.write_bytes(text.encode("utf-8"))
    return helper, tmp_path / "bk", {**os.environ, "PATH": f"{(tmp_path / 'bin').as_posix()}{os.pathsep}{os.environ['PATH']}"}


@pytest.mark.skipif(not shutil.which("bash"), reason="needs bash")
def test_backup_prunes_to_keep_and_spares_named_snapshots(tmp_path):
    helper, bk, env = _sandbox_helper(tmp_path, keep=3)
    for stamp in ["20260101_010101", "20260102_010101", "20260103_010101", "20260104_010101", "20260105_010101"]:
        (bk / f"{stamp}.tar.zst").write_text("old")
    (bk / "seed.tar.zst").write_text("seed")
    r = subprocess.run([shutil.which("bash"), helper.as_posix(), "20260106_010101"], capture_output=True, text=True, env=env)
    assert r.returncode == 0, r.stderr
    left = sorted(f.name for f in bk.iterdir())
    assert left == ["20260104_010101.tar.zst", "20260105_010101.tar.zst", "20260106_010101.tar.zst", "seed.tar.zst"]


@pytest.mark.skipif(not shutil.which("bash"), reason="needs bash")
def test_backup_with_nothing_to_prune_and_a_name_clash(tmp_path):
    helper, bk, env = _sandbox_helper(tmp_path, keep=14)
    assert subprocess.run([shutil.which("bash"), helper.as_posix(), "seed"], capture_output=True, text=True, env=env).returncode == 0
    assert [f.name for f in bk.iterdir()] == ["seed.tar.zst"]
    again = subprocess.run([shutil.which("bash"), helper.as_posix(), "seed"], capture_output=True, text=True, env=env)
    assert again.returncode == 1 and "already exists" in again.stderr
    assert subprocess.run([shutil.which("bash"), helper.as_posix(), "../x"], capture_output=True, text=True, env=env).returncode == 1


# --- secrets: a fake ssh, no real host

class FakeSsh:
    def __init__(self):
        self.calls = []

    def __call__(self, argv, input=None, text=None, **kw):
        self.calls.append((argv, input))
        return subprocess.CompletedProcess(argv, 0)


def answers(*vals):
    it = iter(vals)
    return lambda prompt="": next(it)


def test_secrets_send_values_on_stdin_only(tmp_path, monkeypatch):
    fake = FakeSsh()
    monkeypatch.setattr(subprocess, "run", fake)
    monkeypatch.chdir(tmp_path)
    before = set(tmp_path.iterdir())
    rc = hostsecrets.run(sp.load(GUO), "ops@host", prompt_fn=answers("/mnt/uo", "owner1"),
                         secret_fn=answers("pa55-w0rd", "pa55-w0rd"))
    assert rc == 0 and len(fake.calls) == 1
    argv, payload = fake.calls[0]
    assert argv[:3] == ["ssh", "--", "ops@host"]
    assert payload == "MUO_CLIENT_DATA=/mnt/uo\nMUO_ADMIN_USER=owner1\nMUO_ADMIN_PASSWORD=pa55-w0rd\n"
    assert not any("pa55-w0rd" in a or "owner1" in a or "/mnt/uo" in a for a in argv)
    assert "/etc/muo/guo-dev.env" in argv[-1] and "0o600" in argv[-1]
    assert set(tmp_path.iterdir()) == before                         # no local file


def test_secrets_mismatch_and_empty_send_nothing(monkeypatch):
    fake = FakeSsh()
    monkeypatch.setattr(subprocess, "run", fake)
    prof = sp.load(GUO)
    with pytest.raises(hostsecrets.SecretsError):
        hostsecrets.run(prof, "h", prompt_fn=answers("", "u"), secret_fn=answers("a", "b"))
    with pytest.raises(hostsecrets.SecretsError):
        hostsecrets.run(prof, "h", prompt_fn=answers("", ""), secret_fn=answers(""))      # nothing to send
    assert fake.calls == []


def test_secrets_blank_answers_keep_the_other_keys(monkeypatch):
    fake = FakeSsh()
    monkeypatch.setattr(subprocess, "run", fake)
    assert hostsecrets.run(sp.load(GUO), "h", prompt_fn=answers("", "only-user"), secret_fn=answers("")) == 0
    assert fake.calls[0][1] == "MUO_ADMIN_USER=only-user\n"


@pytest.mark.parametrize("host", ["-oProxyCommand=x", "", "a b", "h;rm"])
def test_secrets_refuses_odd_hosts(host, monkeypatch):
    fake = FakeSsh()
    monkeypatch.setattr(subprocess, "run", fake)
    with pytest.raises(hostsecrets.SecretsError):
        hostsecrets.run(sp.load(GUO), host, prompt_fn=answers("", "u"), secret_fn=answers(""))
    assert fake.calls == []


@pytest.mark.parametrize("value", ["it's", "a\nb", "a\rb"])
def test_secrets_refuses_values_the_env_file_cannot_hold(value):
    with pytest.raises(hostsecrets.SecretsError):
        hostsecrets.check_value("K", value)


def test_secrets_remote_merge_keeps_other_keys_and_mode(tmp_path):
    env = tmp_path / "x.env"
    env.write_text("MUO_REPO='/srv/r'\nMUO_ADMIN_USER='old'\n", encoding="utf-8")
    cmd = [sys.executable, "-c", hostsecrets._MERGE, str(env)]
    r = subprocess.run(cmd, input="MUO_ADMIN_USER=new\nMUO_ADMIN_PASSWORD=pw\n", text=True, capture_output=True)
    assert r.returncode == 0, r.stderr
    assert env.read_text(encoding="utf-8") == "MUO_REPO='/srv/r'\nMUO_ADMIN_USER='new'\nMUO_ADMIN_PASSWORD='pw'\n"
    if os.name == "posix":
        assert oct(env.stat().st_mode & 0o777) == "0o600"
    assert "pw" not in r.stderr


def test_secrets_refuses_a_pipe(monkeypatch, capsys):
    monkeypatch.setattr(sys, "stdin", io.StringIO(""))
    assert run.main(["secrets", "--host", "h", "--profile", str(GUO)]) == 2
    assert "terminal" in capsys.readouterr().err


# ------------------------------------------------------------ SF3: a patch that does not apply is fatal


def _patch_run(tmp_path, original: str, patch_from: str, patch_to: str):
    """Make a checkout holding `original`, a patch turning `patch_from` into `patch_to`, and run the deploy's patch step on it."""
    bash, git = shutil.which("bash"), shutil.which("git")
    if not bash or not git:
        pytest.skip("no bash or git")
    gitc = [git, "-c", "user.name=t", "-c", "user.email=test", "-c", "core.autocrlf=false"]
    src = tmp_path / "src"
    src.mkdir()
    subprocess.run(gitc + ["init", "-q", str(src)], check=True)
    subprocess.run([git, "-C", str(src), "config", "core.autocrlf", "false"], check=True)
    f = src / "a.txt"
    f.write_bytes(patch_from.encode())
    subprocess.run(gitc + ["-C", str(src), "add", "a.txt"], check=True)
    subprocess.run(gitc + ["-C", str(src), "commit", "-qm", "x"], check=True)
    f.write_bytes(patch_to.encode())
    diff = subprocess.run(gitc + ["-C", str(src), "diff"], check=True, capture_output=True).stdout
    (tmp_path / "patches").mkdir()
    (tmp_path / "patches" / "0001-x.patch").write_bytes(diff)
    f.write_bytes(original.encode())
    script = "\n".join([
        "set -euo pipefail",
        'die() { echo "muo_shard: $*" >&2; exit 1; }',
        'as_user() { "$@"; }',
        'SRC=src BASE="$(pwd)" PIN=deadbeef',
        *plans._apply_patch("0001-x.patch"),
        "echo reached-the-build",
        "",
    ])
    (tmp_path / "s.sh").write_bytes(script.encode())
    r = subprocess.run([bash, "s.sh"], cwd=tmp_path, capture_output=True, text=True)
    return r, f.read_bytes().decode()


def test_patch_that_applies_is_applied(tmp_path):
    r, after = _patch_run(tmp_path, "one\ntwo\n", "one\ntwo\n", "one\nTWO\n")
    assert r.returncode == 0, r.stderr
    assert after == "one\nTWO\n" and "reached-the-build" in r.stdout


def test_patch_already_applied_is_skipped(tmp_path):
    r, after = _patch_run(tmp_path, "one\nTWO\n", "one\ntwo\n", "one\nTWO\n")
    assert r.returncode == 0, r.stderr
    assert "0001-x.patch already applied, skipping" in r.stdout
    assert after == "one\nTWO\n" and "reached-the-build" in r.stdout


def test_patch_that_does_not_apply_stops_the_plan(tmp_path):
    # neither forward nor reverse applies: before SF3 this was reported "already applied" and the build went on
    r, after = _patch_run(tmp_path, "one\nsomething else\n", "one\ntwo\n", "one\nTWO\n")
    assert r.returncode != 0
    assert "already applied" not in r.stdout and "reached-the-build" not in r.stdout
    assert "0001-x.patch does not apply to the checkout at deadbeef; stopping" in r.stderr
    assert "patch failed" in r.stderr  # git's own reason is shown
    assert after == "one\nsomething else\n"


def test_deploy_applies_every_patch_through_the_fatal_step():
    s = plans.deploy(sp.load(GUO))
    patches = good()["server"]["patches"]
    assert patches
    for rel in patches:
        name = Path(rel).name
        assert "\n".join(plans._apply_patch(name)) in s
        assert f'die "{name} does not apply' in s
