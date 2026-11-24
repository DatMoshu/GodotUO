"""Finding a control in a `guo_ui` snapshot, for the ui.* step kinds and the ui.* expectations.

A selector is a string (matches a control whose type, name or text equals it) or an object with any of
`type`, `name`, `text`, `contains` (substring of the text), `system` (classic | godot), `gump` (top-level gump type), `button_id` (a classic button's ID), `serial` (the game object
a classic control shows, such as an item: a number, decimal text or 0x hex) and `index`
(which of several matches, 0 based). Gump order follows the front-most gump, so prefer gump + button_id to a bare index.
"""

from __future__ import annotations


class SelectorError(ValueError):
    pass


_KEYS = {"type", "name", "text", "contains", "system", "gump", "button_id", "serial", "index"}


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
    if "gump" in want and control.get("gump") != want["gump"]:
        return False
    if "button_id" in want and control.get("buttonId") != want["button_id"]:
        return False
    if "serial" in want and (control.get("serial") is None or control.get("serial") != parse_serial(want["serial"])):
        return False
    return True


def parse_serial(value) -> int:
    """A serial written as a number, decimal text (a $variable) or 0x hex."""
    if isinstance(value, int):
        return value
    text = str(value).strip()
    return int(text, 16) if text.lower().startswith("0x") else int(text)


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
