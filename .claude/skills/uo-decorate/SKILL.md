---
name: uo-decorate
description: "Furnish the interior of a generated UO multi (house) from what UO's own buildings hold: mine the furnished interiors of the facet statics, the shard's decoration and the client multis into a SQLite database, segment rooms, learn furniture groups and room types, then produce a seeded, deterministic `decor` list for a built multi that never blocks a door, stair, landing, arrival or window. Renders before/after previews offline. Use for 'decorate/furnish this house', after `/uo-multi` builds a house, and before claiming a furnished interior looks right."
argument-hint: "<built multi name | description.json> [--seed N] [--density F]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# UO Decorate

Runs `tools/decorate` and reports what actually happened. The house side of the
contract is `docs/data_formats.md` section 16 (`decor[]`, the built multi's
sidecar `local.storeys` and `local.stairs`). Offline only: no Godot, no shard.

**Never write under `UO_CLIENT_DATA`.** The database and the previews are
derived from client data: they stay in `build/decorate/`, never in a commit.
The database holds item ids, counts, tiledata names and flags, and the layouts
derived from them: no art and no copied map or statics records.

---

## 0. Before anything

```
python tools\decorate\test_decorate.py
```

It must end `6/6 passed` (exit 0). A `FAIL` stops the flow: report it verbatim.

The multi catalogue must exist (`build\multi\catalogue\families.json`); if not,
run `python tools\multi\run.py mine`.

## 1. The database

If `build\decorate\decor.sqlite` is missing or older than the install or the
shard's decoration:

```
python tools\decorate\run.py mine
```

About 10 s. It prints the table counts and the room types found. Report them.
It uses the shard's `Data/Decoration` when it finds one (`--decoration DIR`,
`GUO_SHARD_DECORATION`, this checkout's or the main checkout's ModernUO
source); say whether it did, since without it the town interiors are much
barer. `python tools\decorate\run.py stats` summarises an existing database.

## 2. Decorate

For a house already built by `/uo-multi`:

```
python tools\decorate\run.py decorate <name> --seed N --out build\decorate\<name>_decor.json
python tools\decorate\run.py decorate <name> --seed N --desc tools\multi\examples\<name>.json --out build\decorate\<name>.json
```

The second form writes the whole description with the decor merged in (the
description's own outdoor decor is kept), ready for `tools\multi\run.py build`.
Exit 1 and a `PROBLEM` line mean an item stands on a door, stair, landing,
arrival or window, or cuts a storey apart: report it, do not hand-edit.

## 3. Look at it

```
python tools\decorate\run.py preview <description.json> --seed N
python tools\decorate\run.py demo --seed N          t_manor, l_townhouse, courtyard_house
```

Writes to `build\decorate\previews\`: per storey `<name>_s<n>_before.png`,
`_after.png` (the storeys above cut away) and `_plan.png` (walls grey, windows
blue, doors orange, stairs magenta, arrivals pink, furniture green), plus
`<name>_report.txt` (the room types and what went in each) and
`<name>_decorated.json`. The preview also runs the house through the multi
validator with the decor in.

Open the `_after.png` of every storey and say what you actually looked at:
which room got which type, whether beds, counters, shelves and bookcases stand
against walls facing in, and anything floating, half-placed or odd.

## 4. Report

- The seed and the room types per room (from the report).
- Item counts before/after, and `check and validate: clean` or the problems.
- The preview paths, as `file:///` links.
- What looked wrong. A repeatable oddity is a rule for `classify.py` or
  `decorate.py`, not a hand edit to the decor list.
