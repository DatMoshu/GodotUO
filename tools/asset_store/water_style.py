"""The guo/water@1 style file a `water` component names (ADR-0037 decision 6).

Data only: a built-in shading model and the numbers it reads. StoreWater.cs
applies the same rules; the corpus (corpus/water.py) holds both to them.
"""
import json

SCHEMA = "guo/water@1"
MODELS = ("uo", "pixel", "cel", "realistic")
MAX_BYTES = 16384
# Every number a style sets: (low, high, integer).
NUMBERS = {
    "speed": (0, 4, False),
    "scale": (0.005, 0.25, False),
    "wave": (0, 1, False),
    "foam": (0, 1, False),
    "foam_width": (0, 0.5, False),
    "glint": (0, 1, False),
    "levels": (0, 16, True),
    "grain": (1, 8, True),
}
RIPPLES = {"strength": (0, 2, False), "damping": (0.9, 0.999, False)}
FIELDS = {"schema", "name", "model", "tint", "ripples", *NUMBERS}


def _require(ok, message):
    if not ok:
        raise ValueError(message)


def _unique(pairs):
    out = {}
    for key, value in pairs:
        _require(key not in out, "duplicate JSON key")
        out[key] = value
    return out


def _constant(name):
    raise ValueError(f"{name} is not JSON")


def _utf8(value):
    try:
        value.encode("utf-8")
        return True
    except UnicodeEncodeError:  # a lone surrogate, which C# cannot read as a string either
        return False


def _number(value, low, high, integer, what):
    ok = type(value) is int if integer else type(value) in (int, float)
    _require(ok and low <= value <= high, f"water {what} out of range")


def validate_water(raw):
    """Parse and check one style file; returns the dict. Raises ValueError."""
    _require(len(raw) <= MAX_BYTES, "water style too large")
    _require(not raw.startswith(b"\xef\xbb\xbf"), "water style has a byte order mark")
    try:
        style = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique, parse_constant=_constant)
    except UnicodeDecodeError:
        raise ValueError("water style is not UTF-8") from None
    _require(isinstance(style, dict), "water style must be an object")
    _require(set(style) == FIELDS, "water style fields")
    _require(style["schema"] == SCHEMA, "unsupported water schema")
    name = style["name"]
    _require(isinstance(name, str) and name.strip() and _utf8(name) and len(name.encode("utf-16-le", "surrogatepass")) // 2 <= 48
             and not any(ord(c) < 32 or ord(c) == 127 for c in name), "invalid water name")
    _require(style["model"] in MODELS, "unknown water model")
    tint = style["tint"]
    _require(isinstance(tint, list) and len(tint) == 3, "water tint is three numbers")
    for value in tint:
        _number(value, 0, 1, False, "tint")
    for key, (low, high, integer) in NUMBERS.items():
        _number(style[key], low, high, integer, key)
    ripples = style["ripples"]
    _require(isinstance(ripples, dict) and set(ripples) == set(RIPPLES), "water ripples fields")
    for key, (low, high, integer) in RIPPLES.items():
        _number(ripples[key], low, high, integer, "ripples " + key)
    return style
