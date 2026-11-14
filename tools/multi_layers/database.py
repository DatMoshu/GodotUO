"""Read the shared building census and compile a traceable, layered native multi.

No source database/install writes. Uses canonical PZ resolver, native compiler,
mined UO families, furniture placer, yard/roof generators and movement checks.
"""
from __future__ import annotations
from contextlib import closing
import argparse
import copy
import hashlib
import json
from pathlib import Path
import sqlite3
import re
import sys
from dataclasses import replace

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "tools"), str(ROOT / "tools/multi")]
from guo import load_config
from guo.uoread import Art, TileData
from layout_import import zomboid, native, semantic
from decorate.decorate import Template
import generate as G
from multifile import Component


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def read_source(db, source_id):
    with closing(sqlite3.connect(Path(db).resolve().as_uri() + "?mode=ro", uri=True)) as con:
        snapshots = dict(con.execute("SELECT source,snapshot FROM current_snapshot"))
        snap = snapshots["project-zomboid"]
        row = con.execute("SELECT analysis FROM building WHERE snapshot=? AND source_id=?", (snap, source_id)).fetchone()
        if row is None:
            raise ValueError("Building is absent from the current Zomboid snapshot")
        building = json.loads(row[0])
        rooms = [json.loads(r[0]) for r in con.execute("SELECT geometry FROM room WHERE snapshot=? AND source_id=? ORDER BY room_id", (snap, source_id))]
        if sorted(rooms, key=lambda r:r["id"]) != sorted(building["rooms"], key=lambda r:r["id"]):
            raise ValueError("Room table and building analysis disagree")
        if building["levels"] != [0, 1]:
            raise ValueError("This milestone requires exactly ground and first floors")
        patterns = {kind: {k: json.loads(v) for k,v in con.execute("SELECT key,data FROM pattern WHERE snapshot=? AND kind=? ORDER BY key", (snapshots["ultima-online"],kind))} for kind in ("families", "pieces")}
        if not all(patterns.values()):
            raise ValueError("Current UO snapshot lacks mined construction patterns")
        return building, rooms, snapshots, patterns


def verify_header(source, building):
    path = Path(source)/"media/maps"/building["map"]/building["header"]
    if not path.resolve().is_relative_to(Path(source).resolve()):
        raise ValueError("Census source path escapes installation")
    if hashlib.sha256(path.read_bytes()).hexdigest() != building["header_sha256"]:
        raise ValueError("Installed lotheader changed since census; rescan before generating")


def themed_bath(layout, library, patterns, data):
    """Explicit visual equivalents; no invented plumbing mechanics."""
    td, art = TileData(data), Art(data)
    selected = {}
    for role, names in {"wash-basin": ["wash basin", "empty tub"], "water-tub": ["water tub", "empty tub"]}.items():
        candidates = sorted({int(k,16) for k,p in patterns["pieces"].items() if p.get("name", "").lower() in names} | {i for i,n in library.names.items() if n.lower() in names})
        candidates = [i for i in candidates if td.static(i)["name"].lower() in names and art.get_static(i)]
        if not candidates:
            raise ValueError(f"No mined/installed native equivalent for {role}")
        item = min(candidates)
        library.templates.append(Template(-item, [[item,0,0,0]],1,1,frozenset({(0,0)}),"",False,{role:1},1,{}))
        selected[role] = item
    for level in layout["levels"]:
        for cell in level["cells"]:
            if cell.get("furnishing_role") == "bath":
                cell["furnishing_role"] = "wash-basin" if "sink" in cell["furniture"].lower() else "water-tub"
    return selected


def add_yard(comps, side, cat, patterns, data):
    cx,cy = side["centre"]
    footprint = set(map(tuple,side["local"]["storeys"][0]["floor"]))
    x0,y0 = min(x for x,y in footprint)-5,min(y for x,y in footprint)-5
    x1,y1 = max(x for x,y in footprint)+5,max(y for x,y in footprint)+5
    entry = next(s for s in side["stops"] if s["name"] == "doorway")
    outside = next(s for s in side["stops"] if s["name"] == "outside")
    dx = (outside["x"]>entry["x"])-(outside["x"]<entry["x"])
    dy = (outside["y"]>entry["y"])-(outside["y"]<entry["y"])
    gx = x1 if dx>0 else x0 if dx<0 else entry["x"]+cx
    gy = y1 if dy>0 else y0 if dy<0 else entry["y"]+cy
    steps = [(s["x"]+cx,s["y"]+cy,s["z"]) for s in side["stops"] if s["name"]=="entrance_step"]
    b=G.Built()
    yard=G.build_yard(b,cat,{"box":[x0,y0,x1,y1],"gate":{"at":[gx,gy]},"height":6,"fence":"wooden fence","path":"paver"},0,footprint,steps)
    # A small designed garden and supply corner, drawn from the mined UO pieces.
    art=Art(data)
    garden=[]
    for name, offsets in {"flowers":[(2,2),(2,3),(3,2)],"wheat":[(2,5),(3,5)],"barrel":[(x1-x0-2,2)]}.items():
        choices=sorted(int(k,16) for k,p in patterns["pieces"].items() if p.get("name","").lower()==name and art.get_static(int(k,16)))
        if not choices: raise ValueError("No mined garden equivalent for "+name)
        for ox,oy in offsets:
            x,y=x0+ox,y0+oy
            if (x,y) in set(map(tuple,yard["path"])) or abs(x-gx)+abs(y-gy)<3: continue
            b.add(choices[0],x,y,0)
            garden.append({"name":name,"item":choices[0],"at":[x,y]})
    side["doors"] += [{**d,"x":d["x"]-cx,"y":d["y"]-cy} for d in b.doors]
    side["local"]["yard"]=yard
    side["stops"]=[{"name":"yard_outside","x":gx+dx-cx,"y":gy+dy-cy,"z":0},
                    {"name":"yard_gate","x":gx-cx,"y":gy-cy,"z":0},
                    {"name":"yard_inside","x":gx-dx-cx,"y":gy-dy-cy,"z":0}]+side["stops"]
    added=[Component(c.item,c.x-cx,c.y-cy,c.z,c.visible) for c in b.comps]
    return comps+added, set((c.item,c.x,c.y,c.z) for c in added), {"kind":"designed-yard","source_boundary":"not present in census; not a claimed original parcel","margin":5,"box":yard["box"],"decor":garden}


def build(args):
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", args.name):
        raise ValueError("Name must be 1-80 letters, digits, underscores or hyphens")
    cfg=load_config()
    out=Path(args.out)
    if out.exists() and any(out.iterdir()):
        raise ValueError("Choose a new output directory; prior builds are preserved")
    for protected in (cfg.client_data, Path(args.source)):
        if out.resolve().is_relative_to(protected.resolve()):
            raise ValueError("Output cannot be inside a source installation")
    building,rooms,snapshots,patterns=read_source(args.db,args.source_id)
    verify_header(args.source,building)
    out.mkdir(parents=True,exist_ok=True)
    catalogue=out/"catalogue";catalogue.mkdir()
    for kind,rows in patterns.items():
        (catalogue/(kind+".json")).write_text(json.dumps(rows),encoding="utf-8")
    cat=G.Catalogue(catalogue)
    layout=zomboid.resolve(args.source,building["map"],building["header"],building["building"],args.name)
    if layout.get("blocked"):
        raise ValueError(layout["blocked"])
    original_hash=layout["layout_hash"]
    library=native.furniture_library(Path(args.decor_db),cfg.client_data)
    equivalents=themed_bath(layout,library,patterns,cfg.client_data)
    theme=copy.deepcopy(native.DEFAULT_THEME)
    theme["materials"]["wall"]="plaster"
    theme["furnishing_roles"].update({k:k for k in equivalents})
    layout["layout_hash"]=semantic.layout_hash(layout)
    comps,side,record=native.compile_layout(layout,cat,library,theme)
    if side["problems"]:
        (out/"blocked.json").write_text(json.dumps(record,indent=2))
        raise ValueError("; ".join(side["problems"]))
    # Uniform flooring affects structural floor cells only, never furniture IDs.
    floor_ids=set(cat.floor("wooden")); floor_id=min(floor_ids)
    structural={(x-side["centre"][0],y-side["centre"][1],s["z"]) for s in side["local"]["storeys"] for x,y in s["floor"]}
    comps=[replace(c,item=floor_id) if c.item in floor_ids and (c.x,c.y,c.z) in structural else c for c in comps]
    # Canonical pitched roof over the source footprint; this is an explicit theme adaptation.
    roof_z=side["roof_z"]
    roof_cells={(c.x,c.y) for c in comps if c.z==roof_z}
    lo_x,lo_y=min(x for x,y in roof_cells),min(y for x,y in roof_cells)
    hi_x,hi_y=max(x for x,y in roof_cells),max(y for x,y in roof_cells)
    b=G.Built()
    # Even width supplies a single ridge; add one overhang cell if needed.
    width=hi_x-lo_x
    width += width%2
    G.roof_rect(b,cat,{"style":"gable","ridge":"y"},theme["materials"],(lo_x-1,lo_y-1,lo_x+width-1,hi_y),roof_z)
    comps=[c for c in comps if c.z<roof_z]+b.comps
    comps,yard_keys,yard=add_yard(comps,side,cat,patterns,cfg.client_data)
    side["components"]=len(comps)
    # Original generator bounds include a margin; enlarge for site selection and the yard.
    side["size"]=[max(c.x for c in comps)-min(c.x for c in comps)+1,max(c.y for c in comps)-min(c.y for c in comps)+1]
    record["adaptations"] += [yard,{"kind":"uniform-native-floor","item":floor_id},{"kind":"native-gable-roof","source_roof_shape_preserved":False}, {"kind":"medieval-wash-fixtures","items":equivalents,"mechanics":"visual substitutes only"}]
    generation={"format":1,"source_id":args.source_id,"snapshots":snapshots,"database_record_hash":digest(building),"pattern_hash":digest(patterns),"decor_database_sha256":hashlib.sha256(Path(args.decor_db).read_bytes()).hexdigest(),"source_rooms":rooms,"source_layout_hash":original_hash,"compiled_layout_hash":layout["layout_hash"],"source_provenance":layout["provenance"],"adaptations":record["adaptations"],"room_masks": [{"level":l["z"],"rooms":l.get("authored_rooms",[])} for l in layout["levels"]],"coordinate_contract":"room_masks are original generation coordinates; subtract centre for document coordinates; immutable provenance, not live constraints","centre":side["centre"]}
    (out/"layout.json").write_text(json.dumps(layout,indent=1),encoding="utf-8")
    if not native.save_native(comps,side,record,out,cfg.client_data):
        raise ValueError("Native validation failed: "+str(record["problems"][:12]))
    cx,cy=side["centre"]
    furnishings={(p[0],p[1]-cx,p[2]-cy,p[3]) for m in record["furnishing_mappings"] if m["status"]=="mapped" for p in m["target_items"]}
    stair_cells={(x-cx,y-cy) for s in side["local"]["stairs"] for x,y in s["cells"]}
    names=["Foundation","Yard","Tavern hall","Upstairs rooms","Stairs","Roof","Furnishings"]
    membership=[]
    for c in comps:
        key=(c.item,c.x,c.y,c.z)
        layer=1 if key in yard_keys else 5 if c.z>=roof_z else 6 if key in furnishings else 4 if (c.x,c.y) in stair_cells and c.item!=floor_id else 0 if c.z<7 else 3 if c.z>=27 else 2
        membership.append(layer)
    doc={"format":1,"kind":"components","name":args.name,"components":[[c.item,c.x,c.y,c.z,int(c.visible),0] for c in comps],"editor_layers":{"version":1,"layers":[{"Id":i,"Name":n,"Visible":True,"Locked":False} for i,n in enumerate(names)],"membership":membership},"generation":generation}
    (out/(args.name+".multi.json")).write_text(json.dumps(doc,indent=1),encoding="utf-8")
    result={"components":len(comps),"doors":len(side["doors"]),"source_rooms":len(rooms),"storeys":side["storeys"],"layers":{n:membership.count(i) for i,n in enumerate(names)},"source_id":args.source_id,"snapshots":snapshots,"geometry_hash":digest(doc["components"]),"status":"native-valid; gameplay not yet run"}
    (out/"generation-report.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for key in ("db","source","source-id","decor-db","name","out"):
        p.add_argument("--"+key,required=True)
    args=p.parse_args()
    try: print(json.dumps(build(args),indent=2))
    except (ValueError,KeyError,sqlite3.Error) as error:
        p.exit(1,str(error)+"\n")

if __name__=="__main__":main()
