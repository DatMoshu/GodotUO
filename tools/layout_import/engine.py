"""Run the inspected headless exporter in an isolated user directory."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess

from layout_import import cdda,semantic
from layout_import.bake import Bake


def file_hash(path):
    h=hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda:stream.read(1<<20),b""):
            h.update(chunk)
    return h.hexdigest()


def export(con,profile,source,binary,seed,bounds,out,timeout=900,world_size=None):
    source,binary,out = Path(source).resolve(),Path(binary).resolve(),Path(out).resolve()
    definitions=semantic.SourceDefinitions(con,profile)
    if out.is_relative_to(source) or out==binary or not (source/"data").is_dir():
        raise ValueError("headless export must use a separate output and a valid source root")
    # A profile must describe the actual data this executable will read.
    for record in con.execute("SELECT path,sha256 FROM source_file WHERE snapshot_id=?",(definitions.snapshot,)):
        if not (source/record["path"]).is_file() or file_hash(source/record["path"]) != record["sha256"]:
            raise ValueError(f"source changed since snapshot: {record['path']}; rescan before exporting")
    help_result=subprocess.run([str(binary),"--help"],capture_output=True,text=True,timeout=30)
    help_text=help_result.stdout+help_result.stderr
    for flag in ("--submap-export","--submap-bounds","--worldgen-mods"):
        if flag not in help_text:
            raise ValueError(f"binary has no inspected headless capability: {flag}")
    inputs={"format":1,"source_snapshot":definitions.snapshot,"binary_sha256":file_hash(binary),
            "binary_source_correspondence":"unverified; capabilities probed, no reproducible source build asserted",
            "seed_argument":seed,"bounds":list(bounds),"mods":definitions.order,"world_size":world_size}
    control=out/"engine-inputs.json"
    if control.exists():
        previous=json.loads(control.read_text(encoding="utf-8"))
        if previous!=inputs:
            raise ValueError("output belongs to different engine inputs; use a new output directory")
        if (out/"bake"/"manifest.json").exists():
            Bake(out/"bake")
            return {"status":"reused-identical-inputs","inputs":inputs,"bake":str(out/"bake")}
    elif out.exists() and any(out.iterdir()):
        raise ValueError("nonempty output has no engine provenance; use a new output directory")
    out.mkdir(parents=True,exist_ok=True)
    control.write_text(json.dumps(inputs,indent=1),encoding="utf-8")
    common=[str(binary),"--basepath",str(source)+os.sep,"--userdir",str(out/"user")+os.sep,
            "--seed",seed,"--worldgen-mods",",".join(definitions.order)]
    if world_size:
        with (out/"world.log").open("w",encoding="utf-8") as log:
            result=subprocess.run([*common,"--worldgen-export",str(out/"world"),"--worldgen-size",str(world_size)],stdout=log,stderr=subprocess.STDOUT,timeout=timeout)
        if result.returncode:
            raise ValueError(f"headless world planner failed: {out/'world.log'}")
    with (out/"bake.log").open("w",encoding="utf-8") as log:
        result=subprocess.run([*common,"--submap-export",str(out/"bake"),"--submap-bounds",",".join(map(str,bounds))],stdout=log,stderr=subprocess.STDOUT,timeout=timeout)
    if result.returncode:
        raise ValueError(f"headless detailed bake failed: {out/'bake.log'}")
    baked=Bake(out/"bake")
    if baked.manifest["mods"]!=definitions.order:
        raise ValueError("headless exporter loaded different mods")
    return {"status":"baked","inputs":inputs,"bake":str(out/"bake"),"manifest_sha256":baked.manifest_hash}
