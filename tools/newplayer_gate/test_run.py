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
