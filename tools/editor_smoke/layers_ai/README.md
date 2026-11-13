# Layers AI boundary native checks

Run the no-filesystem-probe CLI refusal controls first:

```
python tools/editor_smoke/layers_ai/test_runner.py -v
```

Build the production Debug assembly, then run the actual dock with synthetic
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

The new `--out` must be outside the checkout. Operational projects, compiler
caches and the isolated engine live under the OS temp directory and are kept
for independent repeat; their paths, exact source/DLL/engine hashes, loaded
module ID, owned process IDs, raw logs and assertions are recorded in the run.
The supplied DLL can be selected with `--assembly`; production code is never
stubbed or replaced. Preserve inherited layer-root and preference state.
