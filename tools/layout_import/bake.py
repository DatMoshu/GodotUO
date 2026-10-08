"""Strict adapter for the inspected CDDA 1.0.0 headless submap export."""
from __future__ import annotations

import gzip
import hashlib
import json
from pathlib import Path

from layout_import.cdda import digest
from layout_import.semantic import ResolutionError, cell_semantics, infer_rooms, layout_hash


def read_json(path):
    raw = path.read_bytes()
    return json.loads(gzip.decompress(raw) if path.suffix == ".gz" else raw), hashlib.sha256(raw).hexdigest()


def expand(runs, palette, size):
    if not isinstance(runs, list):
        raise ResolutionError("RLE must be an array")
    out = []
    for pair in runs:
        if not isinstance(pair, list) or len(pair) != 2:
            raise ResolutionError("RLE run must contain index,count")
        index, count = pair
        if type(index) is not int or type(count) is not int or not 0 <= index < len(palette) or count <= 0:
            raise ResolutionError(f"invalid RLE run {pair}")
        if len(out) + count > size:
            raise ResolutionError("RLE overrun")
        out.extend([palette[index]] * count)
    if len(out) != size:
        raise ResolutionError(f"RLE has {len(out)} cells, expected {size}")
    return out


class Bake:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.manifest, self.manifest_hash = read_json(self.root / "manifest.json")
        if self.manifest.get("tool") != "cdda-submap-export" or self.manifest.get("tool_version") != "1.0.0":
            raise ResolutionError("unsupported or missing headless bake manifest")
        if self.manifest.get("omt_tiles") != 24:
            raise ResolutionError("unsupported OMT size")
        self.omts, self.shard_hashes = {}, {}
        for part in self.manifest["shards"]:
            path = (self.root / part["file"]).resolve()
            if not path.is_relative_to(self.root):
                raise ResolutionError("shard path escapes bake root")
            shard, sha = read_json(path)
            if shard.get("row_major") != "index = y * omt_tiles + x" or shard.get("omt_tiles") != 24:
                raise ResolutionError("unknown shard axes/dimensions")
            if shard.get("seed") != self.manifest["seed"]:
                raise ResolutionError("manifest/shard seed mismatch")
            if len(shard["omts"]) != part["omt_count"]:
                raise ResolutionError("manifest/shard OMT count mismatch")
            self.shard_hashes[part["file"]] = sha
            for key, omt in shard["omts"].items():
                xy = tuple(map(int, key.split(",")))
                if xy in self.omts:
                    raise ResolutionError(f"duplicate OMT {xy}")
                self.omts[xy] = (omt, shard, part["file"])

    def decode_level(self, xy, z):
        if xy not in self.omts:
            raise ResolutionError(f"OMT {xy} is outside this bake")
        omt, shard, file = self.omts[xy]
        matches = [level for level in omt["layers"] if level["z"] == z]
        if len(matches) != 1:
            raise ResolutionError(f"OMT {xy} has {len(matches)} records for level {z}; missing layers are not inferred")
        level = matches[0]
        if "uniform_ter" in level:
            terrain = expand([[level["uniform_ter"], 576]], shard["ter_palette"], 576)
        else:
            terrain = expand(level["ter"], shard["ter_palette"], 576)
        furniture = expand(level["furn"], shard["furn_palette"], 576) if "furn" in level else ["f_null"] * 576
        return terrain, furniture, level, file

    def layout(self, definitions, name, bounds, levels=(0,)):
        x0, y0, x1, y1 = bounds
        if x1 < x0 or y1 < y0:
            raise ResolutionError("invalid OMT bounds")
        if self.manifest.get("mods") != definitions.order:
            raise ResolutionError("bake mods differ from selected source profile")
        out = {"format": 1, "kind": "semantic-layout", "name": name,
               "width": (x1 - x0 + 1) * 24, "height": (y1 - y0 + 1) * 24,
               "levels": [], "diagnostics": [], "adaptations": [],
               "provenance": {"adapter": "cdda-engine-bake-1", "source_snapshot": definitions.snapshot,
                  "manifest_hash": self.manifest_hash, "shard_hashes": self.shard_hashes,
                  "source_version_claim": self.manifest.get("cdda_version"), "seed": self.manifest["seed"],
                  "mods": self.manifest["mods"], "omt_bounds": list(bounds), "license": definitions.license,
                  "definition_lineage": "unavailable in engine exporter; semantic metadata IDs are not a mapgen selection trace"}}
        control=self.root.parent/"engine-inputs.json"
        if control.exists():
            inputs=json.loads(control.read_text(encoding="utf-8"))
            if inputs["source_snapshot"]!=definitions.snapshot or inputs["mods"]!=definitions.order:
                raise ResolutionError("engine input provenance differs from selected source snapshot/profile")
            out["provenance"]["engine_inputs"]=inputs
        for z in levels:
            cells, extras = [], []
            for ay in range(y0, y1 + 1):
                for ax in range(x0, x1 + 1):
                    terrain, furniture, raw, file = self.decode_level((ax, ay), z)
                    for i, (ter, furn) in enumerate(zip(terrain, furniture)):
                        cells.append({"x": (ax - x0) * 24 + i % 24, "y": (ay - y0) * 24 + i // 24,
                                      "terrain": ter, "furniture": furn, **cell_semantics(ter, furn, definitions),
                                      "lineage": {"shard": file, "omt": [ax, ay], "cell": [i % 24, i // 24], "z": z}})
                    for key in ("items", "traps", "fields", "spawns", "vehicles", "cosmetics", "radiation"):
                        if raw.get(key):
                            extras.append({"omt": [ax, ay], "kind": key, "records": raw[key]})
            cells.sort(key=lambda c: (c["y"], c["x"]))
            out["levels"].append(infer_rooms({"z": z, "cells": cells, "source_extras": extras}))
        out["layout_hash"] = layout_hash(out)
        return out
