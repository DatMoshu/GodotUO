"""Build the layered tavern proof fixture using canonical UO authoring tools."""
from pathlib import Path
import json
import sys
import hashlib
from dataclasses import replace

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools"), str(ROOT / "tools" / "multi")]
from guo import load_config
import generate
import validate
import render
from guo.uoread import TileData

def main():
    cfg = load_config()
    recipe = ROOT / "tools/multi/examples/layered_tavern.json"
    desc = json.loads(recipe.read_text())
    cat = generate.Catalogue(cfg.build / "multi/catalogue")
    parts, side = generate.build(desc, cat)
    # Deliberate coherent floor palette; do not scatter darker alternate boards.
    td = TileData(cfg.client_data)
    floor_ids = {p.item for p in parts if p.z in side["storeys"] and td.static(p.item)["name"] in ("wooden boards", "wooden floor") and td.static(p.item)["height"] == 0}
    floor_id = cat.floor(desc["materials"]["floor"])[0]
    parts = [replace(p, item=floor_id) if p.item in floor_ids else p for p in parts]
    problems = validate.validate(parts, side, cfg.client_data)
    if problems:
        raise SystemExit("\n".join(problems))
    side.update(valid=True, problems=[], components=len(parts))
    built = cfg.build / "multi/built/layered_tavern"
    built.mkdir(parents=True, exist_ok=True)
    (built / "components.json").write_text(json.dumps([p.as_list() for p in parts], indent=1))
    (built / "multi.json").write_text(json.dumps(side, indent=1))
    render.render(parts, cfg.client_data, built / "preview.png")
    render.render(parts, cfg.client_data, built / "preview_noroof.png", max_z=46)
    render.render(parts, cfg.client_data, built / "preview_ground.png", max_z=26)
    names = ["Foundation", "Yard", "Tavern hall", "Upstairs rooms", "Stairs", "Roof", "Furnishings"]
    cx, cy = side["centre"]
    decor = {(int(d["item"], 0), d["at"][0]-cx, d["at"][1]-cy,
              d.get("z", 0)+(7+20*d["storey"] if "storey" in d else 0)) for d in desc["decor"]}
    stair_cells = {(x-cx, y-cy) for s in side["local"]["stairs"] for x,y in s["cells"]}
    membership = []
    for p in parts:
        if (p.item,p.x,p.y,p.z) in decor: layer = 6
        elif p.z >= side["roof_z"]: layer = 5
        elif not (-cx <= p.x <= 12-cx and -cy <= p.y <= 10-cy): layer = 1
        elif p.z < 7: layer = 0
        elif (p.x,p.y) in stair_cells and p.item not in floor_ids: layer = 4
        elif p.z >= 27: layer = 3
        else: layer = 2
        membership.append(layer)
    document = dict(format=1, kind="components", name="layered_tavern",
        components=[[p.item,p.x,p.y,p.z,int(p.visible),0] for p in parts],
        editor_layers=dict(version=1,
            layers=[dict(Id=i,Name=n,Visible=True,Locked=False) for i,n in enumerate(names)],
            membership=membership))
    out = cfg.build / "multi/edit/layered_tavern.multi.json"
    out.parent.mkdir(parents=True,exist_ok=True)
    out.write_text(json.dumps(document,indent=2))
    (built / "provenance.json").write_text(json.dumps(dict(
        recipe_sha256=hashlib.sha256(recipe.read_bytes()).hexdigest(),
        catalogue_sha256=hashlib.sha256((cfg.build/"multi/catalogue/families.json").read_bytes()).hexdigest(),
        source="Local UO mined construction families; original demonstration recipe, not a Zomboid reconstruction",
        coherent_floor_id=floor_id, layer_counts={n:membership.count(i) for i,n in enumerate(names)}),indent=2))
    print(f"Validated {len(parts)} components, two storeys and yard: {out}")
if __name__ == "__main__":
    main()
