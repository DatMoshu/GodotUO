"""Cases for the `water` component and its guo/water@1 style file (ADR-0037, WATER-04)."""
import base64
import json
from copy import deepcopy

STYLE = {
    "schema": "guo/water@1", "name": "Corpus water", "model": "pixel", "tint": [0.1, 0.2, 0.4],
    "speed": 1, "scale": 0.045, "wave": 0.2, "foam": 0.8, "foam_width": 0.22, "glint": 0.3,
    "levels": 4, "grain": 2, "ripples": {"strength": 1, "damping": 0.985},
}
COMPONENT = {"id": "sea", "type": "water", "target": "client", "entry": "water.json"}


def water_cases():
    cases = []

    def add(name, accepted, why, component=None, style=None, raw=None, zipped=True, payload=None):
        text = raw if raw is not None else json.dumps(style if style is not None else STYLE)
        case = dict(name="water-" + name, expect="accept" if accepted else "reject", why=why,
                    manifest=dict(schema="guo/store-pack@2", kind="content", target="client",
                                  dependencies={}, components=[deepcopy(component or COMPONENT)]),
                    payload=payload or {"still.png": "png", "water.json": text})
        if zipped:
            case["zip"] = {}
        cases.append(case)

    def changed(**fields):
        style = deepcopy(STYLE)
        for key, value in fields.items():
            if value is None:
                style.pop(key)
            else:
                style[key] = value
        return style

    for model in ("uo", "pixel", "cel", "realistic"):
        add("model-" + model, True, f"a {model} style installs", style=changed(model=model))
    add("manifest", True, "the manifest alone is valid", zipped=False)
    add("integer-numbers", True, "whole numbers are numbers", style=changed(speed=2, wave=1, tint=[0, 1, 0]))
    add("edges", True, "every range is inclusive",
        style=changed(speed=4, scale=0.005, foam_width=0.5, levels=16, grain=8, ripples={"strength": 2, "damping": 0.999}))
    add("target-shared", False, "a style is client only", component=dict(COMPONENT, target="shared"), zipped=False)
    add("entry-png", False, "a style is a JSON file", component=dict(COMPONENT, entry="still.png"), zipped=False)
    add("foreign-field", False, "no media metadata on a water component", component=dict(COMPONENT, graphic=12), zipped=False)
    add("shader", False, "no shader code, in any kind but postfx",
        payload={"still.png": "png", "water.json": json.dumps(STYLE), "water.gdshader": "shader_type canvas_item;"})
    for label, style, why in [
        ("schema", changed(schema="guo/water@2"), "an unknown schema"),
        ("model-unknown", changed(model="ocean"), "only the built-in models"),
        ("model-shader", changed(model="shader"), "no pack-supplied shader model in v1"),
        ("extra-field", dict(STYLE, texture="foam.png"), "textures are not in v1"),
        ("missing-field", changed(glint=None), "every number is set"),
        ("tint-two", changed(tint=[0.1, 0.2]), "tint is three numbers"),
        ("tint-high", changed(tint=[0.1, 0.2, 1.5]), "tint is 0..1"),
        ("tint-string", changed(tint=["0.1", 0.2, 0.4]), "tint is numbers"),
        ("speed-high", changed(speed=4.5), "speed is 0..4"),
        ("speed-string", changed(speed="1"), "speed is a number"),
        ("speed-bool", changed(speed=True), "true is not a number"),
        ("scale-zero", changed(scale=0), "scale is at least 0.005"),
        ("levels-fraction", changed(levels=2.5), "levels is whole"),
        ("levels-float", changed(levels=2.0), "levels is an integer, not 2.0"),
        ("levels-high", changed(levels=17), "levels is 0..16"),
        ("grain-zero", changed(grain=0), "grain is 1..8"),
        ("name-empty", changed(name=" "), "a name has text"),
        ("name-long", changed(name="x" * 49), "a name is at most 48 units"),
        ("name-control", changed(name="Sea\nwater"), "no control characters"),
        ("name-number", changed(name=7), "a name is a string"),
        ("ripples-extra", changed(ripples={"strength": 1, "damping": 0.985, "wake": 3}), "only strength and damping"),
        ("ripples-damping", changed(ripples={"strength": 1, "damping": 1.0}), "damping below 1 or it never calms"),
        ("not-object", ["guo/water@1"], "a style is an object"),
    ]:
        add(label, False, why, style=style)
    good = json.dumps(STYLE)
    add("name-long-emoji", True, "48 UTF-16 units: 24 emoji", style=changed(name="\U0001F30A" * 24))
    add("name-emoji-over", False, "49 UTF-16 units", style=changed(name="a" + "\U0001F30A" * 24))
    add("duplicate-key", False, "a duplicate key", raw=good[:-1] + ', "speed": 2}')
    add("nan", False, "NaN is not JSON", raw=good.replace('"speed": 1', '"speed": NaN'))
    add("huge", False, "1e400 is no speed", raw=good.replace('"speed": 1', '"speed": 1e400'))
    add("levels-exponent", False, "2e0 is not an integer", raw=good.replace('"levels": 4', '"levels": 2e0'))
    add("bom", False, "no byte order mark", raw="﻿" + good)
    add("lone-surrogate", False, "a lone surrogate is not text", raw=good.replace("Corpus water", "Corpus \\ud800"))
    add("too-large", False, "at most 16 KiB", raw=good[:-1] + ', "pad": "' + "x" * 16384 + '"}')
    add("truncated", False, "malformed JSON", raw=good[:-2])
    cases.append(dict(name="water-not-utf8", expect="reject", why="the style is UTF-8",
                      manifest=dict(schema="guo/store-pack@2", kind="content", target="client",
                                    dependencies={}, components=[deepcopy(COMPONENT)]),
                      payload={"still.png": "png",
                               "water.json": {"b64": base64.b64encode(good.replace("Corpus", "Corp\xe9").encode("latin-1")).decode()}},
                      zip={}))
    return cases
