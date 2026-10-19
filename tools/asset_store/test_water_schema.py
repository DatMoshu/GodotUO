"""W-01 authoring schema boundaries; installation waits for W-02's reader."""
from copy import deepcopy
import json
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from asset_store.water_style import validate_water  # noqa: E402

HERE = Path(__file__).resolve().parent / "schema"
SCHEMA = json.loads((HERE / "water_v2.schema.json").read_text(encoding="utf-8"))
DEFAULTS = json.loads((HERE / "water_v2.defaults.json").read_text(encoding="utf-8"))


def nodes(node=SCHEMA, path=()):
    yield path, node
    for key, child in node.get("properties", {}).items():
        yield from nodes(child, (*path, key))


def replace(path, value):
    doc = deepcopy(DEFAULTS)
    parent = doc
    for key in path[:-1]:
        parent = parent[key]
    parent[path[-1]] = value
    return doc


@pytest.fixture(scope="module")
def validator():
    # Already supplied by the repo's pooled pytest CI, like the scenario schema tests.
    jsonschema = pytest.importorskip("jsonschema")
    jsonschema.Draft202012Validator.check_schema(SCHEMA)
    return jsonschema.Draft202012Validator(SCHEMA)


def test_defaults_are_complete_valid_and_match_annotations(validator):
    validator.validate(DEFAULTS)
    for path, node in nodes():
        value = DEFAULTS
        for key in path:
            value = value[key]
        if node.get("type") == "object":
            assert node["additionalProperties"] is False
            assert set(node["required"]) == set(node["properties"]) == set(value)
        else:
            assert value == node["default"], path


@pytest.mark.parametrize("path,node", [(p, n) for p, n in nodes() if n.get("type") in ("number", "integer")],
                         ids=lambda x: ".".join(x) if isinstance(x, tuple) else None)
def test_numeric_boundaries_and_wrong_types(validator, path, node):
    for bound in (node["minimum"], node["maximum"]):
        validator.validate(replace(path, bound))
    for bad in (node["minimum"] - 1, node["maximum"] + 1, True, None, "0"):
        assert not validator.is_valid(replace(path, bad)), (path, bad)
    if node["type"] == "integer":
        assert not validator.is_valid(replace(path, 1.5))
        validator.validate(replace(path, 2.0))  # v2 uses JSON Schema integer semantics


@pytest.mark.parametrize("path", [p for p, n in nodes() if n.get("type") == "object"])
def test_missing_unknown_and_wrong_group_shapes(validator, path):
    for bad in (None, [], "water", {}):
        doc = replace(path, bad) if path else bad
        assert not validator.is_valid(doc)
    for removed in (None, "unknown"):
        doc = deepcopy(DEFAULTS)
        group = doc
        for key in path:
            group = group[key]
        if removed:
            group[removed] = 1
        else:
            del group[next(iter(group))]
        assert not validator.is_valid(doc)


@pytest.mark.parametrize("path,size,low,high", [
    (("surface", "base_color"), 3, 0, 1), (("depth", "tint"), 3, 0, 1),
    (("flow", "direction"), 2, -1, 1),
])
def test_vector_boundaries_and_shapes(validator, path, size, low, high):
    for values in ([low] * size, [high] * size, [0] * size):
        validator.validate(replace(path, values))
    for bad in ([0] * (size - 1), [0] * (size + 1), [low - 1] * size,
                [high + 1] * size, [True] * size, ["0"] * size, "0,0,0"):
        assert not validator.is_valid(replace(path, bad))


def test_names_models_clock_and_version(validator):
    for model in ("uo", "pixel", "cel", "realistic", "classicplus"):
        validator.validate(replace(("model",), model))
    for name in ("Custom water", "\U0001f30a" * 48):
        validator.validate(replace(("name",), name))
    for path, values in [
        (("name",), ["", " \t", "\u2003", "\nsea", "sea\n", "sea\x7f", "\ud800", "x" * 49, 1]),
        (("model",), ["classic", "shader", None]),
        (("schema",), ["guo/water@1", "guo/water@3", None]),
        (("clock", "source"), ["shard", "wall", None]),
    ]:
        for bad in values:
            assert not validator.is_valid(replace(path, bad)), (path, bad)
    validator.validate(replace(("clock", "source"), "fixed"))


def test_shipping_reader_refuses_v2_until_renderer_supports_it():
    with pytest.raises(ValueError):
        validate_water((HERE / "water_v2.defaults.json").read_bytes())
