# moge — MoGe sprite-to-splat bridge

One tool: `moge_to_ply.py` runs Microsoft MoGe (monocular geometry
estimation) on a PNG sprite and writes a 3D-gaussian PLY that the client
parses with `src/Assets/SplatPlyParser.cs` (14 float props, `binary_little_endian`).

```
python tools/moge/moge_to_ply.py <sprite.png> --out build/moge/sprite.ply --mask-background
python tools/moge/moge_to_ply.py <sprite.png> --size 256 --model Ruicheng/moge-3-vitl
python tools/moge/moge_to_ply.py <sprite.png> --size 256 --model Ruicheng/moge-2-vitl
python tools/moge/moge_to_ply.py <sprite.png> --out build/moge/s.ply --mask-background --model <local model.pt>
python tools/moge/moge_to_ply.py <sprite.png> --out build/moge/s.ply --mask-background --no-refine
```

Default model is MoGe-3 (`Ruicheng/moge-3-vitl`, ViT-L 370M). Any
`--model` id with a v1/v2 marker (`moge-2-*`, `moge-vitl`, `moge-vitb`,
`moge-vits`) takes the v2 import path (`moge.model.v2`); everything else —
including a local `.pt` whose checkpoint config carries a `refiner` section —
takes the v3 path (`moge.model.v3`). A local v2 `.pt` is detected from its
checkpoint config and still uses v2.

* `--size N` resizes the longest side to N before infer (default 256;
  `0` keeps native). Output maps — and therefore gaussians — come back at
  infer resolution, one gaussian per kept pixel.
* `--mask-background` drops fully-transparent pixels via the PNG alpha.
  Without it, background pixels become black gaussians (MoGe still predicts
  depth for them). MoGe's own validity mask always applies.
* `--model` is a HF repo id or a local `.pt` (default `GUO_MOGE_MODEL`, else
  `Ruicheng/moge-3-vitl`). A local path skips the hub entirely, so
  `HF_HUB_OFFLINE=1` + a cached file works with no network.
* `--refine-steps N` sets MoGe-3's sparse-refinement passes (default
  `GUO_MOGE_REFINE_STEPS`, else 3; `0` skips the refiner); `--no-refine` is
  shorthand for `--refine-steps 0` (fastest, v3 backbone only). Both are
  ignored for v2 models, with a log line when explicitly set.
* `--device cuda` falls back to cpu with a warning; `--resolution-level 0-9`
  (default 9); `--timeout` is the tool-side stage budget (default 600 s).
* Stored frame: iso-billboard, Y-DOWN, bbox-centered, uniform-scaled to
  max-span 2.0 (image-X fanned across world X/Z diagonal, depth flattened).
  The client negates Y at load (== the `Scale(-1,1,1)*RotZ(180)` base
  orientation's net Y flip), giving a Y-up billboard facing the camera at
  yaw 180; the gump shifts it from bbox-center to the tile anchor. Colours are `Dc=(c-0.5)/0.2829` (the renderer's
  `SH_C0`), opacity logit 5.0, isotropic log-scales, identity rotation.
* Every run ends with a self-verify that mirrors `SplatPlyParser.Parse`
  (magic, vertex count, required props, body byte count) plus bbox + opacity
  mass. Exit codes: 0 ok, 1 input/failure, 2 tool-side timeout.

## Environment

| Key | Used by | Default |
|---|---|---|
| `GUO_PYTHON` | the C# caller (which python runs this) | `python` |
| `GUO_MOGE_TOOL` | the C# caller (path to `moge_to_ply.py`) | `<repo>/tools/moge/moge_to_ply.py` |
| `GUO_MOGE_MODEL` | this tool (`--model` wins) | `Ruicheng/moge-3-vitl` |
| `GUO_MOGE_REFINE_STEPS` | this tool (`--refine-steps` / `--no-refine` win) | `3` |
| `HF_HUB_OFFLINE` | huggingface_hub (force cache, no network) | unset |

C# call shape copies `VoiceManager` (`src/IO/Audio/VoiceManager.cs`):
`GUO_PYTHON` + tool path args, `UseShellExecute=false`, stdout/stderr
redirected, `WaitForExit(timeout)` then `Kill`, non-zero exit = tool error.
Timeouts live on both sides: `--timeout` bounds stages here, the caller
holds the hard kill switch (one CUDA kernel cannot be interrupted).

Needs `torch` (CUDA preferred) + `moge==3.0.0`
(`pip install git+https://github.com/microsoft/MoGe.git`, which also pulls
`utils3d_moge`, `pipeline`, `flex-gemm` + `triton-windows`) + PIL/numpy.
MoGe code and the MoGe-3 checkpoints are MIT-licensed (DINOv2 submodule
Apache-2.0); see the script header.
Nothing here writes to the UO client install; outputs go under `build/`.

## Proven (local RTX 4070 box, 2026-10-10)

| Input | Command tail | Result |
|---|---|---|
| real static sprite `static_0x0014.png` 44×48 RGBA | `--mask-background` (+ cached `moge-2-vits-normal`) | 34,789 gaussians, 1,948,545 bytes, bbox ≈ ±1, mass 34,556.2 |
| `build/comfy/proof.png` 512×512 | default (MoGe mask only) | 23,816 gaussians, 1,334,057 bytes |
| `static_0x0014.png` 44×48 → 235×256 infer | `--mask-background` (default MoGe-3, refine 3, warm) | 34,766 gaussians, 1,947,257 bytes, bbox x/z ±0.473 y ±1.0, mass 34,533.3; load 2.8 s, infer 1.2 s, total 6.1 s |
| same sprite | `--mask-background --model Ruicheng/moge-2-vitl` | 34,764 gaussians, 1,947,145 bytes, bbox x/z ±0.493 y ±1.0, mass 34,531.3; load 2.7 s, infer 0.8 s, total 5.4 s |
| same sprite | `--mask-background --no-refine` | 34,766 gaussians, 1,947,257 bytes; infer 0.8 s, total 5.8 s |

MoGe-3 vs v2 on the same sprite: mask-valid 34,769 vs 34,783 pixels
(alpha-kept 34,766 vs 34,764) — parity, not a quality gap on flat pixel
art. First-ever v3 run in a fresh process costs an extra ~5 s of CUDA/
Triton warmup (infer 6.3 s cold vs 1.2 s warm); refine 0/1/3 are 0.8/1.2/
1.2 s warm. A repeat refine-3 run is byte-identical in count/size/bbox
(deterministic).

Both files re-read clean with the independent `tools/comfy/splatlod.py`
reader: exact 14-prop layout, stride 56, `count*56+header == file`,
finite positions, opacity 5.0, isotropic log-scale `ln(0.5/256)` (= `ln(1/512)`, quarter pixel), rot w=1,
DC span ±1.7674 (= ±0.5/0.2829, full black→white).
