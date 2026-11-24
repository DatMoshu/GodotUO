"""Self-test of the new-player gate's own logic. Starts no client and needs no shard."""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

_spec = importlib.util.spec_from_file_location("newplayer_gate_run", Path(__file__).with_name("run.py"))
gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gate)


def test_account_is_short_lowercase_and_changes_with_time_and_token():
    a = gate.new_account(now=1795000000, token="ab")
    assert a.startswith("np") and a == a.lower() and a.isalnum() and len(a) <= 16
    assert gate.new_account(now=1795000060, token="ab") != a
    assert gate.new_account(now=1795000000, token="cd") != a


def test_character_name_is_letters_only_and_stable():
    name = gate.character_name("np7x2k9f")
    assert name.isalpha() and name[0].isupper() and 4 <= len(name) <= 12
    assert gate.character_name("np7x2k9f") == name


def test_chain_scenarios_exist_and_ids_match():
    import json
    for _, scenario in gate.CHAIN:
        path = gate.ROOT / "tools" / "scenarios" / "client" / (scenario.split(".", 1)[1].replace(".", "_") + ".scenario.json")
        assert json.loads(path.read_text(encoding="utf-8"))["id"] == scenario


def test_chain_stops_at_first_failure_and_returns_its_code():
    calls, lines = [], []

    def run(cmd, env, cwd):
        calls.append(cmd[2])
        return 1 if len(calls) == 1 else 0

    code = gate.run_chain(list(gate.CHAIN), "npx", "Guox", {}, [], run=run, out=lines.append)
    assert code == 1 and calls == [gate.CHAIN[0][1]]
    assert any("skip" in line for line in lines) and lines[-1].startswith("[newplayer] FAIL")


def test_chain_passes_account_and_name_but_never_a_password():
    seen = []

    def run(cmd, env, cwd):
        seen.append(cmd)
        return 0

    lines = []
    assert gate.run_chain(list(gate.CHAIN), "npx", "Guox", {"GUO_SCENARIO_PASSWORD": "s3cret"}, [], run=run, out=lines.append) == 0
    assert len(seen) == len(gate.CHAIN)
    assert all("account=npx" in cmd and "charname=Guox" in cmd for cmd in seen)
    assert not any("s3cret" in part for cmd in seen for part in cmd) and not any("s3cret" in line for line in lines)
    assert lines[-1].startswith("[newplayer] PASS")


def test_unknown_link_is_refused():
    with pytest.raises(SystemExit):
        gate.select(["nosuch"])


def test_other_options_reach_the_scenario_runner_unchanged():
    seen = []

    def run(cmd, env, cwd):
        seen.append(cmd)
        return 0

    gate.run_chain(list(gate.CHAIN), "npx", "Guox", {}, ["--no-record", "--client-exe", "C:/pkg/GUO.exe"], run=run, out=lambda _: None)
    assert all(cmd[-3:] == ["--no-record", "--client-exe", "C:/pkg/GUO.exe"] for cmd in seen)


def test_the_chain_gets_one_home_for_all_links_and_removes_it(monkeypatch):
    homes = []

    def run_chain(links, account, charname, env, extra, **kw):
        home = gate.Path(extra[extra.index("--home") + 1])
        homes.append((home, home.is_dir()))
        return 0

    monkeypatch.setattr(gate, "run_chain", run_chain)
    monkeypatch.setattr(gate, "free_memory_gb", lambda: None)
    assert gate.main([]) == 0
    assert homes[0][1] and not homes[0][0].exists()


def _profile(home, **kw):
    import json
    d = home / "Data" / "Profiles" / "acct" / "Shard" / "Char"
    d.mkdir(parents=True)
    (d / "profile.json").write_text(json.dumps(kw), encoding="utf-8")


def test_saved_options_check(tmp_path):
    assert "no saved" in gate.check_saved_options(tmp_path)
    _profile(tmp_path, always_run=True, fast_rotation=False)
    assert "fast_rotation" in gate.check_saved_options(tmp_path)


def test_saved_options_pass(tmp_path):
    _profile(tmp_path, always_run=True, fast_rotation=True)
    assert gate.check_saved_options(tmp_path) is None


def test_chain_fails_when_options_not_saved(tmp_path):
    lines = []
    code = gate.run_chain([("options", "client.newplayer.options")], "npx", "Guox", {}, [], run=lambda *a, **k: 0, out=lines.append, home=tmp_path)
    assert code == 1 and any("not kept" in l for l in lines)


def test_fixture_runs_once_just_before_the_combat_link():
    order = []

    def run(cmd, env, cwd):
        order.append(cmd[2])
        return 0

    def fixture(extra):
        order.append("fixture")
        return 0

    assert gate.run_chain(list(gate.CHAIN), "npx", "Guox", {}, [], run=run, out=lambda _: None, fixture=fixture) == 0
    assert order.count("fixture") == 1 and order[order.index("fixture") + 1] == "client.newplayer.combat"


def test_a_fixture_that_fails_stops_the_chain_with_its_code():
    ran, lines = [], []
    code = gate.run_chain(list(gate.CHAIN), "npx", "Guox", {}, [], run=lambda cmd, env, cwd: ran.append(cmd[2]) or 0,
                          out=lines.append, fixture=lambda extra: 2)
    assert code == 2 and "client.newplayer.combat" not in ran and lines[-1].startswith("[newplayer] FAIL")


def test_fixture_runs_on_its_own_home_with_the_game_master_password(monkeypatch):
    cfg = type("Cfg", (), {"shard_gm_accounts": ["guoeffects", "guohighlight"], "shard_gm_password": "gmpw"})()
    import guo
    monkeypatch.setattr(guo, "load_config", lambda: cfg)
    seen = {}

    def run(cmd, env, cwd):
        seen["cmd"], seen["env"] = cmd, env
        seen["home_existed"] = gate.Path(cmd[cmd.index("--home") + 1]).is_dir()
        return 0

    assert gate.place_fixture(["--no-record", "--home", "C:/player_home"], out=lambda _: None, run=run) == 0
    cmd = seen["cmd"]
    assert cmd[2] == gate.FIXTURE and "account=guoeffects" in cmd and "C:/player_home" not in cmd and "--no-record" in cmd
    assert seen["env"]["GUO_SCENARIO_PASSWORD"] == "gmpw" and seen["home_existed"]
    assert not gate.Path(cmd[cmd.index("--home") + 1]).exists()
    assert not any("gmpw" in part for part in cmd)


def test_fixture_without_a_game_master_is_a_missing_prerequisite(monkeypatch):
    import guo
    monkeypatch.setattr(guo, "load_config", lambda: type("Cfg", (), {"shard_gm_accounts": [], "shard_gm_password": ""})())
    assert gate.place_fixture([], out=lambda _: None, run=lambda *a, **k: 0) == 2


def test_generated_scenarios_match_their_generator():
    import json
    _s = importlib.util.spec_from_file_location("newplayer_make", Path(__file__).with_name("make_scenarios.py"))
    make = importlib.util.module_from_spec(_s)
    _s.loader.exec_module(make)
    for name in ("equip", "combat", "reconnect", "fixture"):
        on_disk = json.loads((make.CLIENT / f"newplayer_{name}.scenario.json").read_text(encoding="utf-8"))
        assert on_disk == getattr(make, name)(), f"run python tools/newplayer_gate/make_scenarios.py ({name} is stale)"
