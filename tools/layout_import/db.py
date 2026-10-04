"""Durable layout catalogue, deliberately independent of the rebuildable decor DB."""
from __future__ import annotations

import sqlite3
from pathlib import Path

SCHEMA_VERSION = 3
MIGRATIONS = {1: """
CREATE TABLE source_snapshot (
 id TEXT PRIMARY KEY, adapter TEXT NOT NULL, parser_version TEXT NOT NULL,
 source_version_claim TEXT, source_state TEXT NOT NULL, license_text TEXT NOT NULL,
 scan_scope_json TEXT NOT NULL, implementation_hashes_json TEXT NOT NULL);
CREATE TABLE source_file (
 snapshot_id TEXT NOT NULL REFERENCES source_snapshot(id), path TEXT NOT NULL,
 sha256 TEXT NOT NULL, namespace TEXT NOT NULL, parse_error TEXT,
 PRIMARY KEY(snapshot_id,path));
CREATE TABLE definition (
 id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL, source_path TEXT NOT NULL,
 pointer TEXT NOT NULL, namespace TEXT NOT NULL, type TEXT NOT NULL,
 selectors_json TEXT NOT NULL, raw_json TEXT NOT NULL, content_hash TEXT NOT NULL,
 row_codepoints_json TEXT, weight_json TEXT,
 FOREIGN KEY(snapshot_id,source_path) REFERENCES source_file(snapshot_id,path),
 UNIQUE(snapshot_id,source_path,pointer));
CREATE TABLE identity (
 definition_id TEXT NOT NULL REFERENCES definition(id), kind TEXT NOT NULL,
 value TEXT NOT NULL, PRIMARY KEY(definition_id,kind,value));
CREATE TABLE dependency (
 id INTEGER PRIMARY KEY, definition_id TEXT NOT NULL REFERENCES definition(id),
 pointer TEXT NOT NULL, kind TEXT NOT NULL, target_kind TEXT NOT NULL,
 target_id TEXT, expression_json TEXT NOT NULL);
CREATE TABLE profile (
 id TEXT PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES source_snapshot(id),
 requested_json TEXT NOT NULL, load_order_json TEXT NOT NULL,
 status TEXT NOT NULL, diagnostics_json TEXT NOT NULL);
CREATE TABLE dependency_candidate (
 profile_id TEXT NOT NULL REFERENCES profile(id), dependency_id INTEGER NOT NULL REFERENCES dependency(id),
 target_definition_id TEXT NOT NULL REFERENCES definition(id), load_order INTEGER NOT NULL,
 PRIMARY KEY(profile_id,dependency_id,target_definition_id));
CREATE TABLE dependency_result (
 profile_id TEXT NOT NULL REFERENCES profile(id), dependency_id INTEGER NOT NULL REFERENCES dependency(id),
 status TEXT NOT NULL, diagnostic TEXT NOT NULL,
 PRIMARY KEY(profile_id,dependency_id));
CREATE TABLE coverage (
 profile_id TEXT NOT NULL REFERENCES profile(id), definition_id TEXT NOT NULL REFERENCES definition(id),
 category TEXT NOT NULL, status TEXT NOT NULL, reason TEXT NOT NULL,
 PRIMARY KEY(profile_id,definition_id));
CREATE INDEX definition_type ON definition(snapshot_id,type,namespace);
CREATE INDEX identity_target ON identity(kind,value);
CREATE INDEX dependency_target ON dependency(target_kind,target_id);
CREATE INDEX coverage_status ON coverage(profile_id,status,category);
""", 2: """
CREATE TABLE layout_template (
 hash TEXT PRIMARY KEY, format INTEGER NOT NULL, geometry_json TEXT NOT NULL);
CREATE TABLE layout_instance (
 id TEXT PRIMARY KEY, template_hash TEXT NOT NULL REFERENCES layout_template(hash),
 profile_id TEXT NOT NULL REFERENCES profile(id), name TEXT NOT NULL,
 provenance_json TEXT NOT NULL, layout_json TEXT NOT NULL);
CREATE TABLE room_template (
 instance_id TEXT NOT NULL REFERENCES layout_instance(id), level INTEGER NOT NULL,
 room_id TEXT NOT NULL, cells_json TEXT NOT NULL, bounds_json TEXT NOT NULL,
 area INTEGER NOT NULL, semantic_use TEXT NOT NULL, label_origin TEXT NOT NULL,
 confidence REAL NOT NULL, evidence_json TEXT NOT NULL,
 PRIMARY KEY(instance_id,level,room_id));
CREATE TABLE opening (
 instance_id TEXT NOT NULL REFERENCES layout_instance(id), level INTEGER NOT NULL,
 x INTEGER NOT NULL, y INTEGER NOT NULL, kind TEXT NOT NULL,
 adjacency_json TEXT NOT NULL, access_state TEXT NOT NULL, exterior INTEGER NOT NULL,
 PRIMARY KEY(instance_id,level,x,y));
CREATE TABLE theme_profile (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, version TEXT NOT NULL, profile_json TEXT NOT NULL);
CREATE TABLE build (
 id TEXT PRIMARY KEY, instance_id TEXT NOT NULL REFERENCES layout_instance(id),
 theme_id TEXT NOT NULL REFERENCES theme_profile(id), converter_hash TEXT NOT NULL,
 status TEXT NOT NULL, result_json TEXT NOT NULL);
CREATE TABLE validation (
 build_id TEXT NOT NULL REFERENCES build(id), kind TEXT NOT NULL,
 evidence_hash TEXT NOT NULL, result_json TEXT NOT NULL,
 PRIMARY KEY(build_id,kind,evidence_hash));
CREATE TABLE usage_event (
 id TEXT PRIMARY KEY, build_id TEXT NOT NULL REFERENCES build(id),
 project TEXT NOT NULL, region TEXT NOT NULL, placement_json TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN ('placed','removed','superseded')), timestamp TEXT NOT NULL);
CREATE INDEX room_semantic_use ON room_template(semantic_use,area);
CREATE INDEX build_status ON build(status,theme_id);
CREATE INDEX usage_project ON usage_event(project,region,state);
""", 3: """
CREATE TABLE layout_level (
 instance_id TEXT NOT NULL REFERENCES layout_instance(id), z INTEGER NOT NULL,
 width INTEGER NOT NULL, height INTEGER NOT NULL, record_json TEXT NOT NULL,
 PRIMARY KEY(instance_id,z));
CREATE TABLE cell (
 instance_id TEXT NOT NULL, level INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL,
 role TEXT NOT NULL, terrain_id TEXT NOT NULL, furniture_id TEXT NOT NULL,
 record_json TEXT NOT NULL, PRIMARY KEY(instance_id,level,x,y),
 FOREIGN KEY(instance_id,level) REFERENCES layout_level(instance_id,z));
CREATE TABLE furnishing_group (
 instance_id TEXT NOT NULL, level INTEGER NOT NULL, group_hash TEXT NOT NULL,
 role TEXT NOT NULL, source_id TEXT NOT NULL, record_json TEXT NOT NULL,
 PRIMARY KEY(instance_id,level,group_hash),
 FOREIGN KEY(instance_id,level) REFERENCES layout_level(instance_id,z));
CREATE TABLE theme_mapping (
 build_id TEXT NOT NULL REFERENCES build(id), mapping_hash TEXT NOT NULL,
 source_role TEXT NOT NULL, status TEXT NOT NULL, record_json TEXT NOT NULL,
 PRIMARY KEY(build_id,mapping_hash));
CREATE TABLE artifact (
 build_id TEXT NOT NULL REFERENCES build(id), kind TEXT NOT NULL, path TEXT NOT NULL,
 sha256 TEXT NOT NULL, PRIMARY KEY(build_id,kind,path,sha256));
CREATE INDEX cell_semantics ON cell(role,terrain_id,furniture_id);
CREATE INDEX furnishing_role ON furnishing_group(role,source_id);
CREATE INDEX mapping_status ON theme_mapping(status,source_role);
"""}


def open_db(path: Path) -> sqlite3.Connection:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(path)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA foreign_keys=ON")
    version = con.execute("PRAGMA user_version").fetchone()[0]
    if version > SCHEMA_VERSION:
        con.close()
        raise ValueError(f"unsupported catalogue schema {version}")
    if version == 0 and con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchone():
        con.close()
        raise ValueError("unversioned database: use a new output path; the handoff census is read-only")
    try:
        for target in range(version + 1, SCHEMA_VERSION + 1):
            con.executescript("BEGIN IMMEDIATE;\n" + MIGRATIONS[target]
                              + f"\nPRAGMA user_version={target};\nCOMMIT;")
    except Exception:
        con.close()
        raise
    return con
