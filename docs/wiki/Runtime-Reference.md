# Runtime switches and environment reference

Source-literal inventory at `fc9e3588` (2026-10-28), covering C# under
`godot/GUO`. This is a developer index, **not a recommended launch profile**.
It includes test/probe switches, fixtures, internal experiments and editor-only
arguments. A literal's presence is not proof that its code is compiled,
accepted in every mode, safe on a live shard, or enabled by default.

The source links and adjacent guards determine the contract. A file outside
`Labs/` can still have a `GUO_LABS`, `TOOLS` or target-platform guard. Listed
guards are lexical context, not a full preprocessor evaluation; conditional
branches and indirect registrations require reading the source. Repeated
references are limited to the first three links for readability.

For normal use choose [Renderer and Performance](Renderer-and-Performance.md),
[Gameplay and Settings](Gameplay-and-Settings.md), [Configuration](Configuration.md)
or the target guide. Do not use fault injection, staff commands or synthetic
probe accounts in a real play session. Secrets remain environment-only; this
reference lists names and never resolved values.

## User-argument literals

Put Godot engine flags before `--` and client user flags after it when invoking
the engine/export directly. The play launcher routes its own supported flags;
read [the play tool](../../tools/play/README.md) before composing commands.
Some options take the next value, some take `=value`, and a listed prefix may
not be a complete option by itself. Read the parser instead of guessing.

| Literal | Source category / guard | First references |
|---|---|---|
| `--account` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L95) / [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L96) / [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L107) |
| `--accounts-restart` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2390) |
| `--adapter-slots` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L403) |
| `--adaptive-layout` | Runtime/diagnostic source | [source](../../godot/GUO/src/Platform/Android/AdaptiveLayout.cs#L38) |
| `--all` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/SplashProof.cs#L57) |
| `--ambient-particles-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L111) |
| `--art-sample` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2062) |
| `--art-set` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L272) / [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L77) |
| `--art-shard` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L360) / [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L105) |
| `--as` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L381) |
| `--asset-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2295) |
| `--asset-probe-ids` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2298) |
| `--attach` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L130) |
| `--audio-content-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L126) |
| `--autologin` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L107) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2431) |
| `--backend` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L389) |
| `--background` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2564) |
| `--banner` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L162) |
| `--batched-world` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2219) |
| `--batcher-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2052) / [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L19) |
| `--bind` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L398) |
| `--body` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L610) |
| `--brushes` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L68) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L73) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L710) |
| `--cache-dir` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/RunBar.cs#L392) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2491) / [source](../../godot/GUO/src/Workspace/ClientLaunch.cs#L111) |
| `--canvas-world` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2056) / [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L17) |
| `--catalogue` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L390) |
| `--character` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2434) |
| `--chunk-skip` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2139) |
| `--chunk-skip=off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2147) |
| `--click-splat` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2594) |
| `--client` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ParityPanel.cs#L169) |
| `--client-data` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L1437) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2481) |
| `--client-version` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L158) / [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L125) / [source](../../godot/GUO/addons/guo_editor/Panels/HueStageDialog.cs#L107) |
| `--companion-tabs` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2345) |
| `--confirm` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L151) / [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L180) / [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L140) |
| `--cover-cull` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2150) |
| `--custom-data` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2485) |
| `--db` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L77) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L391) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L454) |
| `--depth-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2059) |
| `--depth-world` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2055) / [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L20) |
| `--design-nearby` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2613) |
| `--disable` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L68) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L122) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L727) |
| `--door-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2286) |
| `--dry-run` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L416) |
| `--dual-off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2401) |
| `--dual-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2384) |
| `--dual-probe-held` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2394) |
| `--dual-screen` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2398) |
| `--editor` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/ServerManagerWindow.cs#L129) |
| `--edits` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L125) / [source](../../godot/GUO/addons/guo_editor/Panels/HueStageDialog.cs#L107) / [source](../../godot/GUO/addons/guo_editor/Panels/MetadataStageDialog.cs#L100) |
| `--effects-plain` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2301) |
| `--effects-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2415) |
| `--enable` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L727) |
| `--endure` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2461) |
| `--experimental-acp` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/AgentCatalog.cs#L43) |
| `--extract-dest` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2536) |
| `--extract-feather` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2547) |
| `--extract-in` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2530) |
| `--extract-out` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2533) |
| `--extract-test` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2527) |
| `--extract-tol` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2539) |
| `--fast` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L715) |
| `--filename` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L125) |
| `--files-override` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2561) |
| `--first-run-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2488) |
| `--focus` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2363) |
| `--folder` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L90) |
| `--from` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L125) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L391) |
| `--gamepad-clip` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2191) |
| `--gamepad-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2156) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2194) |
| `--gamepad-trace` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2348) |
| `--glyph-shots` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2188) |
| `--gpu-world` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2105) |
| `--gpu-world-raw` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2117) |
| `--gpu-world=off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2114) |
| `--gpu-world=on` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2106) |
| `--guo-assets-workbench` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L182) |
| `--guo-editor-admin` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAdmin.cs#L33) |
| `--guo-editor-data-stage` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorData.cs#L134) / [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L601) |
| `--guo-editor-header-only` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L185) |
| `--guo-editor-lights-demo` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeLightsDemo.cs#L28) |
| `--guo-editor-live` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L61) |
| `--guo-editor-live-as` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L1007) |
| `--guo-editor-live-facet` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L915) |
| `--guo-editor-live-port` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L925) |
| `--guo-editor-live-traffic` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeLive.cs#L39) |
| `--guo-editor-live-traffic-spawn` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeLive.cs#L55) |
| `--guo-editor-multi-dump` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L733) |
| `--guo-editor-multi-inspect` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L241) |
| `--guo-editor-multi-max-z` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L598) |
| `--guo-editor-multi-shot` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L51) |
| `--guo-editor-smoke` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L32) |
| `--guo-editor-smoke-art` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L33) |
| `--guo-editor-smoke-reload` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L34) |
| `--guo-editor-tour` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L38) |
| `--guo-editor-tour-live-port` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L39) |
| `--guo-editor-tour-segments` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L41) |
| `--guo-editor-tour-size` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L40) |
| `--guo-editor-world-pan` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L48) |
| `--guo-editor-world-shot` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L41) |
| `--guo-gump-studio-only` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L255) |
| `--guo-gump-studio-visual` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L257) |
| `--guo-multi-text-proof` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmoke.cs#L179) |
| `--guo-pixelorama-probe` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor_pixelorama/GuoPixeloramaPlugin.cs#L19) |
| `--hd-content-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L108) |
| `--hear-secs` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2603) |
| `--height` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L707) |
| `--hide-gumps` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2471) |
| `--highlight-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2077) |
| `--holder` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L382) |
| `--host` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeClients.cs#L70) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeClients.cs#L74) / [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L392) |
| `--id` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/StaticStageDialog.cs#L145) |
| `--ids` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BulkPanel.cs#L118) |
| `--image` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/StaticStageDialog.cs#L145) |
| `--input-file` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L124) |
| `--input-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2068) |
| `--install` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor_pixelorama/PixeloramaView.cs#L72) |
| `--inventory-touch-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2307) |
| `--json` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L143) / [source](../../godot/GUO/addons/guo_editor/Store/StoreBench.cs#L211) / [source](../../godot/GUO/addons/guo_editor/Store/StorePublishTab.cs#L159) |
| `--keyboard-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2172) |
| `--kind` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ParityPanel.cs#L180) |
| `--language` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2498) |
| `--layer-at` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2510) |
| `--layer-capture` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2516) |
| `--layer-shot` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2507) |
| `--layer-swap` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2513) |
| `--layer-zoom` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2519) |
| `--limit` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L143) |
| `--load-mods` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2280) |
| `--login-preflight-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2256) |
| `--login-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2370) |
| `--login-probe-stay` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2380) |
| `--macro-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2331) |
| `--merged-cover` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2195) |
| `--merged-cover=off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2206) |
| `--merged-land` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2092) |
| `--merged-land=array` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2086) |
| `--merged-land=off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2095) |
| `--merged-land=ordered` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2089) |
| `--minutes` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L382) |
| `--mods-dir` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2283) |
| `--mods-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L120) |
| `--multi` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ParityPanel.cs#L173) |
| `--multimesh-sprites` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2209) |
| `--multimesh-sprites=off` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2216) |
| `--multimesh-sprites=on` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2210) |
| `--music-expect` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2591) |
| `--name` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L389) |
| `--name=` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L610) |
| `--no-art-set` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L272) / [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L78) |
| `--no-focus` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2360) |
| `--no-splash` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2357) |
| `--nologo` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/ServerManagerWindow.cs#L143) |
| `--objects-dump` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2292) |
| `--objects-watch` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2289) |
| `--offline` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2043) |
| `--one-screen` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2404) |
| `--one-screen-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2185) |
| `--options-touch-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2310) |
| `--out` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L149) / [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L1475) / [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L1481) |
| `--own-profile` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2321) |
| `--pack` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L390) |
| `--pad-regression-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2159) |
| `--pad-wheels-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2176) |
| `--password` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2428) / [source](../../godot/GUO/src/Pregame3D/Pregame3DProbe.cs#L168) |
| `--path` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/RunBar.cs#L392) / [source](../../godot/GUO/addons/guo_editor/ServerManagerWindow.cs#L129) / [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L2150) |
| `--perf-expect-account` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L95) / [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L96) / [source](../../godot/GUO/src/Bootstrap/LoginPreflightProbe.cs#L107) |
| `--perf-expect-character` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2237) |
| `--perf-expect-map` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2244) |
| `--perf-label` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2228) |
| `--perf-out` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2225) |
| `--perf-parity` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2231) |
| `--perf-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2222) |
| `--perf-profile` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2268) |
| `--perf-quiet` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2262) |
| `--perf-scene` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2234) |
| `--perf-static-world` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2259) |
| `--perf-zoom` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2265) |
| `--plain` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/SplashProof.cs#L60) |
| `--plan` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L151) / [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L158) / [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L125) |
| `--play` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L2153) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2065) |
| `--play-music` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2584) |
| `--play-sound` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2577) |
| `--port` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L392) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2567) |
| `--portrait-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2083) |
| `--postfx` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2277) |
| `--postfx-sheet` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2271) |
| `--postfx-tour` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2274) |
| `--pregame-3d` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2180) / [source](../../godot/GUO/src/Pregame3D/Pregame3DSettings.cs#L53) |
| `--pregame-classic` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2181) / [source](../../godot/GUO/src/Pregame3D/Pregame3DSettings.cs#L59) |
| `--pregame-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2387) |
| `--pregame3d-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2182) / [source](../../godot/GUO/src/Pregame3D/Pregame3DSettings.cs#L33) / [source](../../godot/GUO/src/Pregame3D/Pregame3DSettings.cs#L53) |
| `--presentation-parity` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2324) |
| `--preset` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L302) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L705) |
| `--project` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L379) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L380) / [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L1475) |
| `--provenance` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StorePublishTab.cs#L280) |
| `--purpose` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L382) |
| `--rd-content-cache` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/DepthWorld/OrderedWorldRecords.cs#L14) |
| `--rd-world` | Runtime/diagnostic source; guard GUO_LABS | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2099) / [source](../../godot/GUO/src/Render/DepthWorld/OrderedWorldCapture.cs#L16) |
| `--realtime-lights-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L109) |
| `--reduced` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/SplashProof.cs#L54) |
| `--render-particles` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/Particles/AmbientParticlesProbe.cs#L14) |
| `--resolution` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor_pixelorama/PixeloramaView.cs#L98) |
| `--run` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L131) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L847) |
| `--saf-forget` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2366) |
| `--say` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2600) |
| `--scan` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L158) |
| `--scratch-profile` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2318) |
| `--screen-scale` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2408) |
| `--screenshot` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2049) |
| `--screenshot-dir` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2501) / [source](../../godot/GUO/src/Pregame3D/Pregame3DProbe.cs#L163) |
| `--screenshot-name` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2504) |
| `--scripts` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L391) |
| `--scripts-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2327) |
| `--seed` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L118) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L705) / [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L704) |
| `--selection-seam-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L114) |
| `--serial` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L610) |
| `--set` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L68) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L741) |
| `--shard-command` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2468) |
| `--shard-dir` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L391) / [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L501) / [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L559) |
| `--shared-cover` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2121) |
| `--shared-cover-shader-parity` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2122) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2130) |
| `--shot-after` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2474) |
| `--show-touches` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2342) |
| `--sidecar` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Art/ExternalTools.cs#L140) |
| `--silent` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L206) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2351) |
| `--single-window` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor_pixelorama/PixeloramaView.cs#L98) |
| `--size` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L118) / [source](../../godot/GUO/src/Bootstrap/SplashProof.cs#L63) |
| `--skip-at` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/SplashProof.cs#L67) |
| `--sound` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2422) |
| `--source` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L90) / [source](../../godot/GUO/addons/guo_editor/Panels/ClilocStageDialog.cs#L124) / [source](../../godot/GUO/addons/guo_editor/Panels/HueStageDialog.cs#L106) |
| `--spell-lighting-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L110) |
| `--sphere-existing-graphic` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L410) |
| `--splash` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2354) |
| `--spritemotion-smoke` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Docks/SpriteMotionDock.cs#L49) / [source](../../godot/GUO/addons/guo_editor/Docks/SpriteMotionDock.cs#L127) |
| `--stage` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MultiEdit/MultiEditView.Deploy.cs#L209) / [source](../../godot/GUO/addons/guo_editor/MultiEdit/MultiStore.cs#L255) / [source](../../godot/GUO/addons/guo_editor/Panels/BatchStaticDialog.cs#L90) |
| `--stay` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2074) |
| `--step-previews` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L149) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L720) |
| `--store-catalogue-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreCatalogueProbe.cs#L15) |
| `--store-dir` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StorePublishTab.cs#L156) / [source](../../godot/GUO/addons/guo_editor/Store/StorePublishTab.cs#L240) |
| `--store-install` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2558) |
| `--styles` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MultiEdit/Generate/MultiGenerateClient.cs#L77) |
| `--text=` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L704) |
| `--theme` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2619) |
| `--theme-dir` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2616) |
| `--theme-zone` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2622) |
| `--tile` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ParityPanel.cs#L178) |
| `--timeout` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L610) |
| `--to` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/QueueClient.cs#L125) |
| `--tool` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L379) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L380) |
| `--touch` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2304) |
| `--touch-gesture-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2163) |
| `--touch-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2309) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2312) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2313) |
| `--touch-trace` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2339) |
| `--trade-partner` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L2154) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L2071) |
| `--translation-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2376) |
| `--type=` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L610) |
| `--ui-gallery` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2335) |
| `--ui-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2373) |
| `--uncapped` | Runtime/diagnostic source; guard GUO_LABS | [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L34) |
| `--uoplus-ui-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2168) |
| `--url` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StorePublishTab.cs#L283) |
| `--version` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L390) |
| `--voice` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2597) |
| `--voice-fake` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2610) |
| `--voice=` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L704) |
| `--walk-to` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2574) |
| `--warm-cache` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2046) |
| `--water-style` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2202) |
| `--wav` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L704) |
| `--what` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Panels/ArtSetPanel.cs#L172) / [source](../../godot/GUO/addons/guo_editor/Panels/ArtSetPanel.cs#L179) / [source](../../godot/GUO/addons/guo_editor/Panels/BulkPanel.cs#L118) |
| `--wid` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor_pixelorama/PixeloramaView.cs#L98) |
| `--width` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L707) |
| `--window-position` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2437) |
| `--window-size` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2448) |
| `--work` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Store/StoreServerTab.cs#L392) |
| `--world-project` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L131) / [source](../../godot/GUO/addons/guo_editor/MapGen/MapGenView.cs#L847) |
| `--zoom-max=` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L50) |
| `--zoom-probe` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L2080) |

## Environment-variable literals

No effective default is inferred from the variable name. Some reads test
presence, some compare against `1`, and some parse a bounded number or path.
Only the linked implementation establishes that meaning. Launchers/Python
can consume additional settings: see [Configuration Reference](Configuration-Reference.md)
and the [Tool Reference](Tool-Reference.md).

| Name | Source category / guard | First references |
|---|---|---|
| `GUO_AI_HOME` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/SessionScanner.cs#L52) |
| `GUO_AI_REAL_OLLAMA` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L348) |
| `GUO_AI_SKIP` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAi.cs#L54) |
| `GUO_ART_OVERRIDE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/ArtLoader.cs#L83) |
| `GUO_ATLAS_PAGE_1024` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/TextureAtlas.cs#L70) |
| `GUO_CANVAS_CLEAR_USED` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.cs#L271) |
| `GUO_DEFAULT_VOICE` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L365) |
| `GUO_DEPTH_PROBE_DUMP` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/DepthKeyProbe.cs#L106) / [source](../../godot/GUO/src/Bootstrap/DepthKeyProbe.cs#L122) / [source](../../godot/GUO/src/Bootstrap/DepthProbe.cs#L251) |
| `GUO_DW_REUSE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Game/Scenes/GameScene.ChunkReuse.cs#L22) |
| `GUO_DW_REUSE_VERIFY` | Runtime/diagnostic source | [source](../../godot/GUO/src/Game/Scenes/GameScene.ChunkReuse.cs#L23) |
| `GUO_EDITOR_MCP_PORT` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAiToggle.cs#L104) |
| `GUO_EDITOR_MCP_PREAPPROVED` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpAccess.cs#L93) / [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpServer.cs#L172) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeMcp.cs#L111) |
| `GUO_EDITOR_MCP_TOKEN` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpConnection.cs#L22) / [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpServer.cs#L52) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeAiToggle.cs#L105) |
| `GUO_EDITOR_SCRIPTED` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpAccess.cs#L92) / [source](../../godot/GUO/addons/guo_editor/Automation/EditorMcpServer.cs#L171) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeMcp.cs#L110) |
| `GUO_EDITOR_WINDOW_SIZE` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorTour.cs#L1816) |
| `GUO_LAYER_DIR` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/TerrainLayers.cs#L69) |
| `GUO_LOGS_SKIP` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeLogs.cs#L53) |
| `GUO_MAPGEN_SKIP` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMapGen.cs#L41) |
| `GUO_MCP_PORT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Automation/McpHost.cs#L43) |
| `GUO_MCP_TOKEN` | Runtime/diagnostic source | [source](../../godot/GUO/src/Automation/McpHost.cs#L51) |
| `GUO_MINIMAP_TEXTURE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Game/UI/Gumps/MiniMapGump.cs#L41) |
| `GUO_MULTIEDIT_SKIP` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMultiEdit.cs#L42) |
| `GUO_MULTIMESH_MIN_RUN` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.MultiMesh.cs#L47) |
| `GUO_MULTIMESH_SPRITES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.MultiMesh.cs#L36) |
| `GUO_MUSIC_DIR` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeAiToggle.cs#L178) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeArt.cs#L647) / [source](../../godot/GUO/src/IO/Audio/ZoneAudio.cs#L42) |
| `GUO_NO_MMAP` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/MMFileReader.cs#L20) |
| `GUO_PARTICLES_PROOF` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/Particles/AmbientParticlesProbe.cs#L23) |
| `GUO_PERF_DUMP` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfDump.cs#L87) / [source](../../godot/GUO/src/Render/UltimaBatcher2D.cs#L1426) |
| `GUO_PERF_DW_CEILING` | Runtime/diagnostic source; guard GUO_LABS | [source](../../godot/GUO/src/Render/DepthWorldProbe.cs#L28) |
| `GUO_PERF_GAME_WINDOW` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L394) |
| `GUO_PERF_INTERVAL_COUNTERS` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L217) |
| `GUO_PERF_MANIFEST` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfRuntimeIdentity.cs#L38) |
| `GUO_PERF_MINIMAP` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L406) / [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L1236) |
| `GUO_PERF_PARITY_CANVAS` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L219) |
| `GUO_PERF_PARITY_COVER_CANDIDATES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L220) |
| `GUO_PERF_PLAY_UI` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L383) |
| `GUO_PERF_QUIET` | Runtime/diagnostic source; guard !PERF_DUMP_CUO | [source](../../godot/GUO/src/Bootstrap/PerfDump.cs#L106) |
| `GUO_PERF_RAW_FRAMES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L759) / [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L1235) |
| `GUO_PERF_RUNTIME_ATTRIBUTION` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L218) |
| `GUO_PERF_SAMPLE_SECONDS` | Runtime/diagnostic source; guard !PERF_DUMP_CUO | [source](../../godot/GUO/src/Bootstrap/PerfDump.cs#L101) |
| `GUO_PERF_SHOT_DIR` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L510) |
| `GUO_PERF_SORT_INPUT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.SortInputs.cs#L20) |
| `GUO_PERF_WALK_ORIGIN` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L228) |
| `GUO_PERF_WALK_ROUTE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PerfProbe.cs#L229) |
| `GUO_PIXELORAMA_ANIMATION_SAVE` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeArt.cs#L393) |
| `GUO_PIXELORAMA_SAVE_PROOF` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeArt.cs#L204) |
| `GUO_POSTFX_TIER` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/PostFx/PostFxStack.cs#L43) |
| `GUO_PROBE_ART` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/ComfyContentProbe.cs#L33) |
| `GUO_PROBE_MUSIC` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/ComfyContentProbe.cs#L29) |
| `GUO_PROBE_REAL_RESTART` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L1147) |
| `GUO_PROBE_SFX` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/ComfyContentProbe.cs#L28) |
| `GUO_PYTHON` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L170) |
| `GUO_REDUCED_MOTION` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/SplashIntro.cs#L90) / [source](../../godot/GUO/src/Input/Touch/WindowMenu.cs#L44) |
| `GUO_RENDER_DUMP_DIR` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/RenderDump.cs#L82) |
| `GUO_RETAIN_CANVAS` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.RetainedCanvas.cs#L10) |
| `GUO_RETAIN_CANVAS_RECLAIM_EMPTY` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.RetainedCanvas.cs#L12) |
| `GUO_REVIEW_FRAMES` | Editor-only source; guard TOOLS && GUO_LABS | [source](../../godot/GUO/addons/guo_editor/Review/EffectReviewVideo.cs#L25) |
| `GUO_REVIEW_MODE` | Editor-only source; guard TOOLS && GUO_LABS | [source](../../godot/GUO/addons/guo_editor/Review/EffectReviewVideo.cs#L27) |
| `GUO_REVIEW_UI_FRAMES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Configuration/UoPlusProbe.cs#L186) |
| `GUO_SCRIPT_PACK_PROBE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/ScriptsProbe.cs#L142) |
| `GUO_SHOT_DUMP` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/ShotDump.cs#L98) |
| `GUO_SHOT_VARIANT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/ShotDump.cs#L111) |
| `GUO_SPLAT_FULL` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/Splats/SplatBatcher.cs#L117) |
| `GUO_SPLAT_STAGE` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeMultiEdit.cs#L436) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeMultiEdit.cs#L512) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeStudio.cs#L129) |
| `GUO_STORE_EDITOR_PROOF` | Runtime/diagnostic source; guard TOOLS | [source](../../godot/GUO/src/Store/StoreEditorProof.cs#L13) |
| `GUO_STORE_PROOF_SIZE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreProof.cs#L23) |
| `GUO_STORE_PROOF_VIEW` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreProof.cs#L46) |
| `GUO_STORE_SKIP` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeStore.cs#L47) |
| `GUO_TEXT_MESHES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/UltimaBatcher2D.GlyphMesh.cs#L12) |
| `GUO_UI_SCALE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Input/Touch/UoTheme.cs#L91) |
| `GUO_UOPLUS_PROOF` | Runtime/diagnostic source | [source](../../godot/GUO/src/Configuration/UoPlusProbe.cs#L156) |
| `GUO_VARIANT_DIR` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/EditorSmokeStudio.cs#L164) / [source](../../godot/GUO/addons/guo_editor/EditorSmokeStudio.cs#L202) / [source](../../godot/GUO/src/Assets/VariantAtlas.cs#L41) |
| `GUO_VERIFY_COVER_CANDIDATES` | Runtime/diagnostic source | [source](../../godot/GUO/src/Game/Scenes/CoverCandidateVerifier.cs#L13) |
| `GUO_VERIFY_GATHER_INPUTS` | Runtime/diagnostic source | [source](../../godot/GUO/src/Game/Scenes/GameScene.GatherInputs.cs#L46) |
| `GUO_VOICE` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L103) |
| `GUO_VOICES` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L146) |
| `GUO_VOICE_TOOL` | Runtime/diagnostic source | [source](../../godot/GUO/src/IO/Audio/VoiceManager.cs#L158) |
| `GUO_WATER_DEBUG` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/Water/WaterRender.cs#L449) |
| `GUO_WATER_PROBE_OUT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Render/Water/WaterRippleProbe.cs#L83) |
| `GUO_WATER_RAIN_PROOF` | Labs-only source; guard GUO_LABS | [source](../../godot/GUO/src/Labs/WaterRain/WaterRainVisualProbe.cs#L87) |
| `GUO_WATER_WORKBENCH_OUT` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Water/WaterWorkbenchProbe.cs#L34) |
| `GUO_WEATHER_PROOF` | Labs-only source; guard GUO_LABS | [source](../../godot/GUO/src/Labs/Weather/WeatherVisualProbe.cs#L48) |
| `GUO_WEATHER_PROOF_DIR` | Labs-only source; guard GUO_LABS | [source](../../godot/GUO/src/Labs/Weather/WeatherLiveProof.cs#L22) |
| `GUO_ZLIB` | Runtime/diagnostic source | [source](../../godot/GUO/src/Utility/ZLib.cs#L51) |
| `HOME` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/DataSources.cs#L354) |
| `LOCALAPPDATA` | Runtime/diagnostic source | [source](../../godot/GUO/src/Workspace/Workspace.cs#L72) |
| `OLLAMA_HOST` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/AI/AiChatTab.cs#L259) |
| `PATH` | Editor-only source; guard TOOLS; Runtime/diagnostic source | [source](../../godot/GUO/addons/guo_editor/AI/AgentCatalog.cs#L87) / [source](../../godot/GUO/addons/guo_editor/Art/ExternalTools.cs#L30) / [source](../../godot/GUO/addons/guo_editor/Art/ExternalTools.cs#L105) |
| `UO_ART_EXCHANGE` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/EditorSmokeArt.cs#L129) / [source](../../godot/GUO/addons/guo_editor/EditorTourAnim.cs#L29) |
| `UO_ART_EXTRACT_DIR` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L85) |
| `UO_ART_KEY_DIR` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L118) |
| `UO_ART_PARITY_MODE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L47) / [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L48) |
| `UO_ART_PARITY_ONLY` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L64) |
| `UO_ART_PARITY_OUT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L46) |
| `UO_ART_SET` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L74) |
| `UO_ART_SET_CACHE_MB` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L260) |
| `UO_ART_SHARD` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ExtractedArtSource.cs#L104) |
| `UO_BUILD_DIR` | Editor-only source; guard TOOLS | [source](../../godot/GUO/addons/guo_editor/Water/WaterWorkbenchProbe.cs#L145) |
| `UO_CLIENT_DATA` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L37) / [source](../../godot/GUO/src/Pregame3D/Pregame3DProbe.cs#L430) / [source](../../godot/GUO/src/Store/ComfyContentProbe.cs#L25) |
| `UO_CLIENT_VERSION` | Runtime/diagnostic source | [source](../../godot/GUO/src/Assets/Extracted/ArtSetParityProbe.cs#L37) / [source](../../godot/GUO/src/Bootstrap/Main.cs#L1995) / [source](../../godot/GUO/src/Store/ComfyContentProbe.cs#L25) |
| `UO_COMFY_URL` | Runtime/diagnostic source | [source](../../godot/GUO/src/Comfy/ComfyClient.cs#L92) |
| `UO_CONTENT_LOCK` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L1335) / [source](../../godot/GUO/src/Store/StoreDeploymentProbe.cs#L20) / [source](../../godot/GUO/src/Store/StoreDeploymentWindow.cs#L82) |
| `UO_CONTENT_PROBE_COLLISION` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreContentProbe.cs#L53) |
| `UO_CONTENT_PROBE_LAND` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreContentProbe.cs#L48) |
| `UO_CONTENT_PROOF_IMAGE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreContentProbe.cs#L79) |
| `UO_CONTENT_STORE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreDeploymentProbe.cs#L20) / [source](../../godot/GUO/src/Store/StoreDeploymentWindow.cs#L83) / [source](../../godot/GUO/src/Store/StoreOptions.cs#L27) |
| `UO_CUSTOM_DATA` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L1391) |
| `UO_DEPLOYMENT_PROOF_COMPACT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreDeploymentProbe.cs#L18) |
| `UO_DEPLOYMENT_PROOF_IMAGE` | Runtime/diagnostic source | [source](../../godot/GUO/src/Store/StoreDeploymentProbe.cs#L58) |
| `UO_PROBE_SHARD_CONTENT` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PregameProbe.cs#L1556) |
| `UO_PROBE_SHARD_CONTENT_SESSION` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/PregameProbe.cs#L1613) |
| `UO_PROFILE_PLATFORM` | Runtime/diagnostic source | [source](../../godot/GUO/src/Configuration/PlatformDefaults.cs#L223) |
| `UO_SHARD_GM_ACCOUNTS` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L101) |
| `UO_SHARD_GM_PASSWORD` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L107) |
| `UO_SHARD_OWNER` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L92) |
| `UO_SHARD_OWNER_PASSWORD` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/InputProbe.cs#L97) |
| `UO_STORE_URL` | Runtime/diagnostic source | [source](../../godot/GUO/src/Bootstrap/Main.cs#L596) / [source](../../godot/GUO/src/Store/StoreOptions.cs#L18) |
| `XDG_DATA_HOME` | Runtime/diagnostic source | [source](../../godot/GUO/src/Workspace/Workspace.cs#L76) |
