"""Read-only CDDA census and conservative dependency analysis, NOT mapgen evaluation."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

PARSER_VERSION = "cdda-census-1"
SCOPES = ("data/json", "data/mods", "mods")
IMPLEMENTATION = ("doc/JSON/MAPGEN.md", "doc/JSON/OVERMAP.md",
                  "doc/JSON/JSON_Mapping_Guides/JSON_ROOF_MAPGEN.md",
                  "src/mapgen.cpp", "src/submap_export.h", "src/submap_export.cpp",
                  "src/worldgen_export.h", "src/worldgen_export.cpp", "src/main.cpp")


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def strings(value):
    """Flatten selector arrays, not arbitrary expressions."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, list):
        for part in value:
            yield from strings(part)


def identities(entry):
    kind = entry["type"]
    for key in ("id", "abstract"):
        for value in strings(entry.get(key)):
            yield kind, value
    if kind == "mapgen":
        for key in ("om_terrain", "nested_mapgen_id", "update_mapgen_id"):
            for value in strings(entry.get(key)):
                yield key, value


def dependencies(entry):
    """Literal palette/inheritance/predecessor/nested references with expression lineage.

    Dynamic values remain opaque. This does not expand parameters or infer runtime
    ordering. Palette chunk references can occur in symbol mappings or placements.
    """
    if "copy-from" in entry:
        yield "/copy-from", "inheritance", entry["type"], entry["copy-from"]

    def walk(obj, pointer):
        if isinstance(obj, dict):
            for key, value in obj.items():
                p = pointer + "/" + key.replace("~", "~0").replace("/", "~1")
                if key == "palettes" and isinstance(value, list):
                    for i, expr in enumerate(value):
                        yield f"{p}/{i}", "palette", "palette", expr
                elif key in ("predecessor_mapgen", "fallback_predecessor_mapgen"):
                    yield p, key, "om_terrain", value
                elif key in ("chunks", "else_chunks") and isinstance(value, list):
                    for i, expr in enumerate(value):
                        # Weighted chunk entry [value, weight]; preserve the complete expression.
                        yield f"{p}/{i}", key, "nested_mapgen_id", expr
                else:
                    yield from walk(value, p)
        elif isinstance(obj, list):
            for i, value in enumerate(obj):
                yield from walk(value, f"{pointer}/{i}")
    if entry["type"] in ("mapgen", "palette"):
        yield from walk(entry, "")


def literal(expr, kind):
    if isinstance(expr, str):
        return expr
    if kind in ("chunks", "else_chunks") and isinstance(expr, list) and len(expr) == 2:
        if isinstance(expr[0], str) and isinstance(expr[1], (int, float)):
            return expr[0]
    return None


def scan(con, source: Path, version_claim=None):
    source = source.resolve()
    if not (source / "data/json").is_dir():
        raise ValueError("source must contain data/json")
    license_text = (source / "LICENSE.txt").read_text(encoding="utf-8-sig")
    files = []
    for scope in SCOPES:
        for path in sorted((source / scope).rglob("*.json")):
            if path.is_symlink():
                raise ValueError(f"symlink source file refused: {path}")
            raw = path.read_bytes()
            rel = path.relative_to(source).as_posix()
            namespace = "dda" if scope == "data/json" else "unidentified:" + rel.rsplit("/", 1)[0]
            try:
                payload = json.loads(raw.decode("utf-8-sig"))
                error = None
            except (UnicodeError, json.JSONDecodeError) as exc:
                payload, error = [], str(exc)
            files.append([rel, hashlib.sha256(raw).hexdigest(), namespace, payload, error])
    # Namespace follows MOD_INFO identity, not a folder basename. Ambiguous roots
    # remain unselected rather than silently mixing worlds.
    roots = {}
    for rel, _, _, payload, _ in files:
        for entry in payload if isinstance(payload, list) else [payload]:
            if isinstance(entry, dict) and entry.get("type") in ("MOD_INFO", "mod_info"):
                ids = list(strings(entry.get("id")))
                if len(ids) == 1:
                    roots.setdefault(rel.rsplit("/", 1)[0], set()).add(ids[0])
    for file in files:
        if file[0].startswith("data/json/"):
            continue
        matches = [root for root in roots if file[0].startswith(root + "/")]
        if matches:
            ids = roots[max(matches, key=len)]
            if len(ids) == 1:
                file[2] = next(iter(ids))
    impl = {rel: hashlib.sha256((source / rel).read_bytes()).hexdigest()
            for rel in IMPLEMENTATION if (source / rel).is_file()}
    snapshot = digest({"files": [[f[0], f[1], f[2]] for f in files], "license": license_text,
                       "implementation": impl, "parser": PARSER_VERSION, "scopes": SCOPES,
                       "version_claim": version_claim})
    with con:
        con.execute("INSERT OR IGNORE INTO source_snapshot VALUES(?,?,?,?,?,?,?,?)",
                    (snapshot, "cdda", PARSER_VERSION, version_claim, "unverified",
                     license_text, canonical(SCOPES), canonical(impl)))
        for rel, sha, namespace, payload, error in files:
            con.execute("INSERT OR IGNORE INTO source_file VALUES(?,?,?,?,?)",
                        (snapshot, rel, sha, namespace, error))
            entries = enumerate(payload) if isinstance(payload, list) else [(None, payload)]
            for i, entry in entries:
                if not isinstance(entry, dict) or not isinstance(entry.get("type"), str):
                    continue
                pointer = "" if i is None else f"/{i}"
                ident = digest([snapshot, rel, pointer])
                selectors = {k: entry[k] for k in ("id", "abstract", "copy-from", "om_terrain",
                             "nested_mapgen_id", "update_mapgen_id") if k in entry}
                body = entry.get("object", {})
                rows = body.get("rows") if isinstance(body, dict) else None
                widths = [len(row) for row in rows] if isinstance(rows, list) and all(
                    isinstance(row, str) for row in rows) else None
                inserted = con.execute("INSERT OR IGNORE INTO definition VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                    (ident, snapshot, rel, pointer, namespace, entry["type"], canonical(selectors),
                     canonical(entry), digest(entry), canonical(widths) if widths is not None else None,
                     canonical(entry["weight"]) if "weight" in entry else None)).rowcount
                if not inserted:
                    continue
                con.executemany("INSERT OR IGNORE INTO identity VALUES(?,?,?)",
                                [(ident, k, v) for k, v in identities(entry)])
                con.executemany("INSERT INTO dependency(definition_id,pointer,kind,target_kind,target_id,expression_json) VALUES(?,?,?,?,?,?)",
                    [(ident, p, k, t, literal(e, k), canonical(e)) for p, k, t, e in dependencies(entry)])
    return snapshot


def make_profile(con, snapshot, requested):
    if not con.execute("SELECT 1 FROM source_snapshot WHERE id=?", (snapshot,)).fetchone():
        raise ValueError("unknown snapshot")
    requested = list(dict.fromkeys(requested))
    infos = {}
    for row in con.execute("SELECT raw_json FROM definition WHERE snapshot_id=? AND type IN ('MOD_INFO','mod_info')", (snapshot,)):
        entry = json.loads(row[0])
        for mod in strings(entry.get("id")):
            infos.setdefault(mod, []).append(entry)
    diagnostics, order, visiting, done = [], [], set(), set()

    def visit(mod):
        if mod in visiting:
            diagnostics.append(f"mod dependency cycle: {mod}")
            return
        if mod in done:
            return
        visiting.add(mod)
        matches = infos.get(mod, [])
        if not matches and mod == "dda":
            matches = [{"dependencies": []}]  # Core may have no MOD_INFO in small fixtures.
        if len(matches) != 1:
            diagnostics.append(f"mod {mod}: expected one MOD_INFO, found {len(matches)}")
        else:
            deps = matches[0].get("dependencies", [])
            if not isinstance(deps, list) or not all(isinstance(dep, str) for dep in deps):
                diagnostics.append(f"mod {mod}: unsupported dependencies expression")
            else:
                for dep in deps:
                    visit(dep)
        visiting.remove(mod)
        done.add(mod)
        order.append(mod)

    visit("dda")
    for mod in requested:
        visit(mod)
    errors = con.execute("SELECT path FROM source_file WHERE snapshot_id=? AND parse_error IS NOT NULL", (snapshot,)).fetchall()
    # A malformed modinfo cannot be reliably assigned to its intended namespace.
    if errors:
        diagnostics.append(f"snapshot contains {len(errors)} parse errors; profile blocked")
    profile = digest([snapshot, requested, order, PARSER_VERSION])
    with con:
        con.execute("INSERT OR IGNORE INTO profile VALUES(?,?,?,?,?,?)", (profile, snapshot,
                    canonical(requested), canonical(order), "blocked" if diagnostics else "indexed",
                    canonical(diagnostics)))
        # Results are immutable for a given source/profile/parser version.
        if con.execute("SELECT 1 FROM coverage WHERE profile_id=?", (profile,)).fetchone():
            return profile
        rows = con.execute("SELECT * FROM definition WHERE snapshot_id=?", (snapshot,)).fetchall()
        selected = {r["id"] for r in rows if r["namespace"] in order}
        index = {}
        for row in con.execute("SELECT i.*,d.namespace FROM identity i JOIN definition d ON d.id=i.definition_id WHERE d.snapshot_id=?", (snapshot,)):
            if row["definition_id"] in selected:
                index.setdefault((row["kind"], row["value"]), []).append((row["definition_id"], order.index(row["namespace"])))
        graph = {ident: set() for ident in selected}
        problems = {}
        for dep in con.execute("SELECT dep.* FROM dependency dep JOIN definition d ON d.id=dep.definition_id WHERE d.snapshot_id=?", (snapshot,)).fetchall():
            ident = dep["definition_id"]
            if ident not in selected:
                continue
            candidates = index.get((dep["target_kind"], dep["target_id"]), [])
            if dep["target_id"] is None:
                status, reason = "dynamic", "expression requires mapgen evaluation"
            elif dep["target_id"] == "null" and dep["kind"] in ("chunks", "else_chunks"):
                status, reason = "no-op", "explicit empty nested selection"
            elif not candidates:
                status, reason = "missing", f"no selected {dep['target_kind']}:{dep['target_id']}"
            else:
                status = "literal-candidates"
                reason = "candidates retained; overrides, inheritance and weighted variants not evaluated"
                # Keep every candidate; load order is evidence, not an override resolver.
                for target, rank in candidates:
                    con.execute("INSERT INTO dependency_candidate VALUES(?,?,?,?)", (profile, dep["id"], target, rank))
                    graph[ident].add(target)
            con.execute("INSERT INTO dependency_result VALUES(?,?,?,?)", (profile, dep["id"], status, reason))
            if status in ("missing", "dynamic"):
                problems.setdefault(ident, []).append(reason)
        cyclic = cycles(graph)
        for row in rows:
            raw = json.loads(row["raw_json"])
            if row["id"] not in selected:
                category, status, reason = "unselected", "excluded", "namespace outside selected mod profile"
            else:
                kind = row["type"]
                category = ("reusable-chunk-candidate" if "nested_mapgen_id" in raw else
                            "update-dependency" if "update_mapgen_id" in raw else
                            "mapgen-unclassified" if kind == "mapgen" else "supporting-definition")
                reasons = problems.get(row["id"], []).copy()
                if row["id"] in cyclic:
                    reasons.append("dependency candidate cycle; engine review required")
                if diagnostics:
                    reasons.extend(diagnostics)
                status = "blocked" if reasons else "indexed-not-resolved"
                reason = "; ".join(reasons) or "raw source only; geometry and building eligibility pending"
            con.execute("INSERT INTO coverage VALUES(?,?,?,?,?)", (profile, row["id"], category, status, reason))
    return profile


def cycles(graph):
    """Iterative Kosaraju SCC: avoid Python recursion limits on full catalogues."""
    seen, finished = set(), []
    reverse = {v: set() for v in graph}
    for v, targets in graph.items():
        for target in targets:
            reverse[target].add(v)
    for start in graph:
        if start in seen:
            continue
        stack = [(start, False)]
        while stack:
            v, finish = stack.pop()
            if finish:
                finished.append(v)
            elif v not in seen:
                seen.add(v)
                stack.append((v, True))
                stack.extend((t, False) for t in graph[v] if t not in seen)
    seen, cyclic = set(), set()
    for start in reversed(finished):
        if start in seen:
            continue
        group, stack = set(), [start]
        while stack:
            v = stack.pop()
            if v in seen:
                continue
            seen.add(v)
            group.add(v)
            stack.extend(reverse[v] - seen)
        if len(group) > 1 or start in graph[start]:
            cyclic.update(group)
    return cyclic


def coverage(con, profile):
    row = con.execute("SELECT * FROM profile WHERE id=?", (profile,)).fetchone()
    if row is None:
        raise ValueError("unknown profile")
    snapshot = row["snapshot_id"]
    return {"format": 1, "snapshot": snapshot, "profile": profile,
            "load_order": json.loads(row["load_order_json"]), "profile_status": row["status"],
            "diagnostics": json.loads(row["diagnostics_json"]),
            "files": con.execute("SELECT COUNT(*) FROM source_file WHERE snapshot_id=?", (snapshot,)).fetchone()[0],
            "definition_counts": dict(con.execute("SELECT type,COUNT(*) FROM definition WHERE snapshot_id=? GROUP BY type ORDER BY type", (snapshot,))),
            "ledger": [dict(r) for r in con.execute("SELECT category,status,COUNT(*) AS count FROM coverage WHERE profile_id=? GROUP BY category,status ORDER BY category,status", (profile,))],
            "dependencies": dict(con.execute("SELECT status,COUNT(*) FROM dependency_result WHERE profile_id=? GROUP BY status ORDER BY status", (profile,))),
            "sqlite_integrity": con.execute("PRAGMA integrity_check").fetchone()[0],
            "foreign_key_errors": [list(r) for r in con.execute("PRAGMA foreign_key_check")],
            "generated_instances": con.execute("SELECT COUNT(DISTINCT b.instance_id) FROM build b JOIN layout_instance i ON i.id=b.instance_id WHERE i.profile_id=? AND b.status='native-valid'", (profile,)).fetchone()[0],
            "gameplay_proven_instances": con.execute("SELECT COUNT(DISTINCT b.instance_id) FROM validation v JOIN build b ON b.id=v.build_id JOIN layout_instance i ON i.id=b.instance_id WHERE i.profile_id=? AND v.kind='gameplay' AND json_extract(v.result_json,'$.passed')=1", (profile,)).fetchone()[0],
            "limitations": ["Dependency census only; no engine semantics or override resolution.",
                            "Codepoint lengths are observations, not engine display widths.",
                            "Mapgen records are not independent buildings.",
                            "Census dependencies are not an engine selection trace; instance conversions are tracked separately.",
                            "Supported native instances do not establish exhaustive variant coverage."]}
