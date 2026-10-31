# Feature coverage and evidence

Audit baseline: `main` `fc9e35880bcd201d4ed78aa9de562b2d971cc496`,
2026-10-28. The documentation source and the published wiki were audited
separately; the existing wiki last identified itself as publication of
`67630d4` on 2026-10-21. Existing specialist pages and dated compatibility
results are retained. This matrix links feature families to the implementation
or scoped proof; it does not declare every family runtime-verified today.

## How to read this coverage

- **Source** proves a setting, command or implementation exists at the audit baseline.
- **Historical proof** applies only to the recorded build, case, device and date.
- **Capture** proves the pictured state/layout at its recorded build and resolution.
- **Opt-in / editor / Labs** are delivery boundaries, not release readiness.
- Missing captures or unrun cases remain gaps; a diagram cannot replace a real screenshot.

## Feature families

| Feature | Availability / evidence limit | User guide | Source or scoped proof | Current visual coverage |
|---|---|---|---|---|
| Core gameplay / login / gumps | Client source; historical local ModernUO playtest | [Gameplay and Settings](Gameplay-and-Settings.md) | [Source / proof](../../docs/port_plan.md) | [Layout/state](Visual-Guide.md#login) |
| Production Depth World / canvas recovery | Current default selection; per-frame fallback, no new FPS proof | [Renderer and Performance](Renderer-and-Performance.md) | [Source / proof](../../godot/GUO/src/Render/DepthWorldProbe.cs) | No new capture of this feature |
| Zoom and saved camera | Depth World max 5; retained login zoom capped 2.5 | [Renderer and Performance](Renderer-and-Performance.md) | [Source / proof](../../godot/GUO/src/Configuration/Profile.cs) | No new capture of this feature |
| Full-size world / gump scaling / transfer | Profile/platform-dependent; see detailed controls | [Gump Size and Screen](Gump-Size-and-Screen.md) | [Source / proof](../../docs/ui/tall_gumps.md) | No new capture of this feature |
| Modern Options / modern gumps | Shared profile state; source/layout guide | [Gameplay and Settings](Gameplay-and-Settings.md) | [Source / proof](../../docs/ui/modern/options.md) | No new capture of modern Options |
| Controller and prompts | Cross-platform input path; hardware-specific proof differs | [Controller](Controller.md) | [Source / proof](../../docs/thor-controller-layout.md) | No new capture of this feature |
| Touch / mobile layout / fold / rotation | Geometry/emulator and physical-device evidence differ | [Mobile UI](Mobile-UI.md) | [Source / proof](../../docs/ui/adaptive_devices.md) | No new capture of this feature |
| Android test manager | Development emulator/device workflow; not physical firmware proof | [Mobile UI](Mobile-UI.md) | [Source / proof](../../docs/ui/device_test_manager.md) | No new capture of this feature |
| Dual screen / shelf / second-screen pregame | Requires display/simulator; dated device limitations retained | [Dual Screen](Dual-Screen.md) | [Source / proof](../../docs/ui/second_screen_pregame.md) | No new capture of this feature |
| Grid containers / loot / placement | Platform-dependent defaults | [Gameplay and Settings](Gameplay-and-Settings.md) | [Source / proof](../../docs/ui/grid_container.md) | No new capture of this feature |
| Canvas backgrounds / screensaver | Profile setting and mobile low-power behaviour | [Canvas Background](Canvas-Background.md) | [Source / proof](../../docs/architecture/ADR-0016-canvas-background.md) | No new capture of this feature |
| Enhanced HD / HD filtering | Off by default; verified pack needed | [Content Packs](Content-Packs.md) | [Source / proof](../../godot/GUO/src/Configuration/Profile.cs) | No new capture of this feature |
| Regional audio / NPC recordings | Off by default; content-dependent | [Content Packs](Content-Packs.md) | [Source / proof](../../godot/GUO/src/Configuration/Profile.cs) | No new capture of this feature |
| Realtime / spell lighting | Off by default; spell lights require realtime lights | [Renderer and Performance](Renderer-and-Performance.md) | [Source / proof](../../godot/GUO/src/Configuration/Profile.cs) | No new capture of this feature |
| Ambient fog / leaves / dust / UO+ | Off by default; density/cap settings | [Gameplay and Settings](Gameplay-and-Settings.md) | [Source / proof](../../godot/GUO/src/Configuration/Profile.cs) | [Layout/state](Visual-Guide.md#uoplus-options) |
| Styled water / ripples / shard consent | Classic default; revocable policy and migration limits | [Water](Water.md) | [Source / proof](../../docs/water_authoring.md) | [Layout/state](Visual-Guide.md#water-authoring) |
| PostFX / looks / LUTs | Optional presentation; no parity/performance promise | [Screen Effects](Screen-Effects.md) | [Source / proof](../../tools/postfx/README.md) | No new capture of this feature |
| Content Store / typed packs / verification | Separate install from activation; no executable packs | [Asset Store](Asset-Store.md) | [Source / proof](../../tools/asset_store/README.md) | [Layout/state](Visual-Guide.md#content-store) |
| Localization / journal translation | Provider-dependent translation distinct from bundled UI strings | [Gameplay and Settings](Gameplay-and-Settings.md) | [Source / proof](../../docs/localization-runtime.md) | No new capture of this feature |
| Embedded scripting | Bounded client scripting, not arbitrary Python | [Modding](Modding.md) | [Source / proof](../../docs/embedded-scripting.md) | No new capture of this feature |
| Desktop mod ABI / packet handlers | Explicit load, shard scope; Android/web unsupported | [Modding](Modding.md) | [Source / proof](../../docs/modding/surface_2026-10-18.md) | No new capture of this feature |
| Assistant plugins / managed CLR host | Native ABI; managed .NET Framework host Windows-only | [Modding](Modding.md) | [Source / proof](../../tools/plugin_probe/README.md) | No new capture of this feature |
| Windows build/export | Doctor/export source; actual artifact success must be checked | [Windows Build](Windows-Build.md) | [Source / proof](../../tools/windows/README.md) | No new capture of this feature |
| Linux desktop | Dedicated guide; not Windows validation equivalence | [Linux](Linux.md) | [Source / proof](../../docs/wiki/Linux.md) | No new capture of this feature |
| Android ARM64 | Debug export/device workflow; not all Android devices certified | [Android Build](Android-Build.md) | [Source / proof](../../tools/android/README.md) | No new capture of this feature |
| Steam Deck | Linux export/SSH workflow; device testing separate | [Steam Deck](Steam-Deck.md) | [Source / proof](../../tools/steamdeck/README.md) | No new capture of this feature |
| Web | Community engine + WebSocket bridge; Chrome/Firefox evidence differs | [Web Client](Web-Client.md) | [Source / proof](../../tools/web/README.md) | No new capture of this feature |
| World editor / overlay / live tier | Editor-only; live bridge requires supported server | [World Editing](World-Editing.md) | [Source / proof](../../tools/editor_live/README.md) | [Layout/state](Visual-Guide.md#world-tools) |
| Asset browser / fields / working documents | Editor-only; source/replacement/stage distinct | [Assets and Fiddler](Assets-and-Fiddler.md) | [Source / proof](../../docs/editor_staticstudio.md) | [Layout/state](Visual-Guide.md#assets-workbench) |
| Fiddler exchanges / hue / cliloc / multis | Format-specific proof; no blanket UOFiddler parity | [Assets and Fiddler](Assets-and-Fiddler.md) | [Source / proof](../../tools/fiddler_parity/README.md) | [Layout/state](Visual-Guide.md#hue-operations) |
| Pixelorama / PNG-sidecar / uopack | External editor and local staged native data | [Assets and Fiddler](Assets-and-Fiddler.md) | [Source / proof](../../tools/uopack/README.md) | No new capture of this feature |
| Extracted asset sets / animation sets | Local derived data; no redistribution of original art | [Assets and Fiddler](Assets-and-Fiddler.md) | [Source / proof](../../tools/art_extract/README.md) | No new capture of this feature |
| Gump Studio / runtime capture/apply | Editor authoring; custom control limits | [Editor](Editor.md) | [Source / proof](../../docs/ui/gump_studio.md) | [Layout/state](Visual-Guide.md#gump-studio) |
| MapGen / districts / layout import | Offline generation, validation and authoring inputs | [World Editing](World-Editing.md) | [Source / proof](../../tools/mapgen/README.md) | No new capture of this feature |
| Multis / decoration / native slot staging | Staged output; original install read-only | [Building Multis](Building-Multis.md) | [Source / proof](../../tools/multi/README.md) | [Layout/state](Visual-Guide.md#multi-browser) |
| Server content / backend adapters | Trusted generators produce server definitions | [Author UO Data](Author-UO-Data.md) | [Source / proof](../../tools/server_adapters/README.md) | No new capture of this feature |
| Server/client profiles / workspace / slots | Editor selection and separate run slots | [Servers and Accounts](Servers-and-Accounts.md) | [Source / proof](../../tools/server_manager/README.md) | No new capture of this feature |
| Admin / godview / account / backup / audit | Local-server authority required; mutations explicit | [Admin Tab](Admin-Tab.md) | [Source / proof](../../tools/editor_shard/README.md) | No new capture of this feature |
| ModernUO / ServUO / UOX3 / Sphere lab | Historical case grid; not-run remains not-run | [Server Compatibility](Server-Compatibility.md) | [Source / proof](../../tools/server_lab/README.md) | No new capture of this feature |
| PlayerBots / Linux shard scripts | Optional development tools; scripts require review before execution | [Dev Shard](Dev-Shard.md) | [Source / proof](../../tools/playerbots/README.md) | No new capture of this feature |
| AI hub / workspace / generation / queue | Editor-only; outer AI gate, provider/dependency requirements | [AI and MCP](AI-and-MCP.md) | [Source / proof](../../tools/ai_hub/README.md) | [Layout/state](Visual-Guide.md#ai-chat) |
| Client MCP | Debug/source desktop only, token/loopback, no release listener | [AI and MCP](AI-and-MCP.md) | [Source / proof](../../tools/guo_mcp/README.md) | No new capture of this feature |
| Editor MCP / F3 tools / permissions | AI gate, per-group policy, explicit mutation approval | [AI and MCP](AI-and-MCP.md) | [Source / proof](../../tools/editor_mcp/README.md) | No new capture of this feature |
| guoasset reference MCP | Upstream loaders; separate from runtime control | [AI and MCP](AI-and-MCP.md) | [Source / proof](../../tools/guoasset/README.md) | No new capture of this feature |
| Scenario runner / probes / tours | Expectations and provenance; planned commands not implemented | [Scripted Runs and Probes](Scripted-Runs-and-Probes.md) | [Source / proof](../../tools/scenario_run/README.md) | No new capture of this feature |
| Fit Lab / SpriteMotion | Editor integration, optional external service | [Editor](Editor.md) | [Source / proof](../../tools/spritemotion/README.md) | No new capture of this feature |
| Labs Weather / WaterRain / GPU renderer | GuoLabs=true Debug only; never exported | [Feature Layers](Feature-Layers.md) | [Source / proof](../../godot/GUO/src/Labs/README.md) | No new capture of this feature |
| Parity / sort / pixel / performance diagnostics | Bounded proofs; no current speedup inferred | [Parity and Drift](Parity-and-Drift.md) | [Source / proof](../../docs/perf/performance_gate.md) | No new capture of this feature |
| Port audit / drift / upstream sync | Presence counts are not working gameplay | [Architecture](Architecture.md) | [Source / proof](../../tools/port_audit/README.md) | No new capture of this feature |

## Entry-point coverage

- [115 Windows launchers and their shell twins](Launcher-Reference.md).
- [93 top-level tool READMEs](Tool-Reference.md), including diagnostic and maintenance jobs.
- [70 active launcher defaults](Configuration-Reference.md); local-only/commented overrides stay in the source configuration guide.
- [250 scalar profile initializers](Profile-Reference.md), with platform migration caveats.
- [Scenario catalogue](Scenario-Catalogue.md), including format-specific Fiddler slices and expectation files.
- [Visual Guide](Visual-Guide.md): capture provenance and remaining visual gaps.

## Runtime discovery

[Runtime Reference](Runtime-Reference.md) indexes 279 C# user-argument
literals and 118 environment names, including editor, Labs and probe
contexts. These are source references, not recommended defaults or new tests.

## Additional user guides

[Platform Support](Platform-Support.md) separates export paths from device
evidence. [Scripts and Languages](Scripting-and-Languages.md) records actual
syntax, stop/recovery controls, local translation boundaries and queue limits.

## Remaining acceptance limits

This audit is not a new full gameplay/server matrix, device certification,
performance comparison or validation of every external AI service. The
server-generated [compatibility grid](Server-Compatibility.md) preserves
not-run and failed cells. Source-only tools and settings are explicitly
identified in the references. The visual register keeps screenshot gaps visible.
