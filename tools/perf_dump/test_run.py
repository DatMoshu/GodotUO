"""Completion receipts must not terminate a sustained sample early."""
import importlib.util
import json
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location("perf_dump_wait_tests", Path(__file__).with_name("run.py"))
runner = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = runner
spec.loader.exec_module(runner)


class Running:
    def poll(self):
        return None


def test_one_shot_receipt_remains_compatible(tmp_path):
    path = tmp_path / "guo.json"
    path.write_text('{"client":"guo"}')
    assert runner.wait_for(path, Running(), 1)


def test_sustained_receipt_waits_until_complete(tmp_path, monkeypatch):
    path = tmp_path / "guo.json"
    path.write_text('{"sample_complete":false}')
    waits = []

    def finish(_):
        waits.append(1)
        path.write_text(json.dumps({"sample_complete": True}))

    monkeypatch.setattr(runner.time, "sleep", finish)
    assert runner.wait_for(path, Running(), 1)
    assert waits == [1]


def test_partial_write_is_retried(tmp_path, monkeypatch):
    path = tmp_path / "guo.json"
    path.write_text('{"sample_')
    monkeypatch.setattr(runner.time, "sleep", lambda _: path.write_text('{"sample_complete":true}'))
    assert runner.wait_for(path, Running(), 1)


def test_exit_with_incomplete_receipt_is_not_success(tmp_path):
    path = tmp_path / "guo.json"
    path.write_text('{"sample_complete":false}')

    class Exited:
        def poll(self):
            return 0

    assert not runner.wait_for(path, Exited(), 1)
