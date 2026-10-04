"""Canonical native writers and private proof orchestration; no retail writes."""
from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys

from layout_import import catalogue, cdda, native


def command(cfg, tool, args, log):
    log.parent.mkdir(parents=True,exist_ok=True)
    with log.open("w",encoding="utf-8") as stream:
        result = subprocess.run([sys.executable,str(cfg.tools/tool/"run.py"),*map(str,args)],stdout=stream,stderr=subprocess.STDOUT)
    if result.returncode:
        print(log.read_text(encoding="utf-8")[-3000:])
    return result.returncode


def district_stage(cfg, built, stage):
    built,stage = Path(built).resolve(),Path(stage).resolve()
    record = json.loads((built/"district.json").read_text(encoding="utf-8"))
    if record["status"] != "native-valid":
        raise ValueError("blocked district cannot be staged")
    if stage.is_relative_to(cfg.client_data.resolve()) or stage==built or stage.is_relative_to(built/"world"/"blocks"):
        raise ValueError("unsafe district stage output")
    # A world export and the asset stage share this isolated data layer. Save
    # both override maps because the canonical asset writer regenerates its own.
    if command(cfg,"world",["export","--project",built/"world","--out",stage],built/"world-export.log"):
        return 1
    if command(cfg,"world",["verify","--project",built/"world","--out",stage],built/"world-verify.log"):
        return 1
    world_lines = (stage/"files_override.txt").read_text(encoding="utf-8").splitlines()
    if command(cfg,"multi",["scene-write",record["name"],"--built",built,"--stage",stage],built/"multi-write.log"):
        return 1
    multi_lines = (stage/"files_override.txt").read_text(encoding="utf-8").splitlines()
    # An idempotent multi write refreshes only the sidecar. Recover its layer
    # from the canonical stage manifest after world export replaced overrides.
    asset_manifest=json.loads((stage/"stage.json").read_text(encoding="utf-8"))
    for entry in asset_manifest["files"].values():
        name=entry["name"]
        if Path(name).name!=name:
            raise ValueError("unsafe staged asset filename")
        multi_lines.append(f"{name}={stage/name}")
    overrides = {}
    for line in world_lines+multi_lines:
        if "=" in line and not line.lstrip().startswith("#"):
            key,path = line.split("=",1)
            if not Path(path).is_file():
                raise ValueError(f"missing staged override {key}")
            overrides[key.lower()] = path
    (stage/"files_override.txt").write_text("# GUO district world + native multis\n"+"\n".join(f"{k}={v}" for k,v in sorted(overrides.items()))+"\n",encoding="utf-8")
    if command(cfg,"uodata_write",["verify","--stage",stage],built/"multi-verify.log"):
        return 1
    (stage/"district-stage.json").write_text(json.dumps({"format":1,"district":str(built),
        "origin":record["origin"],"name":record["name"],"override_files":list(overrides),
        "passed":True,"writers":["tools/world","tools/multi","tools/uodata_write"]},indent=1),encoding="utf-8")
    print(json.dumps({"stage":str(stage),"world_and_multis_readback":True,"override_files":list(overrides)},indent=2))
    return 0


def district_prove(cfg, built, stage, out, clip=None, only_part=None):
    from multi import prove
    built,stage,out = Path(built).resolve(),Path(stage).resolve(),Path(out).resolve()
    recorded = json.loads((stage/"district-stage.json").read_text(encoding="utf-8"))
    district = json.loads((built/"district.json").read_text(encoding="utf-8"))
    if recorded["district"] != str(built) or not recorded["passed"] or district["status"] != "native-valid":
        raise ValueError("district stage provenance mismatch")
    # UltimaLive keys map copies by shard name; each changed map set gets its
    # own name so a previous proof cannot hide the generated city map.
    map_hash = hashlib.sha256()
    for path in sorted(stage.glob("*.mul"))+sorted(stage.glob("*.uop")):
        map_hash.update(path.name.encode())
        with path.open("rb") as stream:
            for chunk in iter(lambda:stream.read(1<<20),b""):
                map_hash.update(chunk)
    shard_name = "GUO-Layout-"+map_hash.hexdigest()[:12]
    scene = json.loads((stage/"scenes.json").read_text(encoding="utf-8"))[district["name"]]
    parts = [(district["name"]+"."+p["name"],p["id"],p["centre"][0],p["centre"][1],p.get("doors",[])) for p in scene["parts"]]
    stops = [(t["name"],t["x"],t["y"],t["z"]) for t in scene["tour"]]
    if only_part:
        if only_part not in {p['name'] for p in scene['parts']}:
            raise ValueError("unknown scene part for scoped proof")
        stops=[s for s in stops if s[0].startswith(only_part+'_')]
    if not stops:
        raise ValueError("proof has no room stops")
    from multifile import Multis
    from guo.uoread import TileData
    td = TileData(cfg.client_data)
    candidates = []
    for part in scene["parts"]:
        for comp in Multis(stage).get(part["id"]):
            tile=td.static(comp.item)
            if comp.visible and tile and tile["flags"] & 0x40 and tile["height"]>=16 and comp.z==district["theme"]["floor_z"] and "wall" in tile["name"].lower():
                px,py=comp.x+part["centre"][0],comp.y+part["centre"][1]
                candidates.append((abs(px-stops[-1][1])+abs(py-stops[-1][2]),px,py,comp.z,comp.item))
    blocked=[]
    if candidates:
        _,px,py,pz,item=min(candidates)
        blocked=[("negative_native_wall",px,py,pz)]
    out.mkdir(parents=True,exist_ok=True)
    cache_root = Path(os.environ.get("ProgramData",r"C:\ProgramData")).resolve()
    cache = (cache_root/shard_name).resolve()
    made = not cache.exists()
    try:
        result = prove.session(cfg,stage,out,parts,(*district["origin"],0),stops,clip,16,
            "GUO: imported layouts transformed into native UO terrain, houses and doors",
            {"district":district["name"],"scope":only_part or "whole-district",
             "negative_target_tiles":[{"at":[px,py,pz],"item":item,"tiledata":td.static(item)}] if blocked else []},
            shard_env={"GUO_BRIDGE_SHARD":shard_name},profile={"use_custom_light_level":True,"light_level":0,"light_level_type":0},blocked=blocked)
    finally:
        if made and cache.is_relative_to(cache_root) and cache.parent==cache_root and cache.name==shard_name:
            shutil.rmtree(cache,ignore_errors=True)
    print(json.dumps({"result":result,"report":str(out/"report.json"),"shard_name":shard_name}))
    return result


def record_evidence(con, build_id, kind, report, artifacts=()):
    report = Path(report).resolve()
    raw = json.loads(report.read_text(encoding="utf-8"))
    if kind == "negative-blocker":
        negatives=raw.get("negative_blockers",{})
        targets=raw.get("negative_target_tiles",[])
        passed=bool(negatives) and bool(targets) and all(s.get("arrived") is False and bool(s.get("client_says")) for s in negatives.values())
        passed &= all(t["tiledata"]["flags"]&0x40 and t["tiledata"]["height"]>=16 for t in targets)
    elif kind == "gameplay":
        passed = bool(raw.get("stops")) and bool(raw.get("place")) and all(s.get("arrived") and not s.get("jump") for s in raw["stops"].values()) and all(p.get("ok") for p in raw["place"].values())
        passed &= all(s.get("arrived") is False and bool(s.get("client_says")) for s in raw.get("negative_blockers",{}).values())
    else:
        passed = raw.get("ok") is True or raw.get("passed") is True
    evidence = {"passed":passed,"report":str(report),"sha256":hashlib.sha256(report.read_bytes()).hexdigest(),"record":raw}
    catalogue.save_validation(con,build_id,kind,evidence)
    catalogue.save_artifacts(con,build_id,kind,[report,*artifacts])
    return passed
