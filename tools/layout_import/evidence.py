"""Build/content/stage-bound district proof contracts, independently rechecked."""
import hashlib
import json
from pathlib import Path


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def file_hash(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def content(root, folders=()):
    root = Path(root)
    paths = [root / "district.json", root / "scene.json"] if not folders else []
    for folder in folders:
        paths.extend(p for p in (root / folder).rglob("*") if p.is_file())
    return {p.relative_to(root).as_posix(): file_hash(p) for p in sorted(paths)}


def stage_files(stage):
    return {p.name: file_hash(p) for p in sorted(Path(stage).iterdir())
            if p.is_file() and p.name != "district-stage.json"}


def override_hashes(stage):
    overrides = {}
    for line in (Path(stage) / "files_override.txt").read_text(encoding="utf-8").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, path = line.split("=", 1)
            overrides[key] = file_hash(path)
    return overrides


def contract(built, stage, scope="whole-district"):
    built, stage = Path(built).resolve(), Path(stage).resolve()
    district = read(built / "district.json")
    scene = read(stage / "scenes.json")[district["name"]]
    staging = read(stage / "district-stage.json")
    if district["status"] != "native-valid" or staging.get("passed") is not True or staging["district"] != str(built):
        raise ValueError("district stage provenance mismatch")
    inputs = {**content(built), **content(built, ("parts", "world"))}
    staged = stage_files(stage)
    overrides = override_hashes(stage)
    if staging.get("format") != 2 or staging.get("input_content") != inputs or staging.get("stage_content") != staged or staging.get("overrides") != overrides:
        raise ValueError("unbound or stale district stage; restage through canonical writers")
    # The staged scene must retain the authored tour and component placement.
    authored = read(built / "scene.json")
    if scene["tour"] != authored["tour"]:
        raise ValueError("staged tour differs from authored scene")
    parts = {}
    build_ids = {p["part"]: p["build_id"] for p in district.get("parcels", []) if p.get("part") and p.get("build_id")}
    for p in scene["parts"]:
        source = next((a for a in authored["parts"] if a["name"] == p["name"]), None)
        if source is None or any(p[k] != source[k] for k in ("centre", "doors", "components")):
            raise ValueError("staged part differs from authored scene")
        name = p["name"]
        if Path(name).name != name or name in parts:
            raise ValueError("unsafe or duplicate scene part")
        parts[name] = {"sha256": file_hash(built / "parts" / (name + ".json")),
                       "id": p["id"], "centre": p["centre"], "components": p["components"],
                       "doors": len(p["doors"]),
                       "build_id": source.get("build_id", build_ids.get(name))}
    if set(parts) != {p["name"] for p in authored["parts"]}:
        raise ValueError("staged part set differs from authored scene")
    if scope != "whole-district" and scope not in parts:
        raise ValueError("unknown scene part scope")
    stops = {t["name"]: [t["x"], t["y"], t["z"]] for t in scene["tour"]
             if scope == "whole-district" or t["name"].startswith(scope + "_")}
    if not stops or len(stops) != len([t for t in scene["tour"] if scope == "whole-district" or t["name"].startswith(scope + "_")]):
        raise ValueError("empty or duplicate proof stop set")
    return {"format": 1, "built": str(built), "stage": str(stage), "district": district["name"],
            "scope": scope, "origin": district["origin"], "parts": parts, "stops": stops,
            "content": inputs, "stage_content": staged,
            "overrides": overrides}


def validate(con, build_id, raw):
    bound = raw.get("proof_contract")
    if not isinstance(bound, dict):
        return False, "legacy report has no verifiable proof contract", False
    current = contract(bound["built"], bound["stage"], bound["scope"])
    if current != bound or raw.get("district") != bound["district"] or raw.get("scope") != bound["scope"]:
        return False, "content, stage or report identity changed", False
    row = con.execute("SELECT status FROM build WHERE id=?", (build_id,)).fetchone()
    if not row or row[0] != "native-valid":
        return False, "unknown or blocked catalogue build", False
    # Bind the selected catalogue build to its exact native component bytes.
    artifacts = con.execute("SELECT path,sha256 FROM artifact WHERE build_id=? AND kind IN ('native-components','native-build')", (build_id,)).fetchall()
    hashes = {sha for path, sha in artifacts if Path(path).name == "components.json" or Path(path).parent.name == "parts"}
    included = {name for name, p in bound["parts"].items() if p["build_id"] == build_id}
    included_hashes = {bound["parts"][name]["sha256"] for name in included}
    if not hashes or hashes != included_hashes or (bound["scope"] != "whole-district" and bound["scope"] not in included):
        return False, "selected build is outside proved component scope", False
    stops, places = raw.get("stops", {}), raw.get("place", {})
    expected_places = {bound["district"] + "." + p for p in bound["parts"]}
    if set(stops) != set(bound["stops"]) or set(places) != expected_places:
        return False, "incomplete or unexpected stop/part set", False
    if raw.get("site") != [*bound["origin"], 0]:
        return False, "incorrect world origin", False
    for name, local in bound["stops"].items():
        stop = stops[name]
        target = [bound["origin"][0] + local[0], bound["origin"][1] + local[1], local[2]]
        if stop.get("local") != local or stop.get("target") != target or stop.get("arrived") is not True or stop.get("jump"):
            return False, "stop target mismatch, failed movement or teleport", False
    if any(places[bound["district"] + "." + name].get("ok") is not True or
           places[bound["district"] + "." + name].get("components") != p["components"]
           or places[bound["district"] + "." + name].get("doors") != p["doors"]
           or places[bound["district"] + "." + name].get("at") !=
              [bound["origin"][0] + p["centre"][0], bound["origin"][1] + p["centre"][1], 0]
           for name, p in bound["parts"].items()):
        return False, "part placement failed", False
    return True, "exact component, stage and scoped tour binding verified", bound["scope"] == "whole-district"
