#!/usr/bin/env python3
"""ComfyUI client for GUO (images, audio, 3D).

URLs resolve in the standard order (tools/guo/config.py):
  env UO_COMFY_URL -> launchers/_shared/config.local.bat -> config.bat (default http://127.0.0.1:8188)

    python tools/comfy/run.py status [--url URL]
    python tools/comfy/run.py txt2img --prompt "..." --out build/comfy/img.png [options]
    python tools/comfy/run.py run --workflow file.json --out-dir build/comfy/ [--prompt ...] [--set key=value ...]

`run` is the generic path for your hand-crafted workflows (PLY/gaussian, audio
SFX/music, obj/fbx/glb): it queues any API-format workflow, polls /history,
and downloads EVERY file ref it finds in the outputs (images, audio, meshes),
so new output types need no code change. Supported download extensions are
passed through as-is: .png/.jpg/.webp, .wav/.mp3/.ogg/.flac, .obj/.fbx/.glb/.ply/.stl.

Nothing here writes to the UO client install. Outputs default under build/comfy/ (gitignored).
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
import urllib.parse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo.config import load_config  # noqa: E402

HERE = Path(__file__).resolve().parent

# Extensions passed through from ComfyUI outputs (images, audio, 3D).
KNOWN_EXTS = (
    ".png", ".jpg", ".jpeg", ".webp", ".gif",
    ".wav", ".mp3", ".ogg", ".flac",
    ".obj", ".fbx", ".glb", ".gltf", ".ply", ".stl",
)


def resolve_url(override: str | None) -> str:
    if override:
        return override.rstrip("/")
    try:
        return load_config().comfy_url.rstrip("/")
    except Exception:
        return "http://127.0.0.1:8188"


def api_get(url: str, timeout: int = 10) -> dict:
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def api_post(url: str, payload: dict, timeout: int = 30) -> dict:
    import urllib.error
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        body = ex.read().decode("utf-8", "replace")[:2000]
        raise RuntimeError(f"POST {url} -> HTTP {ex.code}: {body}") from ex


def cmd_status(url: str) -> int:
    try:
        s = api_get(url + "/system_stats", timeout=8)
    except Exception as ex:
        print(f"[comfy] no answer at {url}: {ex}")
        return 1
    sysinfo = s.get("system", {})
    print(f"[comfy] {url} -> ComfyUI {sysinfo.get('comfyui_version', '?')}")
    print(f"[comfy] pytorch {sysinfo.get('pytorch_version', '?')} python {sysinfo.get('python_version', '?')}")
    for d in s.get("devices", []):
        print(f"[comfy] device {d.get('name', '?')}")
    q = api_get(url + "/queue", timeout=8)
    print(f"[comfy] running {len(q.get('queue_running', []))} pending {len(q.get('queue_pending', []))}")
    return 0


def txt2img_workflow(prompt: str, ckpt: str, width: int, height: int,
                     seed: int, steps: int, cfg: float, negative: str = "") -> dict:
    return {
        "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": ckpt}},
        "2": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["1", 1]}},
        "3": {"class_type": "CLIPTextEncode", "inputs": {"text": negative, "clip": ["1", 1]}},
        "4": {"class_type": "EmptyLatentImage",
              "inputs": {"width": width, "height": height, "batch_size": 1}},
        "5": {"class_type": "KSampler",
              "inputs": {"model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0],
                         "latent_image": ["4", 0], "seed": seed, "steps": steps,
                         "cfg": cfg, "sampler_name": "euler",
                         "scheduler": "normal", "denoise": 1.0}},
        "6": {"class_type": "VAEDecode", "inputs": {"samples": ["5", 0], "vae": ["1", 2]}},
        "7": {"class_type": "SaveImage",
              "inputs": {"images": ["6", 0], "filename_prefix": "GUO_proof"}},
    }


def queue_and_wait(url: str, workflow: dict, out_dir: Path,
                   timeout_s: int = 600, poll_s: float = 15.0) -> list[Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    client_id = "guo-proof"
    queued = api_post(url + "/prompt", {"prompt": workflow, "client_id": client_id})
    prompt_id = queued.get("prompt_id")
    if not prompt_id:
        raise RuntimeError(f"queue refused: {queued}")
    print(f"[comfy] queued {prompt_id}", flush=True)
    return wait_on_prompt(url, prompt_id, out_dir, timeout_s=timeout_s, poll_s=poll_s)


def wait_on_prompt(url: str, prompt_id: str, out_dir: Path,
                   timeout_s: int = 2400, poll_s: float = 15.0) -> list[Path]:
    """One cheap /history hit per poll; prints only when progress changes."""
    out_dir.mkdir(parents=True, exist_ok=True)
    deadline = time.time() + timeout_s
    last_n = -1
    while time.time() < deadline:
        time.sleep(poll_s)
        try:
            hist = api_get(f"{url}/history/{prompt_id}", timeout=120)
        except Exception as ex:
            print(f"[comfy] history quiet ({type(ex).__name__}); still waiting...", flush=True)
            continue
        entry = hist.get(prompt_id)
        # History accrues node outputs as they complete; only stop when the
        # execution itself is done.
        status = (entry or {}).get("status", {})
        if status.get("completed"):
            if status.get("status_str") == "error":
                msgs = [m for m in status.get("messages", []) if m]
                raise RuntimeError(f"execution error: {json.dumps(msgs)[:1000]}")
            outputs = entry.get("outputs")
            if not outputs:
                raise TimeoutError(f"prompt {prompt_id} completed with no outputs")
            return download_outputs(url, outputs, out_dir)
        n_out = len((entry or {}).get("outputs") or {})
        if n_out != last_n:
            last_n = n_out
            print(f"[comfy] ... nodes_done={n_out}", flush=True)
    raise TimeoutError(f"no completion for {prompt_id} within {timeout_s}s")


def download_outputs(url: str, outputs: dict, out_dir: Path) -> list[Path]:
    """Download every file ref in a /history outputs block.

    ComfyUI image nodes emit outputs[].images; audio/mesh nodes emit the same
    shape ({filename, subfolder, type}) under their own keys. We walk every
    list in every node output and fetch anything with a filename, so PLY/GLB/
    OBJ/FBX/WAV/MP3 workflows work without a code change.
    """
    saved: list[Path] = []
    for node_id, node_out in outputs.items():
        if not isinstance(node_out, dict):
            continue
        for key, items in node_out.items():
            if not isinstance(items, list):
                continue
            for item in items:
                if not isinstance(item, dict) or "filename" not in item:
                    continue
                name = item["filename"]
                sub = item.get("subfolder", "")
                typ = item.get("type", "output")
                ext = Path(name).suffix.lower()
                if ext and ext not in KNOWN_EXTS:
                    print(f"[comfy] node {node_id}/{key}: unexpected '{name}' (fetching anyway)")
                qs = urllib.parse.urlencode({"filename": name, "subfolder": sub, "type": typ})
                src = f"{url}/view?{qs}"
                dest = out_dir / f"{prompt_safe(node_id, key)}{ext or '.bin'}"
                # Avoid collisions when a workflow emits several files.
                i = 1
                while dest.exists():
                    dest = out_dir / f"{prompt_safe(node_id, key)}_{i}{ext or '.bin'}"
                    i += 1
                with urllib.request.urlopen(src, timeout=60) as r, open(dest, "wb") as f:
                    f.write(r.read())
                print(f"[comfy] node {node_id}/{key}: {name} -> {dest} ({dest.stat().st_size} bytes)")
                saved.append(dest)
    if not saved:
        raise RuntimeError(f"workflow finished but produced no downloadable files: {json.dumps(outputs)[:500]}")
    return saved


def prompt_safe(node_id: str, key: str) -> str:
    stem = f"node{node_id}_{key}".replace("/", "_").replace("\\", "_")
    return "".join(c if c.isalnum() or c in ("_", "-", ".") else "_" for c in stem)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="ComfyUI client for GUO")
    ap.add_argument("--url", default=None, help="ComfyUI base URL (default: UO_COMFY_URL)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("status", help="print version and queue depth")
    s.add_argument("--url", default=None)

    t = sub.add_parser("txt2img", help="minimal SD1.5 proof image")
    t.add_argument("--prompt", required=True)
    t.add_argument("--out", default="build/comfy/proof.png")
    t.add_argument("--ckpt", default="DreamShaper_8_pruned.safetensors")
    t.add_argument("--negative", default="blurry, watermark, text")
    t.add_argument("--width", type=int, default=512)
    t.add_argument("--height", type=int, default=512)
    t.add_argument("--seed", type=int, default=1)
    t.add_argument("--steps", type=int, default=20)
    t.add_argument("--cfg", type=float, default=7.0)
    t.add_argument("--timeout", type=int, default=600)

    r = sub.add_parser("run", help="queue any workflow (UI or API format) and fetch all artifacts")
    r.add_argument("--workflow", required=True, help=".json workflow file (UI format incl. subgraphs, or API format)")
    r.add_argument("--out-dir", default="build/comfy/")
    r.add_argument("--prompt", default=None, help="binds {{prompt}} placeholders / positive CLIPTextEncode")
    r.add_argument("--set", action="append", default=[], metavar="key=value",
                   help="extra string bindings for {{key}} placeholders (repeatable)")
    r.add_argument("--drop", action="append", default=[],
                   help="sink node type to drop before queueing (repeatable), e.g. JarJarSubmit3D")
    r.add_argument("--ui-set", action="append", default=[], metavar="node.widget=value",
                   help="override an outer UI node widget (repeatable), e.g. 52.3=true")
    r.add_argument("--save-splat", default=None, metavar="PREFIX",
                   help="append a SaveGaussianSplat after every SplatToFile3D (MultisMaker1 has none; without it the PLY never reaches disk)")
    r.add_argument("--print-prompt", action="store_true",
                   help="print the converted API prompt and exit (no queue)")
    r.add_argument("--validate", action="store_true",
                   help="check node types against the server's /object_info and exit (no queue)")
    r.add_argument("--timeout", type=int, default=1200)

    w = sub.add_parser("wait", help="wait on an already-queued prompt and fetch its artifacts")
    w.add_argument("--prompt-id", required=True)
    w.add_argument("--out-dir", default="build/comfy/")
    w.add_argument("--timeout", type=int, default=2400)

    args = ap.parse_args(argv)
    url = resolve_url(getattr(args, "url", None) or None)
    # --url on root vs subcommand: subcommand wins when given.
    if getattr(args, "url", None):
        url = resolve_url(args.url)

    if args.cmd == "status":
        return cmd_status(url)

    if args.cmd == "txt2img":
        wf = txt2img_workflow(args.prompt, args.ckpt, args.width, args.height,
                              args.seed, args.steps, args.cfg, args.negative)
        out = Path(args.out)
        try:
            saved = queue_and_wait(url, wf, out.parent, timeout_s=args.timeout)
        except Exception as ex:
            print(f"[comfy] FAILED: {ex}")
            return 1
        # First image is the proof; rename for the caller.
        saved[0].rename(out) if saved[0] != out else None
        print(f"[comfy] proof image: {out} ({out.stat().st_size} bytes)")
        print(f"[comfy] seed={args.seed} steps={args.steps} cfg={args.cfg} ckpt={args.ckpt}")
        return 0

    if args.cmd == "run":
        raw = json.loads(Path(args.workflow).read_text(encoding="utf-8"))
        if isinstance(raw, dict) and "nodes" in raw:
            sys.path.insert(0, str(Path(__file__).resolve().parent))
            from uiconv import convert_file  # noqa: E402
            ui_set = {}
            for spec in args.ui_set:
                try:
                    lhs, val = spec.split("=", 1)
                    nid_s, widx_s = lhs.split(".", 1)
                    ui_set[(int(nid_s), int(widx_s))] = coerce(val)
                except ValueError:
                    print(f"[comfy] bad --ui-set {spec!r} (want node.widget=value)")
                    return 2
            try:
                wf = convert_file(args.workflow, drop_types=tuple(args.drop), ui_set=ui_set)
            except Exception as ex:
                print(f"[comfy] conversion FAILED: {ex}")
                return 1
            print(f"[comfy] converted UI workflow: {len(wf)} nodes")
        else:
            wf = raw
            bindings = dict(s.split("=", 1) for s in args.set if "=" in s)
            if args.prompt is not None:
                bindings["prompt"] = args.prompt
            if bindings:
                wf = bind_placeholders(wf, bindings)
        if args.save_splat:
            wf = append_splat_saves(wf, args.save_splat)
        if args.validate:
            return cmd_validate(url, wf)
        if args.print_prompt:
            print(json.dumps(wf, indent=1)[:8000])
            return 0
        try:
            saved = queue_and_wait(url, wf, Path(args.out_dir), timeout_s=args.timeout)
        except Exception as ex:
            print(f"[comfy] FAILED: {ex}")
            return 1
        print(f"[comfy] {len(saved)} file(s) in {args.out_dir}")
        return 0

    if args.cmd == "wait":
        try:
            saved = wait_on_prompt(url, args.prompt_id, Path(args.out_dir),
                                   timeout_s=args.timeout)
        except Exception as ex:
            print(f"[comfy] FAILED: {ex}")
            return 1
        print(f"[comfy] {len(saved)} file(s) in {args.out_dir}")
        return 0

    return 2


def append_splat_saves(wf: dict, prefix: str) -> dict:
    """Append SaveGLB after every SplatToFile3D so the PLY hits disk (and history).

    Mirrors the author's own B2 pattern (SaveGLB mesh <- SplatToFile3D,
    prefix "3d/PLY_"): SaveGLB accepts FILE_3D_SPLAT_ANY and writes the PLY.
    Returns the extended prompt.
    """
    ids = [int(k) for k in wf]
    nxt = max(ids, default=0) + 1
    for k in sorted(wf, key=int):
        if isinstance(wf[k], dict) and wf[k].get("class_type") == "SplatToFile3D":
            wf[str(nxt)] = {"class_type": "SaveGLB",
                            "inputs": {"mesh": [k, 0], "filename_prefix": prefix}}
            nxt += 1
    return wf


def coerce(val: str):
    low = val.lower()
    if low in ("true", "false"):
        return low == "true"
    try:
        return int(val)
    except ValueError:
        pass
    try:
        return float(val)
    except ValueError:
        pass
    return val


def cmd_validate(url: str, wf: dict) -> int:
    try:
        info = api_get(url + "/object_info", timeout=120)
    except Exception as ex:
        print(f"[comfy] validate FAILED: {ex}")
        return 1
    missing = sorted({n.get("class_type") for n in wf.values()
                      if isinstance(n, dict) and n.get("class_type") not in info})
    print(f"[comfy] {len(wf)} nodes, {len(missing)} unknown type(s)")
    for t in missing:
        print(f"[comfy] MISSING: {t}")
    return 1 if missing else 0


def bind_placeholders(wf: dict, bindings: dict[str, str]) -> dict:
    """Replace {{key}} in any string input (same convention as the editor's Art dock)."""
    for _node_id, node in wf.items() if isinstance(wf, dict) else []:
        inputs = node.get("inputs") if isinstance(node, dict) else None
        if not isinstance(inputs, dict):
            continue
        for k, v in list(inputs.items()):
            if isinstance(v, str):
                for bk, bv in bindings.items():
                    v = v.replace("{{" + bk + "}}", bv)
                inputs[k] = v
    return wf


if __name__ == "__main__":
    raise SystemExit(main())
