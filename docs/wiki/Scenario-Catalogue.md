# Scenario catalogue

Audit baseline: `main` `fc9e3588`, 2026-10-28.
These are source-discovered entry points, not new execution receipts.
Read the linked README/source before running a tool; dependencies, approvals,
output paths and destructive verbs differ. Generated output belongs outside
the checkout. A shell twin may explain a Windows-only limitation.

Presence of an expectation file is **not** a PASS. Open the scenario and its
area README for dependencies, required account permissions, driver mode and
what its assertions actually observe. Server mutation and native-stage cases
need an isolated target and explicit review. Run commands through
[Scenario Runner](../../tools/scenario_run/README.md).

| Scenario | Title / surface | Definition |
|---|---|---|
| `client.ambient.particles` | UO+ particles: defaults off, persistence, biomes, density, bounded work and motion / `client` | [JSON](../../tools/scenarios/client/ambient_particles.scenario.json) |
| `client.audio.packs` | Verified audio packs: default off, region layers and recorded NPC clips / `client` | [JSON](../../tools/scenarios/client/audio_packs.scenario.json) |
| `client.gumps.paperdoll_backpack` | Log in on the dev shard and open the paperdoll and the backpack / `client` | [JSON](../../tools/scenarios/client/gumps_paperdoll_backpack.scenario.json) |
| `client.hd.packs` | Verified HD packs: default off, native footprints and filter choices / `client` | [JSON](../../tools/scenarios/client/hd_packs.scenario.json) |
| `client.input.gamepad` | Controller: Set controls rebinds one job, keeps, leaves unsaved and resets to defaults / `client` | [JSON](../../tools/scenarios/client/input_gamepad.scenario.json) |
| `client.input.keyboard_mouse` | Keyboard: modifiers come up on focus loss, AltGr characters are typed / `client` | [JSON](../../tools/scenarios/client/input_keyboard_mouse.scenario.json) |
| `client.input.touch` | Touch: a swipe or a two-finger pan scrolls the list under the finger / `client` | [JSON](../../tools/scenarios/client/input_touch.scenario.json) |
| `client.ui.inventory_touch` | Touch inventory: equipment drag, grid context and pinch / `client` | [JSON](../../tools/scenarios/client/inventory_touch.scenario.json) |
| `client.login.basic` | Log in on the dev shard, walk the server and character lists, go to the Britain bank and log out / `client` | [JSON](../../tools/scenarios/client/login_basic.scenario.json) |
| `client.smoke.login_screen` | Start the client and reach the login screen / `client` | [JSON](../../tools/scenarios/client/login_screen.scenario.json) |
| `client.login.server_identity` | Log in and check the client records the server and the client version it reported (NET-02) / `client` | [JSON](../../tools/scenarios/client/login_server_identity.scenario.json) |
| `client.login.version_refused` | Report an older client version than the server takes and check the disconnect says so in plain words (NET-02) / `client` | [JSON](../../tools/scenarios/client/login_version_refused.scenario.json) |
| `client.mods.probe` | Packet mods: off by default, the sample mod through the real packet seams / `client` | [JSON](../../tools/scenarios/client/mods_probe.scenario.json) |
| `client.ui.options_touch` | Options: classic mouse spacing, touch rows, scrolling and Modern settings / `client` | [JSON](../../tools/scenarios/client/options_touch.scenario.json) |
| `client.perf.gate` | Performance gate input: probe all six scenes on the GM dev shard / `client` | [JSON](../../tools/scenarios/client/perf_gate.scenario.json) |
| `client.perf_lab.scenes` | Visit the places the perf lab times and photograph each / `client` | [JSON](../../tools/scenarios/client/perf_lab_scenes.scenario.json) |
| `client.realtime.lights` | Realtime lights: default off, ambient ramp, walls block, reach cache, roof dim / `client` | [JSON](../../tools/scenarios/client/realtime_lights.scenario.json) |
| `client.render.water` | Realtime water: same draw list as ClassicUO, animated water and ripples at the docks and the bay / `client` | [JSON](../../tools/scenarios/client/render_water.scenario.json) |
| `client.render.water_pack` | A Store water pack's style at the docks, the bay and around a wading player / `client` | [JSON](../../tools/scenarios/client/render_water_pack.scenario.json) |
| `client.selection.seam` | Selection seam: replacement picks forward to the original object / `client` | [JSON](../../tools/scenarios/client/selection_seam.scenario.json) |
| `client.spell.lighting` | UO+ spell lighting: cast and impact tiles, heal, expiry and shared budget / `client` | [JSON](../../tools/scenarios/client/spell_lighting.scenario.json) |
| `client.store.hosted_catalogue` | A hosted catalogue: approve its key, list signed packs from two hosts, install one / `client` | [JSON](../../tools/scenarios/client/store_hosted_catalogue.scenario.json) |
| `client.uoplus.options` | UO+ shared settings: default off, staged edits, persistence and controller scrolling / `client` | [JSON](../../tools/scenarios/client/uoplus_options.scenario.json) |
| `editor.admin.offline` | Inspect every Admin section without desktop input or a shard / `editor` | [JSON](../../tools/scenarios/editor/admin.offline.scenario.json) |
| `editor.anim.roundtrip` | Animations: send a creature to Pixelorama and back / `editor` | [JSON](../../tools/scenarios/editor/anim_roundtrip.scenario.json) |
| `editor.header.review` | Review server/client icon controls through MCP without desktop input / `editor` | [JSON](../../tools/scenarios/editor/header_review.scenario.json) |
| `editor.mcp.access` | MCP refuses a default-off shard mutation and permits listed navigation / `editor` | [JSON](../../tools/scenarios/editor/mcp_access.scenario.json) |
| `editor.smoke.layout` | Open the editor, check the layout and the Art tab / `editor` | [JSON](../../tools/scenarios/editor/smoke_layout.scenario.json) |
| `editor.tabs.sweep` | Sweep the editor tabs: layout, assets, store, maps, world, shard / `editor` | [JSON](../../tools/scenarios/editor/tabs_sweep.scenario.json) |
| `editor.water.authoring` | Author water in the World workspace, compare Classic and export a local pack / `editor` | [JSON](../../tools/scenarios/editor/water_authoring.scenario.json) |
| `shard.admin_add_account` | As the owner, open the Admin gump and add an account (the shard has auto account creation off) / `client` | [JSON](../../tools/scenarios/shard/admin_add_account.scenario.json) |
| `shard.console` | Log in as the shard's owner account and run an owner chat command / `client` | [JSON](../../tools/scenarios/shard/console.scenario.json) |
| `world.britain_parity` | Compare what GUO draws at the Britain bank with ClassicUO / `client` | [JSON](../../tools/scenarios/world/britain_parity.scenario.json) |
| `world.dungeon_parity` | Compare what GUO draws inside Despise with ClassicUO / `client` | [JSON](../../tools/scenarios/world/dungeon_parity.scenario.json) |
