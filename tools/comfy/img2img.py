#!/usr/bin/env python3
"""img2img through ComfyUI (StaticStudio's painter): upload a PNG, denoise with a prompt, fetch the result.

Same API shape as run.py (UO_COMFY_URL), same quiet 15 s polling. The workflow
is built in API format inline: LoadImage -> VAEEncode + dual CLIP + KSampler
(denoise) -> VAEDecode -> SaveImage.

    python tools/comfy/img2img.py --in build/theme_proof_src/art/static_0x0034.png \\
        --out build/comfy/theme_wall_mossy.png --prompt "mossy overgrown ancient stone bricks" \\
        --ckpt DreamShaper_8_pruned.safetensors --denoise 0.65 --seed 7
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config  # noqa: E402


def url() -> str:
    try:
        return load_config().comfy_url.rstrip("/")
    except Exception:
        return "http://127.0.0.1:8188"


def upload(base: str, path: Path) -> str:
    import uuid
    boundary = uuid.uuid4().hex
    data = path.read_bytes()
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{path.name}\"\r\n"
            f"Content-Type: image/png\r\n\r\n").encode() + data + \
        f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(f"{base}/upload/image", data=body,
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode())["name"]


def queue(base: str, prompt: dict) -> str:
    data = json.dumps({"prompt": prompt, "client_id": "guo-img2img"}).encode()
    req = urllib.request.Request(f"{base}/prompt", data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode())["prompt_id"]
    except urllib.error.HTTPError as ex:
        raise RuntimeError(f"queue refused: {ex.read().decode()[:500]}")


def wait(base: str, pid: str, timeout: int) -> dict:
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(15)
        with urllib.request.urlopen(f"{base}/history/{pid}", timeout=30) as r:
            hist = json.loads(r.read().decode())
        e = hist.get(pid, {})
        if e.get("status", {}).get("completed"):
            if e["status"].get("status_str") == "error":
                raise RuntimeError(f"execution error: {json.dumps(e['status'].get('messages'))[:500]}")
            return e.get("outputs", {})
    raise TimeoutError("no completion in time")


def fetch(base: str, outputs: dict, out_dir: Path) -> list[Path]:
    import urllib.parse
    saved = []
    for node_id, node_out in outputs.items():
        if not isinstance(node_out, dict):
            continue
        for key, items in node_out.items():
            if not isinstance(items, list):
                continue
            for item in items:
                if not isinstance(item, dict) or "filename" not in item:
                    continue
                qs = urllib.parse.urlencode({"filename": item["filename"],
                                             "subfolder": item.get("subfolder", ""),
                                             "type": item.get("type", "output")})
                with urllib.request.urlopen(f"{base}/view?{qs}", timeout=60) as r:
                    data = r.read()
                dest = out_dir / f"img2img_{node_id}_{item['filename']}"
                dest.write_bytes(data)
                saved.append(dest)
                print(f"[img2img] {item['filename']} -> {dest} ({len(data)} bytes)")
    if not saved:
        raise RuntimeError("no files produced")
    return saved


def build_workflow(uploaded: str, args) -> dict:
    return {
        "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": args.ckpt}},
        "2": {"class_type": "LoadImage", "inputs": {"image": uploaded}},
        "3": {"class_type": "VAEEncode", "inputs": {"pixels": ["2", 0], "vae": ["1", 2]}},
        "4": {"class_type": "CLIPTextEncode", "inputs": {"text": args.prompt, "clip": ["1", 1]}},
        "5": {"class_type": "CLIPTextEncode", "inputs": {"text": args.negative, "clip": ["1", 1]}},
        "6": {"class_type": "KSampler", "inputs": {
            "model": ["1", 0], "positive": ["4", 0], "negative": ["5", 0],
            "latent_image": ["3", 0], "seed": args.seed, "steps": args.steps,
            "cfg": args.cfg, "sampler_name": "euler", "scheduler": "normal",
            "denoise": args.denoise}},
        "7": {"class_type": "VAEDecode", "inputs": {"samples": ["6", 0], "vae": ["1", 2]}},
        "8": {"class_type": "SaveImage", "inputs": {"images": ["7", 0], "filename_prefix": "GUO_img2img"}},
    }


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--prompt", required=True)
    ap.add_argument("--negative", default="blurry, watermark, text, deformed")
    ap.add_argument("--ckpt", default="DreamShaper_8_pruned.safetensors")
    ap.add_argument("--denoise", type=float, default=0.65)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--steps", type=int, default=20)
    ap.add_argument("--cfg", type=float, default=7.0)
    ap.add_argument("--timeout", type=int, default=1200)
    args = ap.parse_args(argv)
    base = url()
    print(f"[img2img] uploading {args.src}")
    uploaded = upload(base, Path(args.src))
    pid = queue(base, build_workflow(uploaded, args))
    print(f"[img2img] queued {pid}")
    outputs = wait(base, pid, args.timeout)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    saved = fetch(base, outputs, out.parent)
    saved[0].rename(out)
    print(f"[img2img] {out} ({out.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
