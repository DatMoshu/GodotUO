# Terrain layer path checks

Links the production resolver directly, without Godot or game data. Run with a
new synthetic local fixture folder outside the checkout:

```
dotnet run --project tools/editor_smoke/terrain_paths/TerrainPaths.csproj --artifacts-path <external-build-folder> -- <new-fixture-folder>
```

This checks lexical rejection before root handling, valid Unicode/spaces/legacy
separators, root boundary algebra, parent files, trusted redirected roots and
descendant links. It retains small fixtures. Unsupported symlink creation is
reported as UNTESTED. Network/device spellings are lexical tests only; no real
network root or user files are accessed. Native PNG/editor/consumer checks are
separate; these assertions do not establish a hostile-process sandbox.

The native harness runs the actual production reader and Layers dock by reflection,
with AI disabled and without loading the game/editor plugin or client install.
It builds a small separate Godot editor project beneath a fresh external output
folder. It records the production/probe/source hashes, logs, and assertion result.
No production methods are stubbed. Build the normal Debug client first, then run:

```
python tools/editor_smoke/terrain_paths/run_native.py --out <new-external-run>
python tools/editor_smoke/terrain_paths/run_native.py --mode control --control-ref <frozen-git-ref> --out <new-control-run>
python tools/editor_smoke/terrain_paths/run_native.py --prepared-root <first-run>/result/layers --out <new-cold-run>
```

The reader control compiles the exact frozen `TerrainLayers.cs` Git blob. It
demonstrates synthetic local outside PNG decoding and linked-manifest reads/writes;
it does not test the old editor decal method. Network spellings never reach control
I/O. The fixed harness covers native import callbacks, deferred dialog cleanup,
byte-exact imports/overwrites, invalid-image and rejected-save form/model retention,
decal paths, manifest/parent/leaf/dangling links, and trusted redirected roots.
File/dir symlink creation must be supported; failures fail this harness rather
than silently counting as passes. ACL-denial and other platforms remain untested.

`--assembly <GUO.dll>` pins a particular built Debug assembly. Cold mode reads a
previous synthetic fixture in a new process. Add `--windowed` to cold mode for
captures of the dock's own editor viewport; it uses a no-focus window. View the
captures before claiming a UI result. These tests do not establish game rendering,
default/off-state parity, exported-client behavior or editor hot reload. Run the
normal project smoke and appropriate native consumer checks separately.
