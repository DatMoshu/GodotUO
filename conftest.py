"""Pooled pytest run over every tools/<job>/ test folder (pytest.ini, launchers/dev/pytest_all).

Each job folder imports its own siblings by bare name (run, check, decorate, ...) from its own folder on
sys.path. Run alone, a folder owns those names; pooled in one process, the first folder to import `run`
would own it for everyone. So each folder gets its own sys.path and its own set of tools modules, swapped
in before that folder's test files are imported and again before each of its tests runs. The shared
`guo` package and pytest's own test-module names (tools.*) stay loaded for all.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

TOOLS = Path(__file__).resolve().parent / "tools"
SHARED = TOOLS / "guo"

_base_path = list(sys.path)
_modules: dict[Path, dict] = {}   # folder -> the tools modules it had loaded when it was last left
_paths: dict[Path, list] = {}     # folder -> its sys.path when it was last left
_current: Path | None = None


def _folder_local(name: str, module) -> bool:
    if name.startswith("tools.") or name == "tools":
        return False
    file = getattr(module, "__file__", None)
    if not file:
        return False
    path = Path(file).resolve()
    return TOOLS in path.parents and SHARED not in path.parents


def _enter(folder: Path) -> None:
    global _current
    if folder == _current or TOOLS not in folder.parents:
        return
    if _current is not None:
        _paths[_current] = list(sys.path)
    local = {n: m for n, m in list(sys.modules.items()) if _folder_local(n, m)}
    if _current is not None:
        _modules[_current] = local
    for name in local:
        del sys.modules[name]
    sys.modules.update(_modules.get(folder, {}))
    sys.path[:] = _paths.get(folder, [str(folder), *_base_path])
    _current = folder


def pytest_collectstart(collector) -> None:
    if isinstance(collector, pytest.Module):
        _enter(Path(collector.path).resolve().parent)


@pytest.hookimpl(tryfirst=True)
def pytest_runtest_setup(item) -> None:
    _enter(Path(item.path).resolve().parent)
