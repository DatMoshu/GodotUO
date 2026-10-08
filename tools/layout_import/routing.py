"""Native movement routes, short proof legs and district frontage metrics."""
from __future__ import annotations

import collections

from layout_import import native
from guo.uoread import TileData


def tile_info(comps, data):
    td = TileData(data)
    return {f"{item:#06x}": {"flags":native.pieces.flag_names(td.static(item)["flags"]),
                            "height":td.static(item)["height"]}
            for item in {c.item for c in comps} if td.static(item)}


def short_tour(parts, stops, data, ground=0, max_steps=12):
    """Use the canonical offline pathfinder to add actual reachable waypoints."""
    info = tile_info([c for part in parts for c in part["comps"]],data)
    stand, covered = native.walkcheck.surfaces(parts,info,ground)
    out, problems = [], []
    for stop in stops:
        if out:
            a = out[-1]
            trail = []
            got = native.walkcheck.search(stand,covered,(a["x"],a["y"],a["z"]),
                                         (stop["x"],stop["y"],stop["z"]),ground,4,trail)
            if got is None:
                problems.append(f"no native route from {a['name']} to {stop['name']}")
            else:
                for i in range(max_steps,len(trail)-1,max_steps):
                    x,y,z = trail[i]
                    out.append({"name":f"{stop['name']}_waypoint{i}","x":x,"y":y,"z":z})
        if not out or (out[-1]["x"],out[-1]["y"],out[-1]["z"]) != (stop["x"],stop["y"],stop["z"]):
            out.append(stop)
    return out,problems


def streets(road_cells, parcel_roads, bounds, entrances, walk, require_frontage=True):
    """Typed edge sockets, connectivity and frontage on the resolved planner grid."""
    from decorate import rooms as R
    from layout_import.district import exterior_land
    components = R.components(set(road_cells))
    main = max(components,key=len,default=set())
    problems, sockets, frontages = [], [], []
    x0,y0,x1,y1 = bounds
    for (ax,ay),cells in sorted(parcel_roads.items()):
        for side, neighbor, edge, opposite in (
            ("E",(ax+1,ay),{y for x,y in cells if x==23},{y for x,y in parcel_roads.get((ax+1,ay),set()) if x==0}),
            ("S",(ax,ay+1),{x for x,y in cells if y==23},{x for x,y in parcel_roads.get((ax,ay+1),set()) if y==0})):
            if not edge and not opposite:
                continue
            boundary = not (x0<=neighbor[0]<=x1 and y0<=neighbor[1]<=y1)
            matched = edge & opposite
            sockets.append({"parcel":[ax,ay],"neighbor":list(neighbor),"side":side,"type":"street",
                            "edge_cells":sorted(edge),"matching_cells":sorted(matched),"boundary_exit":boundary})
            # A public street mouth needs a matching neighbor. Small paved
            # doorstep/gate fragments are frontage, not mandatory streets.
            if not boundary and len(edge)>=4 and len(opposite)>=4 and not matched:
                problems.append(f"street socket {(ax,ay)} {side} has no matching mouth")
    for entrance in entrances:
        at = tuple(entrance["at"])
        reached = R.reachable(at,set(walk)) if at in walk else set()
        matched = reached & main
        frontages.append({**entrance,"public_network_reachable":bool(matched)})
        if not matched and require_frontage:
            problems.append(f"building entrance {entrance['part']} has no public-network route")
    return {"road_cells":len(road_cells),"road_components":len(components),"largest_component":len(main),
            "sockets":sockets,"frontages":frontages,"passed":not problems,"problems":problems}
