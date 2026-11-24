"""Writes the gate's equip, combat and reconnect scenarios from the kept link's login steps, so the shared login
stays one copy. Run after changing the kept scenario's login steps:

    python tools/newplayer_gate/make_scenarios.py
"""

from __future__ import annotations

import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLIENT = ROOT / "tools" / "scenarios" / "client"
LOGIN_UNTIL = "in_world"          # the kept link's steps up to and including this one are a returning player's login


def login_steps() -> list[dict]:
    kept = json.loads((CLIENT / "newplayer_kept.scenario.json").read_text(encoding="utf-8"))
    steps = []
    for step in kept["steps"]:
        steps.append(copy.deepcopy(step))
        if step["id"] == LOGIN_UNTIL:
            return steps
    raise SystemExit(f"the kept scenario has no {LOGIN_UNTIL} step")


def relogin_steps(prefix: str) -> list[dict]:
    """The login again after a drop: the same steps without the launch, ids prefixed."""
    steps = []
    for step in login_steps():
        if step["do"]["kind"] == "launch":
            continue
        step = copy.deepcopy(step)
        step["id"] = prefix + step["id"]
        steps.append(step)
    return steps


def log_out(paperdoll_open: bool = False) -> list[dict]:
    steps = []
    if not paperdoll_open:
        steps.append({"id": "open_paperdoll_to_leave", "do": {"kind": "ui.key", "key": "P", "alt": True},
                      "say": "Open the paperdoll.", "expect": {"ui.exists": "PaperDollGump", "within_s": 10}})
    steps += [
        {"id": "log_out", "do": {"kind": "ui.click", "control": {"type": "Button", "system": "classic", "gump": "PaperDollGump", "button_id": 2}},
         "say": "Press Log Out on the paperdoll.", "expect": {"ui.exists": "QuestionGump", "within_s": 10}},
        {"id": "confirm_log_out", "do": {"kind": "ui.click", "control": {"type": "Button", "system": "classic", "gump": "QuestionGump", "button_id": 1}},
         "say": "Confirm.", "expect": {"scene": "LoginScene", "within_s": 30}},
        {"id": "back_at_login", "do": {"kind": "wait", "seconds": 2}, "say": "Back at the login screen.", "shot": True},
    ]
    return steps


def scenario(sid: str, title: str, steps: list[dict], run_s: int = 480) -> dict:
    return {"id": sid, "title": title, "surface": "client",
            "requires": {"shard": "dev", "account": "$account", "build": "debug"},
            "timeouts": {"step_s": 60, "run_s": run_s}, "steps": steps}


def equip() -> dict:
    steps = login_steps() + [
        {"id": "open_paperdoll", "do": {"kind": "ui.key", "key": "P", "alt": True}, "say": "Open the paperdoll.",
         "expect": {"ui.exists": "PaperDollGump", "within_s": 10}},
        {"id": "open_backpack", "do": {"kind": "ui.key", "key": "I", "alt": True}, "say": "Open the backpack.",
         "expect": {"ui.exists": "ContainerGump", "within_s": 10}},
        {"id": "both_open", "do": {"kind": "wait", "seconds": 2}, "say": "The paperdoll and the backpack are open.",
         "shot": True},
        {"id": "move_backpack", "do": {"kind": "ui.drag", "from": {"type": "GumpPicContainer", "gump": "ContainerGump"},
                                       "from_offset": {"x": 0, "y": -75}, "to": {"type": "PaperDollGump"},
                                       "to_offset": {"x": 400, "y": 0}, "steps": 12},
         "say": "The backpack opened over the paperdoll: drag it by its flap to the paperdoll's right."},
        {"id": "unequip", "do": {"kind": "ui.drag", "from": {"type": "ItemGumpFixed", "gump": "PaperDollGump"},
                                 "to": {"type": "GumpPicContainer", "gump": "ContainerGump"}, "to_offset": {"x": 0, "y": 20}, "steps": 12},
         "say": "Drag something the character wears from its slot on the paperdoll into the backpack.",
         "expect": {"world.equipped": {"delta": -1, "lacks": "$dragged"}, "world.backpack": {"has": "$dragged"}, "within_s": 10},
         "shot": True},
        {"id": "equip", "do": {"kind": "ui.drag", "from": {"serial": "$dragged", "gump": "ContainerGump"},
                               "to": {"type": "PaperDollInteractable"}, "to_offset": {"x": 0, "y": -20}, "steps": 12},
         "say": "Drag the same item from the backpack back onto the paperdoll: the character wears it again.",
         "expect": {"world.equipped": {"delta": 1, "has": "$dragged"}, "world.backpack": {"lacks": "$dragged"}, "within_s": 10},
         "shot": True},
    ] + log_out(paperdoll_open=True)
    return scenario("client.newplayer.equip",
                    "A new player drags a worn item from the paperdoll into the backpack and back: unequip and equip", steps)


DUMMY = {"contains": "Attack Dummy"}
WAR_BUTTON = {"type": "Button", "system": "classic", "gump": "PaperDollGump", "button_id": 7}   # PaperDollGump.Buttons.PeaceWarToggle


def combat() -> dict:
    steps = login_steps() + [
        {"id": "creature_near", "do": {"kind": "wait", "seconds": 1},
         "say": "A creature to fight stands near the start (the gate's fixture, placed by a game master before this link).",
         "expect": {"world.mobile": {**DUMMY, "within": 12}, "within_s": 20}},
        {"id": "open_paperdoll", "do": {"kind": "ui.key", "key": "P", "alt": True}, "say": "Open the paperdoll.",
         "expect": {"ui.exists": "PaperDollGump", "within_s": 10}},
        {"id": "war_mode", "do": {"kind": "ui.click", "control": WAR_BUTTON},
         "say": "Press Peace on the paperdoll: it turns to War and the character readies for a fight.",
         "expect": {"player": {"warMode": True}, "within_s": 10}, "shot": True},
        {"id": "attack", "do": {"kind": "world.click", "mobile": DUMMY, "clicks": 2, "within_s": 20},
         "say": "Double-click the creature: at war, that is an attack.",
         "expect": {"combat.attacking": DUMMY, "journal.contains": "GUO_PAD: hit attack-dummy", "within_s": 20},
         "shot": True},
        {"id": "creature_hurt", "do": {"kind": "wait", "seconds": 1}, "say": "The creature is hurt.",
         "expect": {"world.mobile": {**DUMMY, "hurt": True}, "within_s": 20}},
        {"id": "peace_mode", "do": {"kind": "ui.click", "control": WAR_BUTTON}, "say": "Press War: back to peace.",
         "expect": {"player": {"warMode": False}, "within_s": 10}},
    ] + log_out(paperdoll_open=True)
    return scenario("client.newplayer.combat",
                    "A new player readies for a fight and attacks a creature; the shard answers that the blow landed", steps)


def reconnect() -> dict:
    steps = login_steps() + [
        {"id": "drop", "do": {"kind": "net.drop"}, "say": "The connection drops, as when the network goes away.",
         "expect": {"ui.exists": "MessageBoxGump", "within_s": 30}, "shot": True},
        {"id": "acknowledge", "do": {"kind": "ui.click", "control": {"type": "Button", "system": "classic", "gump": "MessageBoxGump", "button_id": 0}},
         "say": "Press Okay on the message that the connection was lost.",
         "expect": {"scene": "LoginScene", "within_s": 30}},
        {"id": "login_again", "do": {"kind": "wait", "seconds": 2}, "say": "Back at the login screen.", "shot": True},
    ] + relogin_steps("again_") + log_out()
    return scenario("client.newplayer.reconnect",
                    "A dropped connection takes the player back to the login screen; logging in again puts the character back in the world",
                    steps, run_s=600)


def fixture() -> dict:
    """The combat link's fixture, run on a game master lane account: its first character goes to the New Haven
    start and types [GuoPadDummies (MUO patch 0004), which clears earlier dummies there and places new ones."""
    steps = [s for s in login_steps() if s["id"] != "character_list"]
    for s in steps:
        if s["id"] == "server_next":
            s["say"] = "Press Next: the game master's characters are listed."
        if s["id"] == "pick_character":
            s["say"] = "Pick the first character."
    steps += [
        {"id": "to_trammel", "do": {"kind": "chat", "text": "[self set map trammel"}, "say": "Go to Trammel."},
        {"id": "to_start", "do": {"kind": "chat", "text": "[go 3503 2574 14"}, "say": "Go to where a new character starts.",
         "expect": {"world.position": {"map": 1, "x": 3503, "y": 2574, "tolerance": 1}, "within_s": 20}},
        {"id": "place_dummies", "do": {"kind": "chat", "text": "[GuoPadDummies"}, "say": "Place the dummies beside the start.",
         "expect": {"journal.contains": "GUO_PAD: dummies spawned", "world.mobile": {**DUMMY, "within": 3}, "within_s": 20},
         "shot": True},
    ] + log_out()
    return scenario("client.newplayer.fixture",
                    "Gate fixture: a game master places a stationary attack dummy beside the new-player start", steps, run_s=420)


def main() -> int:
    for name, build in (("equip", equip), ("combat", combat), ("reconnect", reconnect), ("fixture", fixture)):
        path = CLIENT / f"newplayer_{name}.scenario.json"
        path.write_text(json.dumps(build(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
