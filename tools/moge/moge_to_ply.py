#!/usr/bin/env python3
"""MoGe sprite -> 3D-gaussian PLY bridge for GUO.

Runs Microsoft MoGe (monocular geometry estimation) on one PNG sprite and
writes a binary_little_endian gaussian PLY the client parses with
godot/GUO/src/Assets/SplatPlyParser.cs (14 float props, Wanted order).

    python tools/moge/moge_to_ply.py <sprite.png> --out build/moge/sprite.ply --mask-background
    python tools/moge/moge_to_ply.py <sprite.png> --size 256 --model Ruicheng/moge-3-vitl
    python tools/moge/moge_to_ply.py <sprite.png> --size 256 --model Ruicheng/moge-2-vitl
    python tools/moge/moge_to_ply.py <sprite.png> --refine-steps 0 --mask-background  # fast, no refinement
    python tools/moge/moge_to_ply.py <sprite.png> --no-refine --mask-background       # same as --refine-steps 0

MoGe license: MIT (github.com/microsoft/MoGe/blob/main/LICENSE); the DINOv2
code under moge/model/modules/dinov2 is Meta's Apache-2.0 release. MoGe-3
checkpoints (Ruicheng/moge-3-vitl, ViT-L 370M, model.pt ~1.48GB) are MIT
like the code. MoGe is
never vendored here: it is pip-installed like torch
(`pip install git+https://github.com/microsoft/MoGe.git`), same as every
other heavy model in this repo (ComfyUI, Ollama, HF hubs).

Repo conventions copied from tools/comfy/voice.py + run.py:
  * resolution order for settings: CLI flag -> GUO_MOGE_* env -> default
    (mirrors env -> config.local.bat -> config.bat; see tools/guo/config.py).
  * `[moge]` log prefix, outputs under build/ (gitignored), never writes to
    the UO client install.
  * timeouts live on BOTH sides (VoiceManager pattern): --timeout is this
    tool's stage-boundary budget, and the C# caller holds the hard kill
    switch (Process.WaitForExit + Kill). A single CUDA kernel cannot be
    interrupted, so the caller must enforce the wall clock.

C# call shape (mirrors VoiceManager.DesignVoice/Synthesize, which read
GUO_VOICE_TOOL + GUO_PYTHON):
  * python:  Environment.GetEnvironmentVariable("GUO_PYTHON") or "python"
  * tool:    Environment.GetEnvironmentVariable("GUO_MOGE_TOOL") or
    <repo>/tools/moge/moge_to_ply.py
  * model:   Environment.GetEnvironmentVariable("GUO_MOGE_MODEL") or default
  * args:    "<tool>" "<in.png>" --out "<out.ply>" --mask-background
             --model "<model>" --timeout <seconds>
  * process: UseShellExecute=false, redirect stdout+stderr, CreateNoWindow,
    WaitForExit(timeout*1000) then Kill, non-zero exit = tool error.

Coordinate frame (READ-ONLY refs, never edited):
  * MoGe returns points in OpenCV camera coords: x right, y DOWN, z forward.
  * FindDimetricProjection (sources-adjacent ClassicUO-JAR, Program.cs
    ~226-228) documents the pipeline base orientation as
    Scale(-1,1,1) * RotZ(180): RotZ(180) maps (x,y)->(-x,-y), Sx=-1 maps x
    back, net effect (x,y)->(x,-y), i.e. a Y flip. Its Sz variants showed
    flattening depth (Sz=0) reprojects cleanest; any depth mixing breaks
    bbox centering (below) and shears tall art taller (sy gains -mx).
  * This tool stores an iso-billboard, Y-DOWN, centroid on the art bbox
    center (not the mean: tall skewed density would shift a mean center),
    uniform-scaled to max-span 2.0. Image-X fans across world X/Z diagonal
    (wx=-dx/2, wz=+dx/2) and depth is flattened (Sz=0), so the yaw=180
    oblique map reproduces the sprite (sx=dx, sy=dy): a front view at the
    pipeline yaw, like every other file in the pipeline.
    SplatPlyParser.Normalize negates Y at load -- that negate IS the base
    orientation's net Y flip -- yielding a Y-up billboard facing the camera
    at yaw 180. The gump then shifts it from bbox-center to the tile anchor
    (sprite center vs RSP), which a PLY cannot carry (the parser recenters).
  * Colours use the renderer's exact constant (SplatBatcher.SH_C0 = 0.2829f,
    splat_2d.gdshader): color = 0.5 + 0.2829*Dc, so Dc = (color-0.5)/0.2829.
  * Opacity logit 5.0 and identity rotation (1,0,0,0) match the pipeline's own
    probe (BatcherProbe.SplatProofPly); scales are stored LOG (the parser
    applies Exp), one isotropic sigma = QUARTER of an infer pixel in stored units
    (sigma 0.5 overcovers the dense billboard: its 3-sigma quad lands at 2.6x
    the infer spacing with ~54% neighbour bleed, so detail reads as half
    resolution; 0.25 lands at 1.3x spacing with ~10% bleed, still continuous;
    see build_rows).
"""

from __future__ import annotations

import argparse
import math
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent

# The client's required props, in order (SplatPlyParser.Wanted). The parser
# maps by name, but canonical order keeps files diffable (BatcherProbe too).
PROPS = ("x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
         "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3")

SH_C0 = 0.2829  # must equal SplatBatcher.SH_C0; inverse of the shader decode
OPACITY_LOGIT = 5.0  # sigmoid(5) ~= 0.993, matches BatcherProbe's proof row
ROW_BYTES = len(PROPS) * 4

DEFAULT_MODEL = "Ruicheng/moge-3-vitl"  # reference MoGe-3; override per call
DEFAULT_REFINE_STEPS = 3  # MoGe-3 sparse-refinement passes (0 skips the refiner)


def is_v3_model(model_id: str) -> bool:
    """True when the id selects the v3 import path (moge.model.v3).

    Explicit v3 markers win; explicit v1/v2 markers fall back to v2; a
    local .pt or anything unrecognised defaults to v3 (the new default
    model). The v2 fallback keeps `--model Ruicheng/moge-2-vitl` (and the
    v1 `moge-vitl` / vitb / vits ids) working on the old import path."""
    low = model_id.lower().replace("_", "-")
    if "moge-3" in low or "moge3" in low:
        return True
    if ("moge-2" in low or "moge2" in low or "moge-vitl" in low
            or "moge-vitb" in low or "moge-vits" in low):
        return False
    return True


def repo_root() -> Path:
    """Repo root by marker, mirroring guo.config.find_repo_root (no import)."""
    for c in [HERE, *HERE.parents]:
        if (c / "launchers" / "_shared" / "config.bat").is_file():
            return c
    return Path.cwd()


def resolve_model(override: str | None) -> str:
    if override:
        return override
    import os
    return os.environ.get("GUO_MOGE_MODEL") or DEFAULT_MODEL


def resolve_refine_steps(override: int | None, no_refine: bool) -> int:
    if no_refine:
        return 0
    if override is not None:
        return override
    import os
    raw = os.environ.get("GUO_MOGE_REFINE_STEPS")
    if raw is not None and raw != "":
        try:
            return int(raw)
        except ValueError:
            print(f"[moge] bad GUO_MOGE_REFINE_STEPS={raw!r}, using {DEFAULT_REFINE_STEPS}")
    return DEFAULT_REFINE_STEPS


def check_deadline(stage: str, deadline: float) -> None:
    if time.time() > deadline:
        raise TimeoutError(f"budget exceeded before {stage} "
                           f"(caller kills the process; see header)")


def load_image(path: Path, size: int) -> tuple:
    """Returns (rgb01 (H,W,3) float32, alpha (H,W) float32, (w,h)) at infer res."""
    from PIL import Image
    import numpy as np
    img = Image.open(path).convert("RGBA")
    w0, h0 = img.size
    if size > 0 and max(w0, h0) != size:
        k = size / max(w0, h0)
        img = img.resize((max(1, round(w0 * k)), max(1, round(h0 * k))),
                         Image.BILINEAR)
        print(f"[moge] resize {w0}x{h0} -> {img.size[0]}x{img.size[1]} (bilinear)")
    a = np.asarray(img).astype("float32") / 255.0
    return a[..., :3].astype("float32"), a[..., 3].astype("float32"), img.size


def run_infer(rgb01, model_id: str, device: str, fp16: bool,
              resolution_level: int, refine_steps: int, deadline: float):
    import torch
    v3 = is_v3_model(model_id)
    # A local .pt carries its own config: a v2 checkpoint has no refiner, so
    # a v2 file passed explicitly still takes the v2 path (the id heuristic
    # cannot tell local files apart). Peek at model_config only, cheap vs load.
    from pathlib import Path as _P
    if _P(model_id).exists():
        try:
            _ckpt = torch.load(model_id, map_location="cpu", weights_only=True)
            _cfg = _ckpt.get("model_config", {}) if isinstance(_ckpt, dict) else {}
            if "refiner" not in _cfg and v3:
                print("[moge] local checkpoint has no refiner config, using v2 path")
                v3 = False
        except Exception as ex:
            print(f"[moge] config peek failed ({type(ex).__name__}), keeping "
                  f"{'v3' if v3 else 'v2'} path")
    if v3:
        from moge.model.v3 import MoGeModel
    else:
        from moge.model.v2 import MoGeModel
    has_cuda = torch.cuda.is_available()
    if device == "cuda" and not has_cuda:
        print("[moge] CUDA unavailable, falling back to cpu (slower, same output)")
        device = "cpu"
    dev = torch.device(device)
    check_deadline("model load", deadline)
    t0 = time.time()
    model = MoGeModel.from_pretrained(model_id)
    model.to(dev)
    model.eval()
    tag = "v3" if v3 else "v2"
    print(f"[moge] model {model_id} ({tag}) on {dev} ({time.time() - t0:.1f}s)")
    check_deadline("infer", deadline)
    t0 = time.time()
    img = torch.from_numpy(rgb01).permute(2, 0, 1)  # (3,H,W), RGB 0..1
    infer_kw = dict(resolution_level=resolution_level,
                    apply_mask=True,
                    use_fp16=fp16 and dev.type == "cuda")
    if v3:
        infer_kw["refine_steps"] = refine_steps
        print(f"[moge] refine_steps={refine_steps}")
    elif refine_steps != DEFAULT_REFINE_STEPS:
        print(f"[moge] --refine-steps ignored for v2 model {model_id}")
    out = model.infer(img, **infer_kw)
    dt = time.time() - t0
    pts = out["points"]
    if pts.dim() == 4:
        pts = pts[0]
    mask = out.get("mask")
    if mask is not None:
        if mask.dim() == 3:
            mask = mask[0]
        mask = (mask > 0.5).cpu().numpy()
    print(f"[moge] infer {tuple(pts.shape)} in {dt:.1f}s")
    return pts.cpu().numpy(), mask


def build_rows(points, rgb01, alpha, mask, mask_background: bool):
    import numpy as np
    h, w, _ = points.shape
    keep = np.ones((h, w), bool) if mask is None else mask.astype(bool)
    n_moge = int(keep.sum())
    if mask_background:
        keep &= alpha > 0.0
    ys, xs = np.nonzero(keep)
    n = len(xs)
    print(f"[moge] keep {n} of {h * w} pixels "
          f"(moge-valid {n_moge}, alpha-kept {n})")
    if n == 0:
        raise ValueError("no gaussians survived masking "
                         "(try without --mask-background)")
    p = points[ys, xs].astype(np.float64)  # x right, y DOWN, z forward
    c = rgb01[ys, xs].astype(np.float64)
    # Iso-billboard: bbox center (tall skewed density must not pull it),
    # image-X across the yaw=180 diagonal, depth flattened (Sz=0).
    plo = p.min(axis=0)
    phi = p.max(axis=0)
    dx = p[:, 0] - float((plo[0] + phi[0]) * 0.5)
    dy = p[:, 1] - float((plo[1] + phi[1]) * 0.5)
    qx = -0.5 * dx
    qy = dy  # stored Y-DOWN; the parser negate makes it Y-up
    qz = 0.5 * dx  # MoGe depth ignored: relief along the viewing ray would
    # mix axes, break the bbox centering the parser relies on, and shear
    # tall art (unbaked sy carries -mx, so height gains the width).
    span = float(max(qx.max() - qx.min(), qy.max() - qy.min(),
                     qz.max() - qz.min()))
    if span < 1e-6:
        span = 1.0
    k = 2.0 / span  # uniform scale to a 2-unit cube, parser convention
    xyz = np.stack([qx * k, qy * k, qz * k], axis=1)
    sigma = 0.25 * 2.0 / max(w, h)  # quarter of an infer pixel, in stored units
    log_sigma = math.log(sigma)
    dc = (c - 0.5) / SH_C0
    rows = np.empty((n, len(PROPS)), dtype="<f4")
    rows[:, 0:3] = xyz.astype("<f4")
    rows[:, 3:6] = dc.astype("<f4")
    rows[:, 6] = OPACITY_LOGIT
    rows[:, 7:10] = log_sigma
    rows[:, 10] = 1.0  # identity quaternion (w,x,y,z)
    rows[:, 11:14] = 0.0
    lo = xyz.min(axis=0).tolist()
    hi = xyz.max(axis=0).tolist()
    return rows, lo, hi


def write_ply(path: Path, rows) -> None:
    head = ["ply", "format binary_little_endian 1.0",
            f"element vertex {len(rows)}"]
    head += [f"property float {p}" for p in PROPS]
    head.append("end_header")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(("\n".join(head) + "\n").encode("ascii") + rows.tobytes())
    print(f"[moge] {len(rows)} gaussians -> {path} ({path.stat().st_size} bytes)")


def verify_ply(path: Path) -> dict:
    """Mirror of SplatPlyParser.Parse validation: header magic, vertex count,
    the 14 required float props, body byte count. Returns count/bbox/mass
    (mass = sigmoid(opacity) sum, the parser's Normalize formula)."""
    import numpy as np
    raw = path.read_bytes()
    end = raw.index(b"end_header\n") + len(b"end_header\n")
    lines = raw[:end].decode("ascii").splitlines()
    assert lines[0] == "ply", "missing ply magic"
    assert "binary_little_endian" in lines[1], "not binary_little_endian"
    count, props = 0, []
    in_vertex = False
    for line in lines[2:]:
        parts = line.split()
        if parts[0] == "element":
            in_vertex = parts[1] == "vertex"
            if in_vertex:
                count = int(parts[2])
        elif parts[0] == "property" and in_vertex:
            assert len(parts) == 3 and parts[1] == "float", \
                f"required props must be float: {line!r}"
            props.append(parts[2])
    missing = [p for p in PROPS if p not in props]
    assert count > 0 and not missing, f"count={count} missing={missing}"
    # Parser strides over extras by width; this writer emits exactly the 14.
    assert props == list(PROPS), f"prop order drift: {props}"
    body = raw[end:]
    assert len(body) == count * ROW_BYTES, \
        f"truncated body: {len(body)} of {count * ROW_BYTES} bytes"
    rows = np.frombuffer(body, dtype="<f4").reshape(count, len(PROPS))
    xyz, op = rows[:, 0:3], rows[:, 6]
    mass = float((1.0 / (1.0 + np.exp(-op.astype(np.float64)))).sum())
    return {"count": count, "min": xyz.min(0).tolist(),
            "max": xyz.max(0).tolist(), "opacity_mass": mass}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input PNG sprite (alpha used by --mask-background)")
    ap.add_argument("--out", default=None, help="output PLY (default build/moge/<stem>.ply)")
    ap.add_argument("--size", type=int, default=256,
                    help="longest side at infer (default 256; 0 keeps native)")
    ap.add_argument("--mask-background", action="store_true",
                    help="drop fully-transparent pixels (PNG alpha == 0)")
    ap.add_argument("--model", default=None,
                    help="HF repo id or local .pt (default GUO_MOGE_MODEL or "
                         f"{DEFAULT_MODEL})")
    ap.add_argument("--device", default="cuda", help="cuda (falls back to cpu) or cpu")
    ap.add_argument("--resolution-level", type=int, default=9,
                    help="MoGe detail 0-9 (default 9)")
    ap.add_argument("--refine-steps", type=int, default=None,
                    help="MoGe-3 sparse-refinement passes (default GUO_MOGE_REFINE_STEPS or "
                         f"{DEFAULT_REFINE_STEPS}; 0 skips the refiner; ignored for v2 models)")
    ap.add_argument("--no-refine", action="store_true",
                    help="skip MoGe-3 refinement for speed (same as --refine-steps 0)")
    ap.add_argument("--no-fp16", action="store_true", help="disable fp16 on CUDA")
    ap.add_argument("--timeout", type=int, default=600,
                    help="stage-boundary budget in seconds (caller holds the kill switch)")
    args = ap.parse_args(argv)

    t0 = time.time()
    deadline = t0 + args.timeout
    src = Path(args.input)
    if not src.is_file():
        print(f"[moge] no input: {src}")
        return 1
    out = Path(args.out) if args.out else repo_root() / "build" / "moge" / f"{src.stem}.ply"
    model_id = resolve_model(args.model)
    refine_steps = resolve_refine_steps(args.refine_steps, args.no_refine)
    if not Path(model_id).exists():
        print(f"[moge] model from hub (needs network once; "
              f"HF_HUB_OFFLINE=1 forces cache): {model_id}")

    try:
        rgb01, alpha, size = load_image(src, args.size)
        points, mask = run_infer(rgb01, model_id, args.device,
                                 not args.no_fp16, args.resolution_level,
                                 refine_steps, deadline)
        check_deadline("write", deadline)
        rows, lo, hi = build_rows(points, rgb01, alpha, mask,
                                  args.mask_background)
        write_ply(out, rows)
        stat = verify_ply(out)
        print(f"[moge] verify count={stat['count']} "
              f"bbox_min={[round(v, 3) for v in stat['min']]} "
              f"bbox_max={[round(v, 3) for v in stat['max']]} "
              f"mass={stat['opacity_mass']:.1f} "
              f"({time.time() - t0:.1f}s total)")
    except TimeoutError as ex:
        print(f"[moge] TIMEOUT: {ex}")
        return 2
    except Exception as ex:
        print(f"[moge] FAILED: {type(ex).__name__}: {ex}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
