# tools/msbuild

`UpstreamDir.props`: where the ClassicUO checkout is, for every C# project that
references `cuoapi.dll` or upstream's asset loaders (`godot/GUO/GUO.csproj`,
`tools/plugin_host`, `tools/plugin_probe/managed`, `tools/guoasset`). First hit
wins, in the same order as the launchers and `tools/guo/config.py`:
`-p:UpstreamDir`, `UO_UPSTREAM_DIR`, this checkout's `sources/ClassicUO`, then,
in a git worktree, the main checkout's, read with MSBuild property functions
(no git, no links).

`CuoApiAnyCpu.targets`: on a non-Windows arm64 host (Apple Silicon, Linux arm64),
`godot/GUO/GUO.csproj` references a copy of upstream's `cuoapi.dll` with the COFF
Machine field set to I386, so the IL-only assembly loads as AnyCPU instead of being
refused as x64 code. Metadata and IL are unchanged; the task refuses any input that
is not IL-only. Windows never runs it.

## Run

```
<Import Project="..\..\tools\msbuild\UpstreamDir.props" />   in a .csproj; nothing to run
```

## Tests

No tests of its own. Any `dotnet build godot\GUO\GUO.csproj` in a worktree exercises it.
