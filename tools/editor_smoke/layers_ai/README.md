# Layers AI boundary native checks

Run the no-filesystem-probe CLI refusal controls first:

```
python tools/editor_smoke/layers_ai/test_runner.py -v
```

Prerequisites: the repository launcher configuration must resolve through
`guo.load_config` (including the shared main-checkout engine/upstream fallback
when using a worktree), .NET 8 with the Godot.NET.Sdk/4.7.2 package, and the pinned
Godot 4.7.2 mono engine. Capture additionally requires configured, readable
client data; it is read in place. `--assembly` selects an existing production
Debug `GUO.dll`, with `GUO.ModApi.dll` and `cuoapi.dll` alongside it. The runner
compiles only its tiny proof, without rebuilding that supplied DLL.

Build the production Debug assembly when needed, then run the actual dock with synthetic
PNGs, a local workflow catalog and an in-memory `IImageProvider` fake:

```
dotnet build godot/GUO/GUO.csproj
python tools/editor_smoke/layers_ai/run_native.py --out <new-external-run>
python tools/editor_smoke/layers_ai/run_native.py --out <another-new-external-run> --mode plugin --persisted-preference --capture
```

The component matrix checks initial/direct off discovery and factory counters,
manual registry/import/metadata and staged pixel/alpha preservation, fake success,
cooperative cancellation, late success/error/finalizer suppression after off/on,
capture replacement, provider error/empty/malformed/cancel retry, and double
Shutdown plus defensive ExitTree. It calls the same runner as Generate.

Plugin mode uses the actual `GuoEditorPlugin`, real EditorSettings signal and
same-assembly teardown/rebuild. It copies the pinned engine and GodotSharp into
an operational temp directory with Godot's `_sc_` self-contained marker, seeds
false in one process and checks the persisted preference on a cold process.
Host editor settings are untouched. AI tabs stay hidden to prevent unrelated
visibility-driven discovery. No real AI provider, model, generation or service
is used; no server interrupt is sent. `--capture` reads the configured client
data in place and uses a separate workspace to check actual capture frame-await
lifetimes and headless no-image recovery. It does not qualify captured pixels.

Raw native logs may contain unrelated shader/resource/layout diagnostics from
the tiny project and existing plugin teardown. The verdict covers the listed
assertions only; it does not qualify whole-editor readiness, assembly hot reload,
render parity, performance, a new exported client, or an AI-off capture-to-decal
workflow without a previously generated image. Whole-editor reload remains a
separate gate.

The new `--out` must be outside the checkout. Each invocation creates a unique
`<OS-temp>/guo-layers-ai-<random>/` scratch root. Set the OS temp location before
starting Python if operational output must use a particular drive. The layout is:

- `godot/GUO/`: proof project, `addons/proof`, and `.godot/mono/temp` compiler output.
- `build/editor_world/`: unmodified WorldHost's `res://../../build/editor_world`,
  including its profiles and `Data/Client` tables; never the shared OS-temp `build`.
- `build/` and `workspace/`: explicit child `UO_BUILD_DIR` and `UO_WORKSPACE_DIR`.
- `engine/`: plugin-mode self-contained engine, GodotSharp and `editor_data/`
  persisted settings; its location does not depend on the proof project's depth.

The dotnet build receives an absolute csproj path; both build and native children
use `godot/GUO` as their explicit working directory. Godot receives the same
absolute project via `--path`. Assembly HintPaths stay absolute. Native
`--proof-out` stays under the requested evidence output: synthetic layers,
workflow files, staged PNGs and `result.json` are written there. Both probes set
`GUO_LAYER_DIR` to that result's `layers` directory and restore its inherited value.
`layout.json` records exact scratch/project/build/workspace/engine/evidence paths
before compilation (including failed builds); successful `identity.json` also
includes that layout. Existing production hashes and native assertions are retained.

The focused Python controls prepare both modes without executing dotnet or Godot.
They derive the relative scratch relationship from the actual WorldHost source,
check containment and distinct roots across invocations, and inspect child cwd,
project/plugin paths, output records and build/workspace overrides. Native proof
is still required after these controls pass.

Ambient-environment audit (proposal only; no broad filtering in this correction):
`os.environ.copy()` remains the child baseline. The runner overrides build,
workspace, scripted-editor, configured client data/version and compiler reuse;
it removes editor MCP port/token. Source review identifies `UO_WORLD_PROJECT`
as an external overlay/staging selector, `GUO_LAYER_DIR` as a layer root,
`UO_SHARD_DIST` as a shard metadata selector, `UO_AGENT_QUEUE`/`GUO_AI_HOME` as
AI queue/session paths, `UO_COMFY_URL`/`UO_COMFY_WORKFLOWS` as provider/catalog
selectors, and `UO_PYTHON`/`PYTHON` as tool selectors. These must not be assumed
isolated merely because WorldHost's path is fixed. No values or secrets are logged.

Proposed follow-up: construct child UO_/GUO_ settings from a minimal allowlist
of configured read-only `UO_CLIENT_DATA`, `UO_CLIENT_VERSION` and optional
`UO_LANGUAGE`, plus runner-owned build/workspace/scripted values. Give world,
layer and AI queue/session paths explicit scratch locations; omit provider,
MCP and shard connection selectors. Preserve the OS/.NET environment needed by
the pinned engine and SDK. Review this separately before changing ambient
behavior; source-side `load_config` remains an explicit prerequisite. The current
native matrix uses only synthetic providers, with unrelated AI tabs hidden;
it does not prove arbitrary inherited environments safe.
Operational projects, compiler
caches and the isolated engine live under the OS temp directory and are kept
for independent repeat; their paths, exact source/DLL/engine hashes, loaded
module ID, owned process IDs, raw logs and assertions are recorded in the run.
The supplied DLL can be selected with `--assembly`; production code is never
stubbed or replaced. Preserve inherited layer-root and preference state.
