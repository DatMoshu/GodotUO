# Server compatibility

GUO is meant to work with any Ultima Online server, not only the ModernUO shard it is developed against. The
server lab runs the same scripted cases against each server and records what happened. This page is generated
by `python tools/server_lab/run.py wiki` from the lab's latest results; do not edit it by hand.

Last updated: 2026-10-09. GUO client version: 7.0.107.76.

## Servers and versions

| Server | Version tested | Era |
|---|---|---|
| ModernUO | core `d4531cd94` (2026-09-30) | Endless Journey (Id 11), tools/modernuo/config/expansion.json |
| ServUO | core `d76bf4443` (2026-08-04) | EJ |
| UOX3 | core `a8a4aefeb` (2025-07-30) | lbr (upstream default; later-era cases may be n/a) |
| Sphere X | core `dd28a0ad5` (2026-09-18), scripts `27e78bc89` (2026-03-27) | upstream default; AOS-era cases such as tooltips may be n/a |

## Results

PASS: the case ran to the end. FAIL (GUO): a GUO bug, filed as a story. FAIL (server gap): the server does
not do this the way the client expects; a local patch is kept for it. FAIL (untriaged): failed, not yet looked
at. n/a: the server does not have the feature (often its era). not run: the case has no scenario yet, or this
server has not been set up in the lab yet.

| # | Case | ModernUO | ServUO | UOX3 | Sphere X |
|---|---|---|---|---|---|
| 0 | Start the client and reach the login screen | PASS | not run | not run | not run |
| 1 | Log in, server list, character list (and log out) | PASS | not run | not run | not run |
| 2 | Create a character on a new account | not run | not run | not run | not run |
| 3 | Delete a character | not run | not run | not run | not run |
| 4 | Log out and back in | not run | not run | not run | not run |
| 5 | Walk, run and pathfind | not run | not run | not run | not run |
| 6 | Map switch (moongate or staff teleport) | not run | not run | not run | not run |
| 7 | Light, weather, day and night | not run | not run | not run | not run |
| 8 | Speech, journal, emotes | not run | not run | not run | not run |
| 9 | Paperdoll and backpack | PASS | not run | not run | not run |
| 10 | Drag and drop (ground, container, equip) | not run | not run | not run | not run |
| 11 | Containers, stacking and splitting | not run | not run | not run | not run |
| 12 | Item property tooltips | not run | not run | not run | not run |
| 13 | Vendor buy and sell | not run | not run | not run | not run |
| 14 | Bank box | not run | not run | not run | not run |
| 15 | War mode and melee against a spawned creature | not run | not run | not run | not run |
| 16 | Cast a spell (target cursor, mana) | not run | not run | not run | not run |
| 17 | Use a skill (hiding, healing) | not run | not run | not run | not run |
| 18 | Death, ghost and resurrection | not run | not run | not run | not run |
| 19 | Party invite and accept | not run | not run | not run | not run |
| 20 | Chat or guild message | not run | not run | not run | not run |
| 21 | House placement (preview, place, open the door) | not run | not run | not run | not run |
| 22 | A boat or multi on the map (drawn, walked on) | not run | not run | not run | not run |
| 23 | Macros and hotkeys against the server | not run | not run | not run | not run |
| 24 | Plugins loaded (plugin_probe) | not run | not run | not run | not run |
| 25 | The staff commands the lab uses (add item, add creature, teleport, set skill) | not run | not run | not run | not run |

## How to run it

`launchers\dev\server_lab.bat row modernuo` sets the server up (fetch at its pinned version, build,
configure on this computer only, with a generated admin account), starts it, runs every case that has a
scenario, stops it and rewrites this page. Details: `tools/server_lab/README.md` and
[the pins table](../server_lab.md).
