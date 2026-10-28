# Launcher reference

Audit baseline: `main` `fc9e3588`, 2026-10-28.
These are source-discovered entry points, not new execution receipts.
Read the linked README/source before running a tool; dependencies, approvals,
output paths and destructive verbs differ. Generated output belongs outside
the checkout. A shell twin may explain a Windows-only limitation.

## android

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [device_manager.bat](../../launchers/android/device_manager.bat) | Opens the Device Test Manager window to pick a device profile, apply it to an emulator or attached device, install and run GUO and collect screenshots; it stays open until you close it. | [.sh](../../launchers/android/device_manager.sh) |
| [doctor.bat](../../launchers/android/doctor.bat) | Lists what an Android export needs on this machine (JDK 17, SDK, templates, keystore, a device) and prints the fix for each missing piece; --release also checks your release key. | [.sh](../../launchers/android/doctor.sh) |
| [dual_probe.bat](../../launchers/android/dual_probe.bat) | Exports, installs and logs in a build on the attached dual-screen device, photographs both displays into build/android and fails if the second screen never comes up. | [.sh](../../launchers/android/dual_probe.sh) |
| [export.bat](../../launchers/android/export.bat) | Exports an APK headless into build/android: GUO-debug.apk, or with --release GUO-release.apk signed with your own key. | [.sh](../../launchers/android/export.sh) |
| [install.bat](../../launchers/android/install.bat) | Installs build/android/GUO-debug.apk onto the attached Android device with adb and reports the result. | [.sh](../../launchers/android/install.sh) |
| [portrait_probe.bat](../../launchers/android/portrait_probe.bat) | Exports and runs the portrait probe on the attached device, photographs each portrait and landscape hold and writes the measurements to build/android/portrait. | [.sh](../../launchers/android/portrait_probe.sh) |
| [run.bat](../../launchers/android/run.bat) | Starts the installed client on the attached device and streams its logcat here until Ctrl+C; the app keeps running afterwards. | [.sh](../../launchers/android/run.sh) |
| [smoke.bat](../../launchers/android/smoke.bat) | Exports, installs and starts GUO on the attached device, waits for the login gump, pulls a screenshot and the log into build/android and fails if the gump never appears. | [.sh](../../launchers/android/smoke.sh) |

## art

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [bg_videos.bat](../../launchers/art/bg_videos.bat) | Renders the procedural background loops (or the screensavers) into the project's assets folder and prints each video it wrote. | [.sh](../../launchers/art/bg_videos.sh) |
| [fetch_pixelorama.bat](../../launchers/art/fetch_pixelorama.bat) | Downloads Pixelorama's pinned source and release build into tools/pixelorama (gitignored) and prints what it fetched. | [.sh](../../launchers/art/fetch_pixelorama.sh) |
| [pixelorama.bat](../../launchers/art/pixelorama.bat) | Opens a PNG in Pixelorama with the GUO tools extension (UO hue palettes, size checks, Save back to GUO); Pixelorama's window opens and this waits for it. | [.sh](../../launchers/art/pixelorama.sh) |
| [pixelorama_status.bat](../../launchers/art/pixelorama_status.bat) | Prints what of Pixelorama is fetched and installed and where the art exchange folder is. | [.sh](../../launchers/art/pixelorama_status.sh) |

## comfy

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [doctor.bat](../../launchers/comfy/doctor.bat) | Verifies pinned ComfyUI nodes and model hashes offline; --validate also checks server node types. | [.sh](../../launchers/comfy/doctor.sh) |
| [setup_studio.bat](../../launchers/comfy/setup_studio.bat) | Checks (or installs) the ComfyUI models and custom nodes GUO's art workflows need, then validates them against the server. | [.sh](../../launchers/comfy/setup_studio.sh) |

## dev

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [ab_compare.bat](../../launchers/dev/ab_compare.bat) | Logs ClassicUO and GUO into the dev shard at the same five spots and writes one comparison sheet per spot; the ClassicUO half types into its window, so it needs --allow-foreground and a desktop left alone. | [.sh](../../launchers/dev/ab_compare.sh) |
| [agent_queue.bat](../../launchers/dev/agent_queue.bat) | Posts to, tails, replies on or lists the editor's agent request queue and prints the messages. | [.sh](../../launchers/dev/agent_queue.sh) |
| [agent_skills.bat](../../launchers/dev/agent_skills.bat) | Links .agents/skills to .claude/skills so Codex finds the project skills, and prints what it linked. | [.sh](../../launchers/dev/agent_skills.sh) |
| [atlas_probe.bat](../../launchers/dev/atlas_probe.bat) | Opens a Godot window that measures what adding one sprite to a texture atlas costs at each page size and prints the timings. | [.sh](../../launchers/dev/atlas_probe.sh) |
| [audio_probe.bat](../../launchers/dev/audio_probe.bat) | Opens a Godot window that checks Godot can play UO's sound effects and music directly and prints PASS, FAIL or SKIP per stage. | [.sh](../../launchers/dev/audio_probe.sh) |
| [batcher_probe.bat](../../launchers/dev/batcher_probe.bat) | Builds the C#, then draws through the real UltimaBatcher2D in a Godot window and prints whether the C# and shader hue packing agree. | [.sh](../../launchers/dev/batcher_probe.sh) |
| [blend_probe.bat](../../launchers/dev/blend_probe.bat) | Opens a Godot window that checks a canvas item can read what was drawn under it (the effect blend modes) and prints the answer. | [.sh](../../launchers/dev/blend_probe.sh) |
| [brand_icons.bat](../../launchers/dev/brand_icons.bat) | Rebuilds every app icon and the splash from design/brand/guo-sigil.png and lists the files it wrote. | [.sh](../../launchers/dev/brand_icons.sh) |
| [build.bat](../../launchers/dev/build.bat) | Builds the Godot project's C# assemblies headless, without opening the editor, and prints the compiler output. | [.sh](../../launchers/dev/build.sh) |
| [build_guoasset.bat](../../launchers/dev/build_guoasset.bat) | Builds the guoasset parity-reference MCP server into build/guoasset with the .NET SDK and prints where the dll landed. | [.sh](../../launchers/dev/build_guoasset.sh) |
| [clean_cache.bat](../../launchers/dev/clean_cache.bat) | Deletes the runtime decode cache (UO_CACHE_DIR) so the client rebuilds it on demand, and prints the folder it cleared. | [.sh](../../launchers/dev/clean_cache.sh) |
| [depth_probe.bat](../../launchers/dev/depth_probe.bat) | Builds the C#, then draws the same quads through the canvas and the world depth pass and prints whether pixels and depth order agree (PERF-GPU step 1). | [.sh](../../launchers/dev/depth_probe.sh) |
| [docs_lint.bat](../../launchers/dev/docs_lint.bat) | Checks every local Markdown link and heading anchor in the repository and prints each broken one. | [.sh](../../launchers/dev/docs_lint.sh) |
| [editor_live.bat](../../launchers/dev/editor_live.bat) | Checks the editor's live tier end to end with two editors and a client on the private shard and prints the result. | [.sh](../../launchers/dev/editor_live.sh) |
| [editor_smoke.bat](../../launchers/dev/editor_smoke.bat) | Opens the Godot editor on the project, lets the GUO add-on check its docks against the real client install, and writes a report and screenshots to build/editor_smoke. | [.sh](../../launchers/dev/editor_smoke.sh) |
| [editor_tour.bat](../../launchers/dev/editor_tour.bat) | Records a captioned tour of every GUO editor feature as an MP4 under build/editor_tour; an editor window opens without taking focus. | [.sh](../../launchers/dev/editor_tour.sh) |
| [endurance.bat](../../launchers/dev/endurance.bat) | Plays a long session against the shard (five minutes unless given seconds) and fails if frame time, object count or memory drift between its first and last stretch. | [.sh](../../launchers/dev/endurance.sh) |
| [extract_test.bat](../../launchers/dev/extract_test.bat) | Proves ExtractDecal headless: two PNGs in, decal out, stats printed. | [.sh](../../launchers/dev/extract_test.sh) |
| [fetch_godot.bat](../../launchers/dev/fetch_godot.bat) | Downloads the pinned Godot build into tools/godot (or UO_GODOT_HOME) unless it is already there, and prints each step. | [.sh](../../launchers/dev/fetch_godot.sh) |
| [fiddler_parity_doctor.bat](../../launchers/dev/fiddler_parity_doctor.bat) | Checks or builds the independent static-art reader used to verify Fiddler-parity staged output. | [.sh](../../launchers/dev/fiddler_parity_doctor.sh) |
| [gump_studio_smoke.bat](../../launchers/dev/gump_studio_smoke.bat) | Builds the project and checks Gump Studio in a headless editor, printing pass or fail per check. | [.sh](../../launchers/dev/gump_studio_smoke.sh) |
| [guo_mcp_probe.bat](../../launchers/dev/guo_mcp_probe.bat) | Self-tests the client automation MCP bridge and prints each check; --client also starts a client to drive. | [.sh](../../launchers/dev/guo_mcp_probe.sh) |
| [install_git_hooks.bat](../../launchers/dev/install_git_hooks.bat) | Installs the pre-push docs, launcher and privacy checks into this checkout's git hooks and prints what it installed. | [.sh](../../launchers/dev/install_git_hooks.sh) |
| [launcher_lint.bat](../../launchers/dev/launcher_lint.bat) | Checks every launcher is a .bat/.sh pair with a description line, the right line endings and the executable bit, and prints each problem. | [.sh](../../launchers/dev/launcher_lint.sh) |
| [layer_shot.bat](../../launchers/dev/layer_shot.bat) | Photographs a terrain layer: probe login, shard [go, two frames saved, quits. | [.sh](../../launchers/dev/layer_shot.sh) |
| [macro_probe.bat](../../launchers/dev/macro_probe.bat) | Logs a GM into the dev shard, taps each touch-bar macro against spawned fixtures and prints a pass or fail line per macro. | [.sh](../../launchers/dev/macro_probe.sh) |
| [mcp.bat](../../launchers/dev/mcp.bat) | Runs the client with its loopback automation MCP on GUO_MCP_PORT (token in GUO_MCP_TOKEN) and stays in the foreground until the client exits. | [.sh](../../launchers/dev/mcp.sh) |
| [mesh_probe.bat](../../launchers/dev/mesh_probe.bat) | Opens a Godot window that checks a canvas mesh can carry a per-vertex value (ARRAY_CUSTOM0) for land lighting and prints the answer. | [.sh](../../launchers/dev/mesh_probe.sh) |
| [multi_client.bat](../../launchers/dev/multi_client.bat) | Runs four scripted clients against the dev shard, tiled 2x2 on screen, and writes their logs, frames and a contact sheet to build/multi_client. | [.sh](../../launchers/dev/multi_client.sh) |
| [perf_gate.bat](../../launchers/dev/perf_gate.bat) | Runs the real performance probe against a machine-local baseline; --record-baseline records it explicitly and --doctor checks prerequisites. | [.sh](../../launchers/dev/perf_gate.sh) |
| [perf_lab.bat](../../launchers/dev/perf_lab.bat) | Measures GUO's frame rate on this machine at 1920x1080 and 2560x1440 (zoom 2.5) and writes a report to share in Discord; needs the dev shard running. | [.sh](../../launchers/dev/perf_lab.sh) |
| [perf_lab_doctor.bat](../../launchers/dev/perf_lab_doctor.bat) | Checks what the perf lab needs (.NET, Godot, the UO client data, a running shard) and says how to fix what is missing; measures nothing. | [.sh](../../launchers/dev/perf_lab_doctor.sh) |
| [perf_probe.bat](../../launchers/dev/perf_probe.bat) | Measures frame time in fixed scenes against the shard and writes build/perf/perf_<label>.md and .json, or compares two labels. | [.sh](../../launchers/dev/perf_probe.sh) |
| [perf_tour.bat](../../launchers/dev/perf_tour.bat) | Runs the benchmark tour (GUO plain, defaults and UO+ max against ClassicUO at the same spots and zooms) and writes tour.md, tour.json and tour.png under build/perf_tour/<label>. | [.sh](../../launchers/dev/perf_tour.sh) |
| [playtest.bat](../../launchers/dev/playtest.bat) | Plays a scripted session against the configured shard (log in, walk, open the backpack, speak), prints each check and exits non-zero if any fails. | [.sh](../../launchers/dev/playtest.sh) |
| [plugin_probe.bat](../../launchers/dev/plugin_probe.bat) | Builds a native (MSVC) and a .NET Framework test plugin, plays a session with both loaded and prints whether each saw packets and movement. | [.sh](../../launchers/dev/plugin_probe.sh) |
| [port_drift.bat](../../launchers/dev/port_drift.bat) | Measures how far ported files have drifted from their upstream originals and prints a report. | [.sh](../../launchers/dev/port_drift.sh) |
| [port_errors.bat](../../launchers/dev/port_errors.bat) | Forces a full rebuild of godot/GUO/GUO.csproj and prints every C# error grouped by the missing symbol. | [.sh](../../launchers/dev/port_errors.sh) |
| [privacy_scan.bat](../../launchers/dev/privacy_scan.bat) | Scans the tracked files for LAN addresses, personal emails, home folders and machine paths and prints each hit. | [.sh](../../launchers/dev/privacy_scan.sh) |
| [profile_migrations.bat](../../launchers/dev/profile_migrations.bat) | Builds GUO and runs the profile migration ladder headless for the desktop, mobile and web profiles, printing each result. | [.sh](../../launchers/dev/profile_migrations.sh) |
| [pytest_all.bat](../../launchers/dev/pytest_all.bat) | Runs every Python test folder under tools\ in one pooled pytest run (pytest.ini, conftest.py). | [.sh](../../launchers/dev/pytest_all.sh) |
| [rebuild_class_cache.bat](../../launchers/dev/rebuild_class_cache.bat) | Deletes the project's .godot folder and re-imports headless, rebuilding Godot's class and script cache; prints each step. | [.sh](../../launchers/dev/rebuild_class_cache.sh) |
| [render_diff.bat](../../launchers/dev/render_diff.bat) | Compares the ClassicUO and GUO render dumps named NAME and writes build/render_dump/NAME/diff.md. | [.sh](../../launchers/dev/render_diff.sh) |
| [render_probe.bat](../../launchers/dev/render_probe.bat) | Opens a Godot window that measures how many bits of the hue vector survive a canvas item's per-quad modulate and prints the result. | [.sh](../../launchers/dev/render_probe.sh) |
| [scenario_run.bat](../../launchers/dev/scenario_run.bat) | Runs a scripted scenario with the AI or human driver and records it under build/runs, or lists and validates scenarios (lists them when given nothing). | [.sh](../../launchers/dev/scenario_run.sh) |
| [screenshot.bat](../../launchers/dev/screenshot.bat) | Starts the client, captures one frame into build/screenshots and prints where it went. | [.sh](../../launchers/dev/screenshot.sh) |
| [selftest.bat](../../launchers/dev/selftest.bat) | Runs the Python tool self-tests CI runs (no client data, no shard) and prints a pass or fail line per test file. | [.sh](../../launchers/dev/selftest.sh) |
| [server_lab.bat](../../launchers/dev/server_lab.bat) | The server compatibility lab: runs GUO's scripted cases against another UO server (ModernUO, ServUO, ...) and rewrites the wiki row; doctor checks the setup. | [.sh](../../launchers/dev/server_lab.sh) |
| [server_manager.bat](../../launchers/dev/server_manager.bat) | Sets up or checks the editor's named local servers and clients and prints what is missing (runs doctor when given nothing). | [.sh](../../launchers/dev/server_manager.sh) |
| [side_by_side.bat](../../launchers/dev/side_by_side.bat) | Starts ClassicUO and GUO side by side on one monitor at the same tile on the dev shard and leaves both running for you to compare. | [.sh](../../launchers/dev/side_by_side.sh) |
| [smoke.bat](../../launchers/dev/smoke.bat) | Runs the fast health check (engine, import, C# build, client data, offline load, editor add-on, launcher lint) and prints OK or FAILED. | [.sh](../../launchers/dev/smoke.sh) |
| [sweep.bat](../../launchers/dev/sweep.bat) | Photographs five fixed places on the dev shard into build/screenshots/sweep, one client run per spot, and fails if any spot fails. | [.sh](../../launchers/dev/sweep.sh) |
| [sync_upstream.bat](../../launchers/dev/sync_upstream.bat) | Clones or updates the read-only ClassicUO reference under sources and prints the upstream commits since the last sync. | [.sh](../../launchers/dev/sync_upstream.sh) |
| [touch_probe.bat](../../launchers/dev/touch_probe.bat) | Runs the client with the touch layer and a synthetic finger against the shard and prints a pass or fail line per gesture. | [.sh](../../launchers/dev/touch_probe.sh) |
| [worktree_setup.bat](../../launchers/dev/worktree_setup.bat) | Readies a fresh git worktree: copies your local config across, checks the engine and upstream resolve, and runs one headless import. | [.sh](../../launchers/dev/worktree_setup.sh) |
| [world_parity.bat](../../launchers/dev/world_parity.bat) | Compares the editor's UO World view with a logged-in client's frame of the same cell, pixel by pixel, and writes the comparison. | [.sh](../../launchers/dev/world_parity.sh) |

## editor

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [open_project.bat](../../launchers/editor/open_project.bat) | Builds the C#, then opens the GUO project in the pinned Godot editor, which detaches (start) so this returns at once. | [.sh](../../launchers/editor/open_project.sh) |
| [open_reference.bat](../../launchers/editor/open_reference.bat) | Opens the read-only upstream ClassicUO solution in your default IDE, which detaches (start) so this returns at once. | [.sh](../../launchers/editor/open_reference.sh) |
| [open_solution.bat](../../launchers/editor/open_solution.bat) | Opens the project's generated C# solution in your default IDE, which detaches (start) so this returns at once. | [.sh](../../launchers/editor/open_solution.sh) |

## game

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [play.bat](../../launchers/game/play.bat) | Runs the GUO client in a window against the configured shard (or the first-run wizard when no UO data is set) and stays open until the game exits. | [.sh](../../launchers/game/play.sh) |

## pipeline

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [00_bootstrap.bat](../../launchers/pipeline/00_bootstrap.bat) | Sets up a fresh clone: fetches the pinned Godot and the upstream ClassicUO reference, then verifies the UO client data. | [.sh](../../launchers/pipeline/00_bootstrap.sh) |
| [01_verify_client_data.bat](../../launchers/pipeline/01_verify_client_data.bat) | Checks every required UO data file is present, detects the client version and writes build/client_manifest.json; never writes into the install. | [.sh](../../launchers/pipeline/01_verify_client_data.sh) |
| [02_warm_cache.bat](../../launchers/pipeline/02_warm_cache.bat) | Pre-decodes UO art into the runtime cache headless so the first visit to a place does not hitch; needs step 01's manifest. | [.sh](../../launchers/pipeline/02_warm_cache.sh) |
| [03_port_audit.bat](../../launchers/pipeline/03_port_audit.bat) | Walks every upstream ClassicUO file, classifies how far it is ported and rewrites docs/port_status.md. | [.sh](../../launchers/pipeline/03_port_audit.sh) |
| [04_world_export.bat](../../launchers/pipeline/04_world_export.bat) | Exports the editor's world project into patched map and statics files under <project>/export, then reads them back to check them. | [.sh](../../launchers/pipeline/04_world_export.sh) |
| [05_extract_art.bat](../../launchers/pipeline/05_extract_art.bat) | Pipeline step 05 (optional): extracts your install's art into a local, gitignored art set and verifies every image against the pages. | [.sh](../../launchers/pipeline/05_extract_art.sh) |

## servuo

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [build.bat](../../launchers/servuo/build.bat) | Builds the private ServUO shard (127.0.0.1:2596) from its fetched source and prints the build result. | [.sh](../../launchers/servuo/build.sh) |
| [configure.bat](../../launchers/servuo/configure.bat) | Writes the private ServUO shard's configuration (127.0.0.1:2596, your UO data folder) and prints what it set. | [.sh](../../launchers/servuo/configure.sh) |
| [fetch.bat](../../launchers/servuo/fetch.bat) | Clones the private ServUO shard's source at its pin and prints what it fetched. | [.sh](../../launchers/servuo/fetch.sh) |
| [start.bat](../../launchers/servuo/start.bat) | Starts the private ServUO shard in the background (it detaches, logging to guo_shard.log) and returns once it listens on 127.0.0.1:2596. | [.sh](../../launchers/servuo/start.sh) |
| [status.bat](../../launchers/servuo/status.bat) | Prints whether the private ServUO shard is running and listening on 127.0.0.1:2596. | [.sh](../../launchers/servuo/status.sh) |
| [stop.bat](../../launchers/servuo/stop.bat) | Stops the private ServUO shard started by servuo/start and prints the result. | [.sh](../../launchers/servuo/stop.sh) |

## shard

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [build.bat](../../launchers/shard/build.bat) | Builds the ModernUO dev shard with its publish script and the .NET 10 SDK and prints where the server landed. | [.sh](../../launchers/shard/build.sh) |
| [fetch.bat](../../launchers/shard/fetch.bat) | Clones ModernUO at the pinned commit (or moves to a new pin) and applies the GUO patches, printing each one. | [.sh](../../launchers/shard/fetch.sh) |
| [playerbots.bat](../../launchers/shard/playerbots.bat) | Sets up, builds, runs or checks the optional PlayerBots ModernUO shard on its own port; run stays in the foreground until Ctrl+C. | [.sh](../../launchers/shard/playerbots.sh) |
| [populate.bat](../../launchers/shard/populate.bat) | Logs the owner into the running dev shard and types every world generator command (doors, signs, spawners, decorations), then saves; takes a few minutes. | [.sh](../../launchers/shard/populate.sh) |
| [run.bat](../../launchers/shard/run.bat) | Runs the local ModernUO dev shard in the foreground, printing its console until Ctrl+C. | [.sh](../../launchers/shard/run.sh) |

## steamdeck

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [doctor.bat](../../launchers/steamdeck/doctor.bat) | Lists what a Linux export needs on this machine and what the Steam Deck has over ssh, with the fix for each miss. | [.sh](../../launchers/steamdeck/doctor.sh) |
| [export.bat](../../launchers/steamdeck/export.bat) | Exports the Linux x86_64 build headless into build/steamdeck and writes build/steamdeck/export.log. | [.sh](../../launchers/steamdeck/export.sh) |
| [push.bat](../../launchers/steamdeck/push.bat) | Copies build/steamdeck onto the Steam Deck over ssh and writes guo.sh there; never pushes the UO client data. | [.sh](../../launchers/steamdeck/push.sh) |
| [run.bat](../../launchers/steamdeck/run.bat) | Starts GUO in the Steam Deck's Desktop-mode session over ssh; output goes to guo.log on the Deck. | [.sh](../../launchers/steamdeck/run.sh) |
| [screenshot.bat](../../launchers/steamdeck/screenshot.bat) | Photographs the Steam Deck's screen over ssh into build/steamdeck/screenshot.png (Desktop mode only). | [.sh](../../launchers/steamdeck/screenshot.sh) |
| [smoke.bat](../../launchers/steamdeck/smoke.bat) | Exports, pushes and starts GUO on the Steam Deck, waits for the login gump, photographs it into build/steamdeck/smoke.png and fails if it never appears. | [.sh](../../launchers/steamdeck/smoke.sh) |

## store

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [hosted_proof.bat](../../launchers/store/hosted_proof.bat) | Proves a hosted catalogue on this computer: signs and pushes to a local Pages branch, serves it, and installs from it with the client's C#. | [.sh](../../launchers/store/hosted_proof.sh) |
| [keygen.bat](../../launchers/store/keygen.bat) | Makes the catalogue's signing key file (never overwriting one) and prints the public key and fingerprint to share. | [.sh](../../launchers/store/keygen.sh) |
| [open.bat](../../launchers/store/open.bat) | Opens the local GUO Asset Store page (UO_STORE_URL) in your browser, which detaches (start) so this returns at once. | [.sh](../../launchers/store/open.sh) |
| [publish.bat](../../launchers/store/publish.bat) | Packs and publishes the local asset packs into the store folder (UO_STORE_DIR) and prints what it published. | [.sh](../../launchers/store/publish.sh) |
| [push_catalogue.bat](../../launchers/store/push_catalogue.bat) | Signs a catalogue index over a GitHub Pages branch and pushes it, fast-forward only (needs UO_STORE_SIGNING_KEY). | [.sh](../../launchers/store/push_catalogue.sh) |
| [serve.bat](../../launchers/store/serve.bat) | Serves the store folder on UO_STORE_URL in the foreground until Ctrl+C, printing each request. | [.sh](../../launchers/store/serve.sh) |

## web

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [doctor.bat](../../launchers/web/doctor.bat) | Lists what a C# web export needs (the community Godot build, its templates and SDK) and what this machine has, with a MISS line for each gap. | [.sh](../../launchers/web/doctor.sh) |
| [export.bat](../../launchers/web/export.bat) | Exports the web build headless into build/web and writes the export log to build/web/export.log. | [.sh](../../launchers/web/export.sh) |
| [serve.bat](../../launchers/web/serve.bat) | Serves build/web on UO_WEB_PORT with the cross-origin isolation headers in the foreground until Ctrl+C. | [.sh](../../launchers/web/serve.sh) |
| [smoke.bat](../../launchers/web/smoke.bat) | Exports and serves the web build, loads it in a headless browser and fails unless the engine prints its first console line. | [.sh](../../launchers/web/smoke.sh) |
| [ws_bridge.bat](../../launchers/web/ws_bridge.bat) | Runs the WebSocket-to-TCP bridge between the browser client and the shard in the foreground until Ctrl+C, or tests a login through it. | [.sh](../../launchers/web/ws_bridge.sh) |

## windows

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [doctor.bat](../../launchers/windows/doctor.bat) | Lists what a Windows export needs (templates, the sigil icon and splash, project settings) and what this machine has. | [.sh](../../launchers/windows/doctor.sh) |
| [export.bat](../../launchers/windows/export.bat) | Exports the Windows build headless into build/windows/GUO.exe and fails if the executable's icon is not the sigil. | [.sh](../../launchers/windows/export.sh) |

## world

| Windows launcher | Purpose | Shell twin |
|---|---|---|
| [decorate.bat](../../launchers/world/decorate.bat) | Furnishes generated multis from UO's own interiors with tools/decorate; prints its usage when given nothing. | [.sh](../../launchers/world/decorate.sh) |
| [mapgen.bat](../../launchers/world/mapgen.bat) | Generates a UO map procedurally with tools/mapgen and validates it; prints its usage when given nothing. | [.sh](../../launchers/world/mapgen.sh) |
| [mapgen_districts.bat](../../launchers/world/mapgen_districts.bat) | Builds, stages and proves towns on a generated map with tools/mapgen_districts; prints its usage when given nothing. | [.sh](../../launchers/world/mapgen_districts.sh) |
| [multi.bat](../../launchers/world/multi.bat) | Mines, builds, validates and writes new multis (houses to castles) with tools/multi; prints its usage when given nothing. | [.sh](../../launchers/world/multi.sh) |
| [uopack.bat](../../launchers/world/uopack.bat) | Unpacks UO art, gumps and animations to PNG plus JSON and packs edited folders back with tools/uopack; prints its usage when given nothing. | [.sh](../../launchers/world/uopack.sh) |
