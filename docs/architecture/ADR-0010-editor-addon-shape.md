# ADR-0010: The Editor Is a C# Addon in the Game Assembly, Behind `#if TOOLS`

## Status

Accepted

## Date

2026-09-26

## Last Verified

2026-09-26 (phase 1): `python tools\editor_smoke\run.py --reload` and
`--headless --reload` pass with nine panels (Art, Gumps, Anims, Hues, Multis,
Cliloc, Sounds, Maps, Parity), a sound played before the reload included.

2026-09-26 (phase 0): `python tools\editor_smoke\run.py --reload` and `--headless --reload`
pass on the pinned Godot 4.7.2 mono. The docks load, the install opens through
`UOFileManager` in about 1.2-1.5 s, static 0x0E75 (backpack, 44x32) decodes, and
the addon survives a rebuild-and-reload with the editor open. Export builds
(ExportDebug, ExportRelease) contain no addon type. `launchers\dev\smoke.bat`
passes with the plugin enabled.

## Decision Makers

Project owner (Moshu); GUO-Fable (plan author); GUOEditor session.

## Summary

The GUO editor (docs/editor_plan.md) is a Godot `EditorPlugin` written in C#,
living at `godot/GUO/addons/guo_editor/`, compiled into the one `GUO`
assembly, with every file wrapped in `#if TOOLS`. It reads the client install
through the ported loaders and, from phase 2, draws through the ported
renderer. It is not a second project and not a separate assembly. The plugin
tears itself down before an assembly reload and builds itself again after
one, through `ISerializationListener`.

## Engine Compatibility

| | |
|---|---|
| Engine | Godot 4.7.2 stable, mono |
| APIs | `EditorPlugin.AddDock(EditorDock)` / `RemoveDock` (the 4.6+ dock API; `AddControlToDock` still exists but `EditorDock` carries title, slot, layout and icon itself), `ISerializationListener`, `EditorInterface` |
| Define | `TOOLS` comes from `Godot.NET.Sdk/Sdk.targets`: defined for `Debug` only. `ExportDebug` and `ExportRelease` do not define it |
| Verified | Debug `GUO.dll` contains `GuoEditorPlugin`, `AssetsDock`, `EditorSmoke`; ExportDebug and ExportRelease `GUO.dll` contain none of them |

## ADR Dependencies

- ADR-0001 (render presenter seam) and ADR-0006 (GameController as a node):
  phase 2's world viewer depends on them; this ADR does not change them.
- Future: ADR-0011 (world project overlay), ADR-0012 (live transport),
  ADR-0013 (asset overlay), ADR-0014 (shard world objects and backends) all
  assume the shape decided here.

## Context

### Problem Statement

The editor needs the same code the game runs: the `.mul`/`.uop` readers and
loaders (which use `unsafe` pointer code), later the renderer. Open question 1
of the plan: does Godot 4.7 mono reliably run C# `[Tool]` EditorPlugins from
the game assembly with that unsafe code, and survive the editor's assembly
reloads? And the exported clients (desktop, and the Android and web builds on
`work/android`) must not grow by the editor.

### What was measured

- **It loads.** A `[Tool]` `EditorPlugin` in `GUO.dll` registers two
  `EditorDock`s, opens the install with `new UOFileManager(...).Load()` on a
  worker thread (1.1-1.5 s on this machine), and decodes art through
  `ArtLoader.GetArt`. `AllowUnsafeBlocks` is a project setting and applies to
  the whole assembly; nothing about running in the editor changes it.
- **Reload needs handling, and then works.** With the editor open, a
  rebuild plus focus-in makes GodotTools unload the assembly load context and
  recreate every managed object with its parameterless constructor. It does
  **not** call `_EnterTree` again. A plugin that sets up in `_EnterTree` comes
  back with null fields: the docks stay on screen but nothing drives them.
  First observed by hand (the log showed `Assembly load context unloaded
  successfully` and no second `plugin entered`), then fixed with
  `ISerializationListener`: `OnBeforeSerialize` removes the docks and disposes
  the loaders (which hold memory-mapped files), and `OnAfterDeserialize`
  defers a rebuild. `tools/editor_smoke --reload` now checks this every time:
  the second pass runs against the rebuilt docks and the log must show the old
  context unloading.
- **Headless editors.** `launchers\dev\smoke.bat` and `build.bat` run the
  editor headless to import and build. The plugin registers there but does
  not open the install unless the smoke flag asks for it.
- **The windowed editor rewrites `project.godot` on exit**, dropping its
  comments and the keys that equal defaults. This happens with or without the
  addon. It is not decided here; the smoke tool restores the file so it never
  leaves the tree dirty.

### Constraints

- GUO never writes to `UO_CLIENT_DATA` (CLAUDE.md rule 8; plan §2).
- Pixel art is never filtered: every `TextureRect`, `ItemList` and later
  `SubViewport` the addon makes sets nearest sampling explicitly.
- Hooks into ported code are `// PORT DEVIATION (GUO):` blocks and keep
  `tools/port_drift --strict` green. Phase 0 needed none.
- Config resolves env > `config.bat` > central config, as everywhere else.
  The addon parses `launchers\_shared\config.bat` itself when Godot was not
  started from a launcher, with the same narrow `set "K=V"` grammar as
  `tools/guo/config.py`.

## Decision

### Architecture

```
godot/GUO/addons/guo_editor/        all files #if TOOLS, namespace GUO.Editor
  plugin.cfg                        enabled in project.godot [editor_plugins]
  GuoEditorPlugin.cs                [Tool] EditorPlugin, ISerializationListener
  EditorData.cs                     config + UOFileManager + art decode
  EditorSmoke.cs                    self-check, present only with --guo-editor-smoke
  Docks/AssetsDock.cs               EditorDock: one tab per AssetPanel
  Docks/InspectorDock.cs            EditorDock: stills, animation playback, text, buttons
  Panels/AssetPanel.cs, GridPanel.cs   the panel contract; paged, searchable id lists
  Panels/Inspection.cs              what a panel hands the inspector
  Panels/{Art,Gump,Animation,Hue,Multi,Cliloc,Sound,Map,Parity}Panel.cs
  World/WorldView.cs, WorldHost.cs  the UO World main-screen tab (ADR-0015)
  Overlay/WorldProject.cs           the world project overlay (ADR-0011)
godot/GUO/src/Editor/               runtime-side hooks, when a phase needs one
tools/editor_smoke/run.py           drives the editor and reads the report
launchers/editor/open_project.bat   builds C#, then opens the editor
launchers/dev/editor_smoke.bat
```

### Key Interfaces

- `EditorData`: `LoadAsync()`, `Loaded` event (main thread), `Files`
  (the `UOFileManager`), `HasArt`, `NameOf`, `ArtImage` (an `Image` decoded by
  `ArtLoader.GetArt`), `Setting(key, fallback)`.
- `AssetPanel`: `OnDataLoaded()`, `Search(text)` (scripted use, returns the
  id selected), `Inspect` event carrying an `Inspection`, `SmokeQuery`.
- `AssetsDock.Inspect` forwards every panel's `Inspect`;
  `InspectorDock.ShowInspection(Inspection)` shows it. (Phase 0 had
  `ArtSelected(uint)` / `ShowArt(uint)`; phase 1 generalised them.)
- Command line after `--`: `--guo-editor-smoke <dir>`,
  `--guo-editor-smoke-art <id>`, `--guo-editor-smoke-reload`.

### Implementation Guidelines

1. Every file in the addon is wrapped in `#if TOOLS` ... `#endif`, including
   helpers that are not Godot types.
2. Every Godot type in the addon is `[Tool]`, `partial`, and has a
   parameterless constructor (the reload recreates objects through it).
3. Anything built in `_EnterTree` is torn down in `OnBeforeSerialize` and
   rebuilt from `OnAfterDeserialize` (deferred). Open files are closed on
   teardown.
4. The loaders are single threaded. Load on a worker, touch them only after
   `Loaded` fires, on the main thread.
5. No addon type is referenced from game code. The dependency runs one way:
   the addon uses the port.

## Alternatives Considered

### Alternative 1: A separate Godot project for the editor

A `godot/GUOEditor/` project referencing `GUO.csproj`. The exports are
naturally clean, but it means two projects, two imports and two sets of
settings, and the world viewer in phase 2 would need the game's scenes,
shaders and resources duplicated or shared by path. Rejected: the plan's
point is that the editor shows what the client shows, which is easiest when
it is the same project.

### Alternative 2: A second C# assembly in the same project

Godot 4 builds one project assembly per Godot project. Extra assemblies are
possible as referenced class libraries but cannot hold Godot script classes
the editor instantiates from `plugin.cfg`. Rejected as unsupported.

### Alternative 3: GDScript editor plugin calling into C#

GDScript for the UI and C# for the data. Cross-language calls for every tile
in a grid, two languages for one feature, and the renderer in phase 2 is C#
anyway. Rejected.

## Consequences

### Positive

- The editor reads through the ported loaders. What the Assets dock shows is
  what `ArtLoader` returns, including GUO's own art-file layer in `GetArt`.
- One build: `dotnet build` builds the game and the editor together.
- Exports carry no editor code (verified per configuration above).

### Negative

- The addon's `.cs` files and `plugin.cfg` are still resources in the
  project, so an export packs them (a few tens of KB of text, no code runs).
  An export preset can drop them with `exclude_filter="addons/guo_editor/*"`;
  presets are per machine (`export_presets.cfg` is gitignored), so this is a
  recommendation to whoever owns each preset, not enforced here.
- Every rebuild with the editor open reopens the install (about 1.5 s).
- **Opening the editor loses `project.godot`'s comments.** The windowed
  editor re-serializes `godot/GUO/project.godot` on exit: it drops every
  comment (the rationale for `default_texture_filter=0`, the stretch-mode
  explanation, the editor-plugin note this ADR added) and the keys whose value
  is the default (`window/stretch/mode="disabled"`,
  `renderer/rendering_method`). Measured with and without the addon: it is
  the editor, not the plugin. Values are unchanged, so behaviour is not
  affected, but the documentation in that file is lost the first time anyone
  opens the editor and saves. Pending the owner's decision on where that
  rationale should live, the file is left as authored, `tools/editor_smoke`
  restores it after every run, and a hand-run editor's rewrite must not be
  committed.
- Editor code shares the game's namespace root and warnings settings.

### Neutral

- The plugin is enabled in `project.godot`, so a fresh clone opens with the
  docks, provided the C# is built first (`open_project.bat` does that).

## Risks

| Risk | Mitigation |
|---|---|
| A future object the addon keeps (a delegate into a static, a native thread) pins the old assembly, so the reload fails and the editor must restart | `editor_smoke --reload` fails on `Failed to unload assemblies` or a missing unload line |
| A reload lands while the install is still loading | `EditorData.Dispose` waits up to 30 s for the load task before closing the files |
| Editor code leaks into game code | Rule 5 above; the ExportRelease check in this ADR's verification |

## Performance Implications

Opening the install costs 1.1-1.5 s on a worker thread at editor start and
after each reload. The Assets dock decodes only the page on screen (240
icons). The full static range is scanned for presence (index lookups, no
decode) when a search runs: 38,286 statics with art in this install.

## Migration Plan

None; this is new. `launchers\editor\open.bat` is renamed
`open_project.bat` (nothing referenced the old name) and now builds first.

## Validation Criteria

- `python tools\editor_smoke\run.py` passes (windowed, screenshot).
- `python tools\editor_smoke\run.py --headless --reload` passes.
- `dotnet build -c ExportRelease` output contains no `GUO.Editor` type.
- `launchers\dev\smoke.bat` passes with the plugin enabled.

## GDD Requirements Addressed

None: GUO is a port and has no GDDs. The requirements are
`docs/editor_plan.md` §4.1, §5 phase 0 and §9 question 1.

## Related

- `docs/editor_plan.md`
- `tools/editor_smoke/README.md`
- ADR-0001, ADR-0006
