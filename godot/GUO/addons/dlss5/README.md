# DLSS5 addon for GUO (native GDExtension, Windows x64)

Enhances 320x320 center crops through NVIDIA's recovered DLSS 5 neural
rendering network **inside the engine** (no Python, no subprocess).

## What ships here (untracked blobs — provision them, see below)

```
bin/libdlss5.windows.template_release.x86_64.dll   GDExtension (source: DlssGodot demo build)
bin/onnxruntime.dll                                ORT 1.31 native
bin/onnxruntime_providers_cuda.dll                 optional CUDA EP (fallback = CPU)
models/dlssnr-core-320-fp16-vendorsm.onnx          704 MB, still-image path (default)
models/dlssnr-core-320-vendorsm.onnx              1386 MB, fp32 (needed for temporal)
models/dlss_tables.bin                             192 MB, reference constants
```

Sources: `<UO_DLSS_SRC>\DlssGodot` (extension + tables) and
`<UO_DLSS_SRC>\dlss5-standalone\weights` (graphs). Set UO_DLSS_SRC to your
DLSS source checkout (gitignored local config, never committed); see provision.ps1.

## C# usage (see GUO/src/Dlss5/Dlss5Service.cs)

```csharp
var dlss = GUO.Dlss5.Dlss5Service.Shared;
Image out320 = dlss.EnhanceStill(someImage, intensity: 1.0f, profile: 0);
// provider/last-ms:  dlss.Provider / dlss.LastMs
```

The service lazily loads the fp16 graph on first use and reuses it after.

## Screenshot hook

`GameController.TakeScreenshot` saves the raw PNG; when the env
`GUO_DLSS_ENHANCE=1` (or user arg `--dlss-enhance-screenshots`) is set it
also saves `screenshot_..._dlss5.png` enhanced at the 320px crop scale.
Default is OFF (the 1.4 GB models are dev blobs on purpose).

## Dev probe

```
tools\godot\godot.cmd --path godot/GUO --headless \
  --script res://dev/dlss5_probe.gd -- <in.png> <out.png> [intensity] [profile]
```

## Verify the extension loads at all

```
tools\godot\godot.cmd --path godot/GUO --headless --script res://addons/dlss5/dlss5_bridge.gd
```
would show "DLSS5" in ClassDB errors if missing; the probe above is the real test.

## Truths to keep in mind

- Still-image enhancement is a *photographic* filter (material/detail
  reconstruction), NOT temporal super-resolution.
- Temporal (video) path exists and is validated; needs the fp32 graph and
  per-pixel motion — engine readback phase pending (see DlssGodot/GODOT-REPORT.md).
- CUDA needs cudart/cublas on PATH (torch's bundle or a CUDA install);
  otherwise it silently falls back to CPU (~7 s/frame faster-graph 1.4 s CUDA).
