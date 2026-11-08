"""Validate, build and query the GUO project fact database.

Canonical input is docs/facts/facts.jsonl (one JSON object per line). The
SQLite file is a generated index, never the source of truth.

    python tools/project_facts/run.py validate
    python tools/project_facts/run.py build --out build/project_facts.sqlite
    python tools/project_facts/run.py query --kind code-derived --category render
    python tools/project_facts/run.py query --id fact.render.lighting-speed-default-off
    python tools/project_facts/run.py examples
"""
from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
FACTS_PATH = ROOT / "docs" / "facts" / "facts.jsonl"
SCHEMA_PATH = ROOT / "docs" / "facts" / "schema.json"

REQUIRED = (
    "id",
    "kind",
    "category",
    "status",
    "statement",
    "source_path",
    "source_line",
    "source_line_end",
    "source_excerpt",
    "source_symbol",
    "source_url",
    "baseline_sha",
    "observed_at",
    "evidence",
    "limitations",
    "stale_policy",
)
KINDS = {"code-derived", "test-result", "historical", "proposal"}
CATEGORIES = {
    "architecture",
    "toolchain",
    "render",
    "network",
    "configuration",
    "security",
    "docs",
    "test",
    "process",
}
STATUSES = {"verified", "inferred", "unknown"}
CLASSIFICATIONS = {
    "cloud-ready",
    "needs-coordination",
    "needs-local-validation",
    "blocked",
}
ID_RE = re.compile(r"^[a-z][a-z0-9]*(\.[a-z0-9-]+)+$")
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
TIME_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
SECRET_RE = re.compile(
    r"(x-access-token|BEGIN [A-Z ]*PRIVATE KEY|AKIA[0-9A-Z]{16}|[A-Za-z]:\\Users\\)",
    re.I,
)


def load_rows(path: Path) -> tuple[list[dict], list[str]]:
    issues = []
    rows = []
    text = path.read_text(encoding="utf-8")
    if text.startswith("\ufeff"):
        issues.append(f"{path}: leading BOM")
        text = text.lstrip("\ufeff")
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            issues.append(f"{path}:{number}: blank line")
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError as exc:
            issues.append(f"{path}:{number}: {exc.msg}")
            continue
        if not isinstance(value, dict):
            issues.append(f"{path}:{number}: row is not an object")
            continue
        value["_line"] = number
        rows.append(value)
    return rows, issues


def _issue(row, message):
    return f"docs/facts/facts.jsonl:{row.get('_line', '?')}: {row.get('id', '?')}: {message}"


def check_row(row: dict, root: Path) -> list[str]:
    issues = []
    unknown = sorted(set(row) - set(REQUIRED) - {"classification", "_line"})
    if unknown:
        issues.append(_issue(row, f"unknown keys: {', '.join(unknown)}"))
    missing = [key for key in REQUIRED if key not in row]
    if missing:
        issues.append(_issue(row, f"missing keys: {', '.join(missing)}"))
        return issues

    ident = row["id"]
    if not isinstance(ident, str) or not ID_RE.fullmatch(ident):
        issues.append(_issue(row, "id must be dotted lowercase"))
    if row["kind"] not in KINDS:
        issues.append(_issue(row, f"kind {row['kind']!r} is not allowed"))
    if row["category"] not in CATEGORIES:
        issues.append(_issue(row, f"category {row['category']!r} is not allowed"))
    if row["status"] not in STATUSES:
        issues.append(_issue(row, f"status {row['status']!r} is not allowed"))
    if row["kind"] == "proposal":
        if row.get("classification") not in CLASSIFICATIONS:
            issues.append(_issue(row, "proposal requires classification"))
        if row["status"] != "unknown":
            issues.append(_issue(row, "proposal status must be unknown"))
    elif "classification" in row:
        issues.append(_issue(row, "classification is only for proposals"))

    statement = row["statement"]
    if not isinstance(statement, str) or not 40 <= len(statement) <= 800:
        issues.append(_issue(row, "statement must be 40 to 800 characters"))
    for key in ("source_path", "source_excerpt", "source_symbol", "source_url", "limitations", "stale_policy"):
        if not isinstance(row[key], str):
            issues.append(_issue(row, f"{key} must be a string"))
    if not isinstance(row["baseline_sha"], str) or not SHA_RE.fullmatch(row["baseline_sha"]):
        issues.append(_issue(row, "baseline_sha must be 40 hex characters"))
    if not isinstance(row["observed_at"], str) or not TIME_RE.fullmatch(row["observed_at"]):
        issues.append(_issue(row, "observed_at must be UTC Zulu, seconds precision"))
    if not isinstance(row["evidence"], list) or not row["evidence"] or any(
        not isinstance(item, str) or not item.strip() for item in row["evidence"]
    ):
        issues.append(_issue(row, "evidence must be a non-empty list of strings"))
    if isinstance(row["stale_policy"], str) and len(row["stale_policy"]) < 20:
        issues.append(_issue(row, "stale_policy is too short"))

    blob = json.dumps(row, ensure_ascii=False)
    if SECRET_RE.search(blob):
        issues.append(_issue(row, "row matches a credential or machine-path pattern"))

    line, end = row["source_line"], row["source_line_end"]
    if line is None and end is not None:
        issues.append(_issue(row, "source_line_end set without source_line"))
    if line is not None and (not isinstance(line, int) or line < 1):
        issues.append(_issue(row, "source_line must be a positive integer or null"))
    if end is not None and (not isinstance(end, int) or (isinstance(line, int) and end < line)):
        issues.append(_issue(row, "source_line_end must be null or >= source_line"))
    path = row["source_path"]
    if path:
        if path.startswith("/") or ".." in Path(path).parts:
            issues.append(_issue(row, "source_path must be repo-relative"))
        else:
            file = root / path
            if not file.is_file():
                issues.append(_issue(row, f"source file missing: {path}"))
            elif isinstance(line, int):
                lines = file.read_text(encoding="utf-8").splitlines()
                last = end if isinstance(end, int) else line
                if last > len(lines):
                    issues.append(_issue(row, f"source line {last} past end of {path} ({len(lines)})"))
                else:
                    excerpt = row["source_excerpt"]
                    if not excerpt:
                        issues.append(_issue(row, "source_excerpt required when source_line is set"))
                    elif excerpt not in "\n".join(lines[line - 1:last]):
                        issues.append(_issue(row, "source_excerpt not in the cited lines"))
            elif row["source_excerpt"]:
                issues.append(_issue(row, "source_excerpt set without source_line"))
    else:
        if line is not None or row["source_excerpt"]:
            issues.append(_issue(row, "empty source_path cannot carry a line or excerpt"))
        if not row["source_url"]:
            issues.append(_issue(row, "empty source_path requires source_url"))
    return issues


def validate(root: Path = ROOT, facts_path: Path = FACTS_PATH) -> list[str]:
    rows, issues = load_rows(facts_path)
    seen = {}
    for row in rows:
        issues.extend(check_row(row, root))
        ident = row.get("id")
        if ident in seen:
            issues.append(_issue(row, f"duplicate id (also line {seen[ident]})"))
        elif isinstance(ident, str):
            seen[ident] = row.get("_line")
    return issues


def build(out: Path, facts_path: Path = FACTS_PATH, root: Path = ROOT) -> None:
    issues = validate(root, facts_path)
    if issues:
        raise SystemExit("\n".join(issues))
    rows, _ = load_rows(facts_path)
    rows = sorted(rows, key=lambda row: row["id"])
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()
    conn = sqlite3.connect(out)
    try:
        conn.executescript(
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE facts (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                category TEXT NOT NULL,
                status TEXT NOT NULL,
                classification TEXT NOT NULL,
                statement TEXT NOT NULL,
                source_path TEXT NOT NULL,
                source_line INTEGER,
                source_line_end INTEGER,
                source_excerpt TEXT NOT NULL,
                source_symbol TEXT NOT NULL,
                source_url TEXT NOT NULL,
                baseline_sha TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                limitations TEXT NOT NULL,
                stale_policy TEXT NOT NULL
            );
            CREATE TABLE fact_evidence (
                fact_id TEXT NOT NULL REFERENCES facts(id),
                ord INTEGER NOT NULL,
                text TEXT NOT NULL,
                PRIMARY KEY (fact_id, ord)
            );
            """
        )
        for row in rows:
            conn.execute(
                """
                INSERT INTO facts (
                    id, kind, category, status, classification, statement,
                    source_path, source_line, source_line_end, source_excerpt,
                    source_symbol, source_url, baseline_sha, observed_at,
                    limitations, stale_policy
                ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                """,
                (
                    row["id"], row["kind"], row["category"], row["status"],
                    row.get("classification", ""), row["statement"], row["source_path"],
                    row["source_line"], row["source_line_end"], row["source_excerpt"],
                    row["source_symbol"], row["source_url"], row["baseline_sha"],
                    row["observed_at"], row["limitations"], row["stale_policy"],
                ),
            )
            for ord_, text in enumerate(row["evidence"]):
                conn.execute(
                    "INSERT INTO fact_evidence (fact_id, ord, text) VALUES (?,?,?)",
                    (row["id"], ord_, text),
                )
        conn.commit()
    finally:
        conn.close()


def logical_dump(path: Path) -> str:
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    try:
        facts = [dict(row) for row in conn.execute("SELECT * FROM facts ORDER BY id")]
        evidence = [
            dict(row) for row in conn.execute(
                "SELECT fact_id, ord, text FROM fact_evidence ORDER BY fact_id, ord"
            )
        ]
    finally:
        conn.close()
    return json.dumps({"facts": facts, "evidence": evidence}, ensure_ascii=False, indent=2)


def query(path: Path, ident="", kind="", category="", status="") -> list[dict]:
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    clauses, args = [], []
    for column, value in (("id", ident), ("kind", kind), ("category", category), ("status", status)):
        if value:
            clauses.append(f"{column} = ?")
            args.append(value)
    where = (" WHERE " + " AND ".join(clauses)) if clauses else ""
    try:
        found = [dict(row) for row in conn.execute(f"SELECT * FROM facts{where} ORDER BY id", args)]
        for row in found:
            row["evidence"] = [
                item["text"] for item in conn.execute(
                    "SELECT text FROM fact_evidence WHERE fact_id = ? ORDER BY ord", (row["id"],)
                )
            ]
    finally:
        conn.close()
    return found


def examples(out: Path) -> int:
    build(out)
    print("validate: python tools/project_facts/run.py validate")
    print("build:    python tools/project_facts/run.py build --out build/project_facts.sqlite")
    samples = (
        {"kind": "code-derived", "category": "render"},
        {"status": "verified", "category": "security"},
        {"kind": "proposal"},
        {"kind": "historical"},
    )
    for sample in samples:
        found = query(out, **sample)
        label = " ".join(f"--{key} {value}" for key, value in sample.items())
        print(f"query {label} -> {len(found)}")
        for row in found[:3]:
            print(f"  {row['id']} [{row['status']}] {row['statement'][:88]}")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="GUO project fact database")
    parser.add_argument("command", choices=("validate", "build", "query", "examples"))
    parser.add_argument("--out", type=Path)
    parser.add_argument("--facts", type=Path, default=FACTS_PATH)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--id", default="")
    parser.add_argument("--kind", default="")
    parser.add_argument("--category", default="")
    parser.add_argument("--status", default="")
    args = parser.parse_args(argv)
    facts = args.facts
    if args.command == "validate":
        issues = validate(args.root, facts)
        if issues:
            print("\n".join(issues), file=sys.stderr)
            return 1
        rows, _ = load_rows(facts)
        print(f"ok {len(rows)} facts")
        return 0
    if args.command == "build":
        if args.out is None:
            print("build requires --out", file=sys.stderr)
            return 2
        # build() validates against the real root unless tests pass --facts under that root.
        global_issues = validate(args.root, facts)
        if global_issues:
            print("\n".join(global_issues), file=sys.stderr)
            return 1
        build(args.out, facts, args.root)
        print(f"wrote {args.out}")
        return 0
    if args.command == "examples":
        out = args.out or (ROOT / "build" / "project_facts.sqlite")
        return examples(out)
    if args.out is None or not args.out.is_file():
        print("query requires --out pointing at a built database", file=sys.stderr)
        return 2
    found = query(args.out, args.id, args.kind, args.category, args.status)
    for row in found:
        print(json.dumps(row, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
