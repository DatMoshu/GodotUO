"""Loading and checking a scenario file (tools/scenarios/<area>/<name>.scenario.json).

The shape is documented in docs/data_formats.md ("Scenario runs"). This is the runner's own structural check, so a
bad file fails before anything launches; the JSON schema under tools/scenarios/schema/ is the contract for editors.
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path

SURFACES = {"client", "editor", "web", "deck", "shard"}
# Every kind the format names; the runner implements the subset in IMPLEMENTED and fails a step of any other kind.
KINDS = {"launch", "wait", "shot", "note", "tour_segment", "editor_invoke", "ui.click", "ui.fill", "ui.key",
         "chat", "scene_set", "renderdump", "render_diff", "lane"}
IMPLEMENTED = {"launch", "wait", "shot", "note", "tour_segment", "editor_invoke", "ui.click", "ui.fill", "ui.key", "chat",
               "renderdump", "render_diff"}
DEFAULT_TIMEOUTS = {"step_s": 30, "run_s": 600}


class ScenarioError(ValueError):
    pass


class Scenario:
    def __init__(self, data: dict, path: Path | None = None):
        self.data = data
        self.path = path
        self.id: str = data["id"]
        self.title: str = data.get("title", self.id)
        self.surface: str = data["surface"]
        self.requires: dict = data.get("requires", {})
        self.timeouts: dict = {**DEFAULT_TIMEOUTS, **data.get("timeouts", {})}
        self.steps: list[dict] = data["steps"]


def validate(data: object) -> list[str]:
    """Returns the problems found, empty when the scenario is well formed."""
    problems: list[str] = []
    if not isinstance(data, dict):
        return ["the scenario must be a JSON object"]
    for key in ("id", "surface", "steps"):
        if key not in data:
            problems.append(f"missing '{key}'")
    if problems:
        return problems
    if not isinstance(data["id"], str) or not re.fullmatch(r"[a-z0-9_]+(\.[a-z0-9_]+)+", data["id"]):
        problems.append("id must be dotted lower-case words, e.g. editor.tabs.sweep")
    if data["surface"] not in SURFACES:
        problems.append(f"surface must be one of {sorted(SURFACES)}")
    steps = data["steps"]
    if not isinstance(steps, list) or not steps:
        return problems + ["steps must be a non-empty list"]
    seen: set[str] = set()
    for i, step in enumerate(steps):
        where = f"steps[{i}]"
        if not isinstance(step, dict):
            problems.append(f"{where} must be an object")
            continue
        sid = step.get("id")
        if not isinstance(sid, str) or not sid:
            problems.append(f"{where} needs an id")
        elif sid in seen:
            problems.append(f"{where}: duplicate step id {sid}")
        else:
            seen.add(sid)
        do = step.get("do")
        if not isinstance(do, dict) or do.get("kind") not in KINDS:
            problems.append(f"{where}: do.kind must be one of {sorted(KINDS)}")
        if "expect" in step and not isinstance(step["expect"], dict):
            problems.append(f"{where}: expect must be an object")
        problems += _unknown_fields(where, step)
        if step.get("on_fail", "abort") not in ("abort", "continue"):
            problems.append(f"{where}: on_fail must be abort or continue")
    shape = _shape()
    problems += [f"unknown field '{k}'" for k in data if k not in shape["top"]]
    timeouts = data.get("timeouts", {})
    for key in ("step_s", "run_s"):
        value = timeouts.get(key, 1)
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0:
            problems.append(f"timeouts.{key} must be a positive number")
    return problems


SCHEMA_FILE = Path(__file__).resolve().parents[1] / "scenarios" / "schema" / "scenario.schema.json"
_SHAPE: dict | None = None


def _shape() -> dict:
    """The field names the JSON schema allows, read from the schema itself so the two cannot drift (stdlib only)."""
    global _SHAPE
    if _SHAPE is None:
        schema = json.loads(SCHEMA_FILE.read_text(encoding="utf-8"))
        defs = schema["$defs"]
        do_args = {d["if"]["properties"]["kind"]["const"]: (set(d["then"]["properties"]), set(d["then"]["required"]))
                   for d in defs["do"]["allOf"]}
        _SHAPE = {"top": set(schema["properties"]), "step": set(defs["step"]["properties"]),
                  "expect": set(defs["expect"]["properties"]), "do": do_args}
    return _SHAPE


def _unknown_fields(where: str, step: dict) -> list[str]:
    """A misspelt step field, kind argument or expectation is refused, never silently ignored."""
    shape = _shape()
    problems = [f"{where}: unknown field '{k}'" for k in step if k not in shape["step"]]
    do = step.get("do")
    if isinstance(do, dict) and do.get("kind") in shape["do"]:
        allowed, required = shape["do"][do["kind"]]
        problems += [f"{where}: {do['kind']} takes no '{k}'" for k in do if k not in allowed]
        problems += [f"{where}: {do['kind']} needs '{k}'" for k in sorted(required) if k not in do]
    expect = step.get("expect")
    if isinstance(expect, dict):
        problems += [f"{where}: unknown expectation '{k}'" for k in expect if k not in shape["expect"]]
    return problems


def load(path: Path) -> Scenario:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        raise ScenarioError(f"{path.name}: cannot read: {ex}") from ex
    problems = validate(data)
    if problems:
        raise ScenarioError(f"{path.name}: " + "; ".join(problems))
    return Scenario(data, path)


def find(root: Path, name: str) -> Path:
    """A scenario by file path or by id (tools/scenarios/**/<name>.scenario.json)."""
    direct = Path(name)
    if direct.suffix == ".json" and direct.is_file():
        return direct
    for candidate in sorted((root / "tools" / "scenarios").rglob("*.scenario.json")):
        try:
            if json.loads(candidate.read_text(encoding="utf-8")).get("id") == name:
                return candidate
        except (OSError, ValueError):
            continue
    raise ScenarioError(f"no scenario with id or path {name}")


_VAR = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?")


def substitute(value, variables: dict[str, str]):
    """Replaces $name / ${name} in every string of a step. A name nobody defined is an error, never ''.

    Names come from --var, then from GUO_SCENARIO_<NAME> in the environment (credentials live there, never in a file)."""
    if isinstance(value, str):
        def repl(m: re.Match[str]) -> str:
            name = m.group(1)
            if name in variables:
                return variables[name]
            env = os.environ.get("GUO_SCENARIO_" + name.upper())
            if env is not None:
                return env
            hint = "" if "password" in name.lower() else f" or pass --var {name}=..."  # a password is environment only
            raise ScenarioError(f"scenario variable ${name} is not defined (set GUO_SCENARIO_{name.upper()}{hint})")
        return _VAR.sub(repl, value)
    if isinstance(value, list):
        return [substitute(v, variables) for v in value]
    if isinstance(value, dict):
        return {k: substitute(v, variables) for k, v in value.items()}
    return value


def subset(expected, actual) -> bool:
    """True when every key of `expected` is in `actual` with an equal value (recursively); other values compare equal."""
    if isinstance(expected, dict):
        return isinstance(actual, dict) and all(k in actual and subset(v, actual[k]) for k, v in expected.items())
    return expected == actual
