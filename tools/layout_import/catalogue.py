"""Immutable semantic instances, build evidence and idempotent usage events."""
from layout_import.cdda import canonical, digest


def save_layout(con, profile, layout):
    # The explicit inventory is authoritative, including unresolved and stacked
    # objects. Reconstructing it from per-cell room-label hints loses both.
    from layout_import.pz_objects import CONTRACT
    contract = layout.get('furnishing_contract')
    source_inventory = {}
    if contract is not None or any('furnishing_groups' in level for level in layout['levels']):
        if contract != CONTRACT or any('furnishing_groups' not in level for level in layout['levels']):
            raise ValueError('source furnishing inventory and contract disagree')
        from layout_import.native import groups
        source_inventory = {level['z']: groups(level) for level in layout['levels']}
    topology = {"width": layout["width"], "height": layout["height"],
                "levels": [{"z": level["z"], "cells": [[c["x"], c["y"], c["role"], c["indoors"],
                          c["furnishing_role"], c["access"]] for c in level["cells"]]} for level in layout["levels"]]}
    instance = digest([profile, layout["layout_hash"], layout["provenance"], layout.get("adaptations", []),
                       [{"z":l["z"],"cells":l["cells"],"rooms":l.get("rooms",[])} for l in layout["levels"]]])
    if source_inventory and con.execute('SELECT 1 FROM layout_instance WHERE id=?', (instance,)).fetchone():
        expected = {(z, digest(group), group['role'], group['source_id'], canonical(group)) for z, groups in source_inventory.items() for group in groups}
        retained = {tuple(r) for r in con.execute(
            'SELECT level,group_hash,role,source_id,record_json FROM furnishing_group WHERE instance_id=?', (instance,))}
        if expected != retained:
            raise ValueError('retained source inventory differs; regenerate with a new converter profile')
    with con:
        con.execute("INSERT OR IGNORE INTO layout_template VALUES(?,?,?)", (layout["layout_hash"], 1, canonical(topology)))
        con.execute("INSERT OR IGNORE INTO layout_instance VALUES(?,?,?,?,?,?)",
                    (instance, layout["layout_hash"], profile, layout["name"], canonical(layout["provenance"]), canonical(layout)))
        for level in layout["levels"]:
            con.execute("INSERT OR IGNORE INTO layout_level VALUES(?,?,?,?,?)",(instance,level["z"],layout["width"],layout["height"],canonical(level)))
            for cell in level["cells"]:
                con.execute("INSERT OR IGNORE INTO cell VALUES(?,?,?,?,?,?,?,?)",(instance,level["z"],cell["x"],cell["y"],
                            cell["role"],cell["terrain"],cell["furniture"],canonical(cell)))
            if contract == CONTRACT:
                inventory = source_inventory[level['z']]
            else:
                # Legacy geometry-only grouping keeps its historical records.
                from decorate import rooms
                grouped, inventory = {}, []
                for cell in level["cells"]:
                    if cell["furnishing_role"]:
                        grouped.setdefault((cell["furnishing_role"],cell["furniture"]),set()).add((cell["x"],cell["y"]))
                for (role,source_id),positions in sorted(grouped.items()):
                    for component in rooms.components(positions):
                        inventory.append({"role":role,"source_id":source_id,"cells":[list(p) for p in sorted(component)]})
            for group in inventory:
                con.execute("INSERT OR IGNORE INTO furnishing_group VALUES(?,?,?,?,?,?)",
                            (instance,level["z"],digest(group),group['role'],group['source_id'],canonical(group)))
            for room in level.get("rooms", []):
                con.execute("INSERT OR IGNORE INTO room_template VALUES(?,?,?,?,?,?,?,?,?,?)",
                    (instance, level["z"], room["id"], canonical(room["cells"]), canonical(room["bounds"]),
                     room["area"], room["use"], room["label_origin"], room["confidence"], canonical(room["evidence"])))
            for opening in level.get("openings", []):
                con.execute("INSERT OR IGNORE INTO opening VALUES(?,?,?,?,?,?,?,?)",
                    (instance, level["z"], *opening["at"], opening["kind"], canonical(opening["rooms"]),
                     opening["access"], int(opening["exterior"])))
    return instance


def save_build(con, instance, theme, converter_hash, result):
    theme_id = digest(theme)
    build_id = digest([instance, theme_id, converter_hash, result])
    with con:
        con.execute("INSERT OR IGNORE INTO theme_profile VALUES(?,?,?,?)",
                    (theme_id, theme["name"], str(theme["version"]), canonical(theme)))
        con.execute("INSERT OR IGNORE INTO build VALUES(?,?,?,?,?,?)",
                    (build_id, instance, theme_id, converter_hash, result["status"], canonical(result)))
        for mapping in result.get("furnishing_mappings",[]):
            con.execute("INSERT OR IGNORE INTO theme_mapping VALUES(?,?,?,?,?)",(build_id,digest(mapping),mapping["role"],mapping["status"],canonical(mapping)))
    return build_id


def save_artifacts(con, build_id, kind, paths):
    import hashlib
    from pathlib import Path
    with con:
        for path in paths:
            path = Path(path).resolve()
            if not path.is_file():
                raise ValueError(f"missing evidence artifact: {path}")
            sha = hashlib.sha256(path.read_bytes()).hexdigest()
            con.execute("INSERT OR IGNORE INTO artifact VALUES(?,?,?,?)",(build_id,kind,str(path),sha))


def save_validation(con, build_id, kind, result):
    with con:
        con.execute("INSERT OR IGNORE INTO validation VALUES(?,?,?,?)",
                    (build_id, kind, digest(result), canonical(result)))


def usage(con, event):
    values = (event["id"], event["build"], event["project"], event["region"],
              canonical(event["placement"]), event["state"], event["timestamp"])
    prior = con.execute("SELECT * FROM usage_event WHERE id=?", (event["id"],)).fetchone()
    if prior and tuple(prior) != values:
        raise ValueError("usage event ID already exists with different content")
    with con:
        con.execute("INSERT OR IGNORE INTO usage_event VALUES(?,?,?,?,?,?,?)", values)
