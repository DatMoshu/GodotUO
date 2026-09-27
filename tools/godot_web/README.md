# tools/godot_web -- a Godot 4.7.2 mono that can export C# to the web

The pinned engine in `tools/godot` refuses a C# web export (ADR-0008). This
folder holds a **community build that does not**, used only for the web
target. It is third-party, large and gitignored; only this README is tracked.
The owner approved it on 2026-09-27, for this PC only (CI undecided). Why this
build and not another: `docs/web/unblock-report.md`.

| | |
|---|---|
| What | Godot 4.7.2-stable mono + raulsntos's draft PR godotengine/godot#106125 (Mono statically linked into the web template), built by ComplexRobot |
| Release | https://github.com/ComplexRobot/godot-dotnet-web-export/releases/tag/4.7.2-stable (2026-08-18) |
| Source | https://github.com/ComplexRobot/godot/tree/dotnet/mono-static-linking |
| File | `Godot_v4.7.2-stable_mono_web_export_win64.zip`, 166,896,143 bytes |
| SHA-256 | `7e54140fd3e84dfbf676bce56f6a792f1bc7734db36c67d860376b002f1a21d8` |
| Engine banner | `Godot Engine v4.7.2.stable.mono.web_export.34ac8af4b`, Emscripten 6.0.5, multi-threaded, no GDExtension |
| .NET | a private .NET SDK **9.0.318** (Microsoft's `dotnet-sdk-9.0.318-win-x64.zip`, SHA-512 checked against Microsoft's release metadata) with the `wasm-tools` workload (runtime 9.0.20) |

## Layout

```
tools/godot_web/
  README.md                                   tracked; everything else is ignored
  _download/                                  the two zips as downloaded
  Godot_v4.7.2-stable_mono_web_export_win64/  the fork, self-contained (._sc_)
    editor_data/export_templates/4.7.2.stable.mono/web_{debug,release}.zip
  nuget/                                      the fork's GodotSharp / Godot.NET.Sdk 4.7.2 packages
  nuget-packages/                             a private NuGet cache (NUGET_PACKAGES) for web builds
  dotnet/                                     the private .NET 9 SDK + wasm-tools
  browsers/                                   Playwright's Firefox (PLAYWRIGHT_BROWSERS_PATH)
```

## Isolation: why it cannot disturb the pinned engine

The fork's own `install.bat` is **never run**. It would move its web templates
into `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`, the folder the
pinned 4.7.2 uses. It would also add `%APPDATA%\NuGet\nuget` as a machine-wide
package source holding its rebuilt `Godot.NET.Sdk 4.7.2`, with the **same
version number** as the official package, and so shadow it. Instead:

- the editor runs **self-contained** (the `._sc_` file), so its settings and
  templates live in its own `editor_data/`;
- a web build gets `NUGET_PACKAGES=tools\godot_web\nuget-packages`, so the
  fork's 4.7.2 packages never enter the user-wide cache where the official
  ones are;
- `DOTNET_ROOT` and `PATH` point at the private SDK. `dotnet workload` fails
  on this PC's machine-wide install (a missing Visual Studio setup DLL, COM
  0x8007007E) and would need admin rights anyway; the private SDK needs
  neither.

`tools/web/run.py` sets all of this for each command it runs (see
`tools/web/README.md`); nothing is set globally.

## Setting it up again

```
gh release download 4.7.2-stable -R ComplexRobot/godot-dotnet-web-export -D tools\godot_web\_download
certutil -hashfile tools\godot_web\_download\Godot_v4.7.2-stable_mono_web_export_win64.zip SHA256
  (must print the SHA-256 above)
unzip into tools\godot_web\; create an empty ._sc_ next to the .exe;
copy web_debug.zip and web_release.zip into editor_data\export_templates\4.7.2.stable.mono\
  (with a version.txt holding 4.7.2.stable.mono); copy its nuget\ folder to tools\godot_web\nuget\
unzip dotnet-sdk-9.0.318-win-x64.zip into tools\godot_web\dotnet\, then with DOTNET_ROOT set to it:
  tools\godot_web\dotnet\dotnet workload install wasm-tools
set PLAYWRIGHT_BROWSERS_PATH=tools\godot_web\browsers & python -m playwright install firefox
```

## What has been run with it

2026-09-27. `tools/web/hello` (a ten-line C# project) exported with
`--export-debug Web` (67 MB wasm, 43 MB pck) and was served by
`tools\web\run.py serve` with COOP/COEP:

| Browser | Result |
|---|---|
| Chrome, headless, SwiftShader | `crossOriginIsolated=true`; `[GUO] web hello from C#: .NET 9.0.20, Browser, 2 + 2 = 4` after 4.9 s; label and rectangle drawn |
| Chrome, headless, hardware GPU (d3d11) | same, 6.8 s |
| Firefox 132 (Playwright), headless | same, 7.6 s, on ANGLE / D3D11 |

The WebGL black screen reported for 4.7.1 on Chrome (issue #16 of the fork)
did not show up.
