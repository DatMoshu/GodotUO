# DLSS 5 in GUO — integration & usage guide

Native DLSS 5 neural rendering (recovered NVIDIA network, runs plain
ONNX Runtime — no NGX, no engine patches) inside Godot 4.7.2-mono / GUO.

This file is the human-facing summary; scripts live in `godot/GUO/addons/dlss5/`.

## TL;DR

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File godot\GUO\addons\dlss5\verify.ps1
# expect: PROBE-OK net_ms=~2000 provider=cuda  → VERIFY-PASS
```

For screenshots in the running game:

```powershell
$env:GUO_DLSS_ENHANCE = "1"   # or launch with --dlss-enhance-screenshots
launchers\game\play.bat              # ...then PressScreen in-game
# Data\Client\Screenshots\  now also contains *_dlss5.png (640px enhanced crop)
```

Calling from your own C# (two lines):

```csharp
Image square = GUO.Dlss5.Dlss5Service.PrepareSquare320(viewportImg);  // center crop -> 320 RGB
Image enhanced = GUO.Dlss5.Dlss5Service.Shared.EnhanceStill(square, intensity: 1.0f, profile: 0);
```

## What was added to the repo

| Path | What |
|---|---|
| `godot/GUO/addons/dlss5/` | runtime: GDExtension dll, ONNX Runtime native, both neural graphs (fp16 704 MB + fp32 1.4 GB), 192 MB reference tables, GDScript bridge, README. Blobs are `.gitignore`d — provision them (below). |
| `godot/GUO/src/Dlss5/Dlss5Service.cs` | the C# API (lazy load, CUDA→CPU fallback handled natively, provider/ms/error surfacing) |
| `godot/GUO/src/Client/GameController.cs` | opt-in screenshot hook (default **off**; raw screenshot is always saved first) |
| `godot/GUO/dev/dlss5_probe.gd` | headless probe (`--script res://dev/dlss5_probe.gd -- <in.png> <out.png> [intensity] [profile]`) |
| `godot/GUO/addons/dlss5/{provision,verify}.ps1` | one-command setup / verification |

## One-time provisioning (done; for fresh machines/CI)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File godot\GUO\addons\dlss5\provision.ps1
```

Copies blobs from `<UO_DLSS_SRC>\{DlssGodot,dlss5-standalone}` (source workspace, set UO_DLSS_SRC in gitignored local config),
runs the one-time headless editor scan (`.godot/extension_list.cfg` must learn the addon),
then verifies. Requires that source workspace to exist; on other machines copy the
`addons/dlss5/bin` + `models` blobs over first, then run provision (scan step still needed once).

## C# surface (full)

```csharp
var svc = GUO.Dlss5.Dlss5Service.Shared;

svc.EnsureLoaded();                 // optional; EnhanceStill auto-loads (fp16 graph)
svc.Provider                        // "cuda" | "cpu"
svc.LastMs                          // wall ms of last enhance call
svc.LastError                       // "" when fine

svc.EnhanceStill(img, intensity, profile)  // img must be exactly 320x320; returns enhanced Image or null (+LastError)
GUO.Dlss5.Dlss5Service.PrepareSquare320(anyImage)  // center-crop + resize + RGB8 (static helper)

// GUI_HOOK: screenshot sidecar enhancement is flag-gated:
GUO.Dlss5.Dlss5Service.EnhanceScreenshotsEnabled  // env GUO_DLSS_ENHANCE=1 or --dlss-enhance-screenshots
```

Profiles: 0 standard, 1 natural, 2 cinematic, 3 neutral. Intensity: 0–1
normal (recommended; >1 clamps internally the DlssGodot demo supports 0–2 —
the service clamps nothing; treat 1.0 as the honest default).

## Performance (RTX 4070, measured)

| Path | provider | per-frame |
|---|---|---|
| still fp16 graph | cuda | ~1.7–2.3 s (320 crop; slow first call warms kernels) |
| still fp16 graph | cpu | ~13 s |
| temporal fp32 graph | cuda | ~1.4 s + history (validated; offline probe green) |

Nudge: CUDA runtime libs must be on PATH (the PyTorch bundle works: prepend
`D:\python312\Lib\site-packages\torch\lib` to PATH, verify.ps1 does this for
its own probe). No CUDA on PATH → automatic silent CPU fallback; check
`svc.Provider` after first call to know which one you got.

## Temporal path (history + motion vectors), available today

Engine-side state machine (keep one `DLSS5` instance per enhanced view):

```gdscript
var dlss = load("res://addons/dlss5/dlss5_bridge.gd").new().call("make")  # or via Dlss5Service once exposed
dlss.load_model(<fp32 onnx>, <tables>, <ort_dir>)
dlss.temporal_reset()                       # on scene cut / new shot
var out = dlss.enhance_temporal(img320, motion_rgf)  # motion: FORMAT_RGF 320x320, pixel current->previous
```

The fp16 graph does NOT serve temporal yet (service returns an error);
use the fp32 graph for that. Depth-guided sampling and scene-cut detection
land in the next phase (see `<UO_DLSS_SRC>\DlssGodot\GODOT-REPORT.md`).

## Troubleshooting

| Symptom | Fix |
|---|---|
| `provider=cpu` unexpectedly | CUDA runtime libs not on PATH (see above); also ensure `bin/onnxruntime_providers_shared.dll` + `_cuda.dll` are in the addon bin dir |
| `DLSS5` class not found first run after copying the addon | run one `--headless --editor --quit` pass or `provision.ps1` (extension list refresh) |
| `PluginHost` build errors re `cuoapi` | pre-existing repo state; build with `UO_UPSTREAM_DIR` set to your ClassicUO checkout |
| Enhance returns null | read `svc.LastError`; the usual case is image size/format (must be 320×320 RGB8 after `PrepareSquare320`) |
| Probe "file not found" | `--path` must point at the GUO project dir, and the addon must be provisioned |
