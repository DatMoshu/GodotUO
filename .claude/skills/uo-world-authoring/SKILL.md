---
name: uo-world-authoring
description: Independently audit, edit, validate and prepare a complete UO world project for review using canonical GUO tools, without mandatory agent routing.
---

# UO world authoring

Apply the owner's authorization directly. Delegation is optional and must not be required to use these tools. Project-specific content stays in the consuming project's workspace; general tools stay here.

1. Read `docs/data_formats.md` sections 9 and 16, `tools/world/README.md`, and the `uo-multi`, `uo-data`, and `uo-decorate` skills. Inspect `tools/multi/examples/fort_demo.json` and the implementation before assuming a capability exists.
2. Freeze the source project with hashes before editing. Keep references, converted assets, generated masters and actual runtime artifacts separate. Index assets by pack, namespace and key, never preview ID alone.
3. Run `python -m guo.world_preflight --project PROJECT --client-data INSTALL --out REPORT.json` with `PYTHONPATH` pointing at this checkout's `tools`. It refuses absent or mismatched fingerprints and malformed block geometry. Do not bypass a mismatch with `--force` merely to get a preview.
4. Rank families by placements, projected opaque area and reuse. Opaque area is an overlap proxy, not measured visible screen coverage. Inspect matched views to assess actual exposure. Reconcile existing overrides before replacing anything. Include corners, joins, edges, slope textures, openings and facings.
5. Use disposable world overlays and collision-checked preview IDs. Land art IDs and `tiledata.land.texture` IDs are different namespaces. The current world overlay supports static tiledata; it does not imply support for custom land tiledata. Source-ID land replacements must remain scoped to the disposable scene and be declared explicitly. Do not call them newly allocated production IDs.
6. Preserve PNG dimensions, alpha, anchors, tiledata flags, height, hue and placement z, or record intentional changes. Use `guo.uoart` encode/decode round trips. `guo.world_preflight.load_static_overrides` supplies parsed ordered overrides to offline renderers; merge each row over base tiledata, including partial fields. Ignoring custom roof flags can make a roof-off test silently invalid.
7. Assemble roofs and furnished interiors. Keep full and cutaway camera settings identical. Roof-flag filtering, explicit upper-storey cutaways, editor captures and logged-in roof hiding are distinct evidence. Count removed objects and compare image pixels; do not claim roof hiding from a manually cut world.
8. Run `tools/world/test_world.py`, export only to a disposable output through `tools/world/run.py`, then verify every edited block and unchanged remainder. Run relevant multi/data/decorate tests when using those systems. Never modify the retail install or borrow another user's active shard.
9. Use `tools/world_parity` for editor/client evidence and `tools/multi/run.py world-prove` for a private walk only after inspecting their setup, private port, free-memory guard and output paths. Walk entrances, stairs, upper floors, clearance, blockers and exits. If unavailable, document the actual prerequisite or failure; surface graphs are not gameplay proof.
10. Save native assets, sidecars, encoded records, placements, provenance, reports and repeatable commands. External review requires owner authorization. Verify reviewer findings against artifacts and record costs separately from unknown/unreported charges.

## Why this was added

A consuming project used a historical client by default while its saved world matched another install. Its previewer warned but continued. Separately, a PNG-only preview overlay ignored project roof flags. The preflight makes the base dependency explicit and provides one reusable override parser, so authoring does not depend on private adapters or an agent's memory.

Verification: `PYTHONPATH=tools python tools/guo/test_world_preflight.py`; existing `tools/world/test_world.py` and `tools/multi/test_multi.py` remain required regression examples. Limitations: fingerprint is GUO's existing structural identity, not a cryptographic hash of every client file; preflight does not validate reachability, visible art quality, or live behavior.

## Detached exports and runtime evidence

A detached `--out` directory previously made the world exporter infer the wrong project directory, silently omitting static tiledata. `tools/world/run.py` now passes the actual project to asset/tiledata export and verifies declared rows plus byte preservation outside those rows. Run `python tools/world/test_tiledata_output_path.py`; it covers detached output, misleading neighboring project data, missing output and unrelated-byte tampering. Use normal `export PROJECT --out OUTPUT` and `verify PROJECT --out OUTPUT` commands (inspect CLI help for client-data options). Tiledata-only projects also export. This does not add custom land tiledata support or allocate production IDs.

Inspect the executable's build as well as its source before relying on command-line automation: an older DLL can reject flags already present in source. Keep that failed evidence. Raw editor overlays may not apply texmap replacements; validate encoded texmaps in an isolated logged-in client before claiming terrain parity. Record each walking target and actual position, failure, tolerance and process cleanup. A successful building tour does not establish reachability of every district lane.

Existing example check: `tools/multi/run.py build tools/multi/examples/cottage.json --catalogue CATALOGUE --out DISPOSABLE_OUTPUT` produces 189 components and one door and validates. The current `fort_demo.json` scene build reports two walking legs over 18 cells; retain that diagnostic rather than representing all existing examples as passing. These are example-specific route limitations, not a reason to bypass preflight.

For CDDA resolved layouts, inspect `tools/layout_import/README.md` and its implementation. Extract rooms into SQLite before selecting complete sampled layouts; retain source hashes, inferred labels and exact doorway/window masks. The assembler currently supports only complete single-storey z0 samples. Use an external project theme and the canonical stage writer, then distinguish native encoding, offline previews, narrow editor inspection and logged-in walking evidence. Stage inspection requires `UO_EDITOR_DATA_STAGE`; 0x3Fxx multis are supported in the editor. Never claim all authored CDDA variants were converted from a sampled bake.

Inspect baked contact shadows against preserved raw sprites before adding them across a family. Native static art has binary alpha: a broad blurred ellipse becomes opaque stippled black pixels, not translucent shade. Prefer preserved source lighting where it already reads correctly. Reconvert raw sources in an isolated stage and record canvas changes; removing an even amount of symmetric horizontal padding preserves the native bottom-center anchor when height remains identical. Do not confuse a successful unchanged collision tour with visual proof of a revised art stage.
