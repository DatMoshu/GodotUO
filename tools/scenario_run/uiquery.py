"""Finding a control in a `guo_ui` snapshot, for the ui.* step kinds and the ui.* expectations.

A selector is a string (matches a control whose type, name or text equals it) or an object with any of
`type`, `name`, `text`, `contains` (substring of the text), `system` (classic | godot) and `index` (which of
several matches, 0 based). Snapshots list controls parents first, so `index` is stable between calls.
"""

from __future__ import annotations


class SelectorError(ValueError):
    pass


_KEYS = {"type", "name", "text", "contains", "system", "index"}


def normalize(selector) -> dict:
    if isinstance(selector, str) and selector:
        return {"any": selector}
    if isinstance(selector, dict) and selector:
        extra = set(selector) - _KEYS
        if extra:
            raise SelectorError(f"unknown selector key {sorted(extra)[0]} (use {sorted(_KEYS)})")
        return dict(selector)
    raise SelectorError("a control selector is a non-empty string or object")


def _matches(control: dict, want: dict) -> bool:
    text = control.get("text")
    if "any" in want:
        return want["any"] in (control.get("type"), control.get("name"), text)
    if "type" in want and control.get("type") != want["type"]:
        return False
    if "name" in want and control.get("name") != want["name"]:
        return False
    if "text" in want and text != want["text"]:
        return False
    if "contains" in want and want["contains"] not in (text or ""):
        return False
    if "system" in want and control.get("system") != want["system"]:
        return False
    return True


def find_all(snapshot: dict, selector) -> list[dict]:
    want = normalize(selector)
    return [c for c in snapshot.get("controls", []) if _matches(c, want)]


def find(snapshot: dict, selector) -> dict | None:
    want = normalize(selector)
    hits = find_all(snapshot, selector)
    index = int(want.get("index", 0))
    return hits[index] if 0 <= index < len(hits) else None


def center(control: dict) -> tuple[int, int]:
    return (int(control["x"] + control["width"] / 2), int(control["y"] + control["height"] / 2))


def describe(selector) -> str:
    want = normalize(selector)
    return want["any"] if "any" in want else ", ".join(f"{k}={v}" for k, v in want.items())
