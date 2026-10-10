#!/usr/bin/env python3
"""Derive GUO's tuned workflow variants from hand-crafted sources.

The sources are the artist's files (starting points); the GUO variants bake in
what the proof runs learned, so automation never relies on memory:

- guo_sfx.json    <- FASTAUDIOTGEN: reprompt ON, qwen_clip fixed to the
  qwen3.5 encoder (the minimax one crashes TextGenerate with a matmul shape
  mismatch), JarJarAudioSaveAudio removed (hardcoded foreign path).
- guo_music.json  <- FASTAUDIOTGEN: same, category music.
- guo_multi.json  <- MultisMaker1: JarJarSubmit3D removed (external POST with
  no key), SaveGLB appended (the SplatToFile3D output is convert-only; without
  a save node the PLY never reaches disk).
- guo_seamless.json <- SEAMLESS: unchanged fork (already clean).

No Hunyuan nodes anywhere in this set, ever.

    python tools/comfy/make_guo_workflows.py --src <workflows-dir> --out tools/comfy/workflows
"""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path

QWEN_CLIP = "qwen3.5_2b_bf16.safetensors"


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def remove_node(ui: dict, node_id: int) -> None:
    """Remove a node and every link touching it (caller must only remove sinks
    or external posts, never load-bearing middles)."""
    nodes = [n for n in ui["nodes"] if n["id"] != node_id]
    dead_inputs = set()
    dead_outputs = set()
    for n in ui["nodes"]:
        if n["id"] == node_id:
            for inp in n.get("inputs") or []:
                if isinstance(inp, dict) and inp.get("link") is not None:
                    dead_inputs.add(inp["link"])
            for out in n.get("outputs") or []:
                if isinstance(out, dict):
                    dead_outputs.update(out.get("links") or [])
    ui["nodes"] = nodes
    ui["links"] = [l for l in ui.get("links", []) if _lid(l) not in dead_inputs | dead_outputs]
    for n in ui["nodes"]:
        for inp in n.get("inputs") or []:
            if isinstance(inp, dict) and inp.get("link") in dead_inputs | dead_outputs:
                inp["link"] = None
        for out in n.get("outputs") or []:
            if isinstance(out, dict) and out.get("links"):
                out["links"] = [l for l in out["links"] if l not in dead_inputs | dead_outputs]


def _lid(link) -> int:
    return link["id"] if isinstance(link, dict) else link[0]


def set_widget(ui: dict, node_id: int, index: int, value) -> None:
    for n in ui["nodes"]:
        if n["id"] == node_id:
            n["widgets_values"][index] = value
            return
    raise KeyError(f"no node {node_id}")


def add_save_glb(ui: dict, splat_node: int = 92, prefix: str = "3d/GUO_") -> int:
    """SaveGLB straight off SplatToFile3D (the B-series-proven shape)."""
    used = {n["id"] for n in ui["nodes"]}
    used.update(_lid(l) for l in ui.get("links", []))
    nid = max(used) + 1
    lid = max(used) + 2
    ui["nodes"].append({
        "id": nid, "type": "SaveGLB", "pos": [1400, 900], "size": [320, 120],
        "flags": {}, "order": 99, "mode": 0,
        "inputs": [
            {"name": "mesh", "type": "FILE_3D_SPLAT_ANY", "link": lid},
            {"name": "filename_prefix", "type": "STRING",
             "widget": {"name": "filename_prefix"}, "link": None},
            {"name": "image", "type": "PREVIEW_3D",
             "widget": {"name": "image"}, "link": None},
        ],
        "outputs": [],
        "properties": {},
        "widgets_values": [prefix, ""],
    })
    for n in ui["nodes"]:
        if n["id"] == splat_node:
            for out in n.get("outputs") or []:
                if isinstance(out, dict):
                    out.setdefault("links", []).append(lid)
    ui["links"].append([lid, splat_node, 0, nid, 0, "FILE_3D_SPLAT_ANY"])
    return nid


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="folder holding the source workflows")
    ap.add_argument("--out", default="tools/comfy/workflows")
    args = ap.parse_args(argv)
    src = Path(args.src)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    # --- audio pair (from FASTAUDIOTGEN; node 52 is the audio subgraph) ---
    base = load(src / "FASTAUDIOTGEN.json")
    # sanity: no Hunyuan, expected subgraph
    assert "Hunyuan" not in json.dumps(base) and "hunyan" not in json.dumps(base).lower()
    sfx = copy.deepcopy(base)
    remove_node(sfx, 61)  # JarJarAudioSaveAudio: foreign absolute path
    set_widget(sfx, 52, 3, True)  # use_reprompt on
    set_widget(sfx, 52, 7, QWEN_CLIP)  # the working reprompt encoder
    (out / "guo_sfx.json").write_text(json.dumps(sfx, indent=1), encoding="utf-8")

    music = copy.deepcopy(sfx)
    set_widget(music, 52, 4, "music")  # reprompt_category music
    (out / "guo_music.json").write_text(json.dumps(music, indent=1), encoding="utf-8")

    # --- multi (from MultisMaker1) ---
    multi = load(src / "MultisMaker1.json")
    assert "Hunyuan" not in json.dumps(multi)
    remove_node(multi, 134)  # JarJarSubmit3D: external POST, no key, not automation
    add_save_glb(multi)
    (out / "guo_multi.json").write_text(json.dumps(multi, indent=1), encoding="utf-8")

    # --- seamless (clean as-is) ---
    seamless = load(src / "SEAMLESS.json")
    (out / "guo_seamless.json").write_text(json.dumps(seamless, indent=1), encoding="utf-8")

    for f in sorted(out.glob("guo_*.json")):
        print(f"[comfy] wrote {f} ({f.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
