"""Tests for the project fact database. Stdlib only.

    python tools/project_facts/test_project_facts.py
"""
import json
import tempfile
import unittest
from pathlib import Path

import run


SHA = "0aec6eb8ccc53a01655fbb8872211ce2358c9992"
WHEN = "2026-11-08T19:38:21Z"


def row(**overrides):
    base = {
        "id": "fact.fixture.sample",
        "kind": "code-derived",
        "category": "toolchain",
        "status": "verified",
        "statement": "The fixture file contains the marker the validator must find.",
        "source_path": "marker.txt",
        "source_line": 1,
        "source_line_end": 1,
        "source_excerpt": "MARKER",
        "source_symbol": "",
        "source_url": "",
        "baseline_sha": SHA,
        "observed_at": WHEN,
        "evidence": ["fixture"],
        "limitations": "Fixture only.",
        "stale_policy": "Re-read the cited lines when that file changes.",
    }
    base.update(overrides)
    return base


class ProjectFactTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "marker.txt").write_text("MARKER\n", encoding="utf-8")
        self.facts = self.root / "facts.jsonl"

    def write(self, rows):
        self.facts.write_text(
            "".join(json.dumps(item) + "\n" for item in rows), encoding="utf-8"
        )

    def test_schema_required_matches_checker(self):
        schema = json.loads(run.SCHEMA_PATH.read_text(encoding="utf-8"))
        self.assertEqual(schema["required"], list(run.REQUIRED))
        self.assertEqual(set(schema["properties"]["kind"]["enum"]), run.KINDS)
        self.assertEqual(set(schema["properties"]["status"]["enum"]), run.STATUSES)

    def test_seed_validates_and_uses_one_baseline(self):
        issues = run.validate()
        self.assertEqual(issues, [], "\n".join(issues))
        rows, _ = run.load_rows(run.FACTS_PATH)
        self.assertGreaterEqual(len(rows), 20)
        self.assertTrue(all(item["baseline_sha"] == SHA for item in rows))
        kinds = {item["kind"] for item in rows}
        self.assertTrue({"code-derived", "test-result", "historical", "proposal"} <= kinds)
        proposals = [item for item in rows if item["kind"] == "proposal"]
        self.assertTrue(proposals)
        self.assertTrue(all(item["status"] == "unknown" for item in proposals))

    def test_bad_excerpt_missing_file_and_duplicate(self):
        self.write([row(source_excerpt="ABSENT")])
        self.assertTrue(any("source_excerpt" in item for item in run.validate(self.root, self.facts)))
        self.write([row(source_path="missing.txt")])
        self.assertTrue(any("missing" in item for item in run.validate(self.root, self.facts)))
        self.write([row(), row()])
        self.assertTrue(any("duplicate" in item for item in run.validate(self.root, self.facts)))

    def test_secret_pattern_and_proposal_status(self):
        self.write([row(statement="Token x-access-token: should never be stored in a fact row here.")])
        self.assertTrue(any("credential" in item for item in run.validate(self.root, self.facts)))
        self.write([row(
            id="prop.fixture.sample", kind="proposal", status="verified",
            classification="cloud-ready",
            statement="Proposal rows stay unknown until someone selects the work.",
        )])
        self.assertTrue(any("proposal status" in item for item in run.validate(self.root, self.facts)))

    def test_sqlite_rebuild_is_deterministic(self):
        self.write([
            row(),
            row(
                id="prop.fixture.other", kind="proposal", status="unknown",
                classification="blocked", category="process",
                statement="A second row proves query filters and stable dump order.",
                source_path="", source_line=None, source_line_end=None, source_excerpt="",
                source_url="https://example.com/proposal",
            ),
        ])
        first = self.root / "a.sqlite"
        second = self.root / "b.sqlite"
        run.build(first, self.facts, self.root)
        run.build(second, self.facts, self.root)
        self.assertEqual(run.logical_dump(first), run.logical_dump(second))
        verified = run.query(first, status="verified")
        self.assertEqual([item["id"] for item in verified], ["fact.fixture.sample"])
        proposals = run.query(first, kind="proposal")
        self.assertEqual([item["id"] for item in proposals], ["prop.fixture.other"])
        self.assertEqual(proposals[0]["evidence"], ["fixture"])

    def test_real_database_queries(self):
        out = self.root / "real.sqlite"
        run.build(out)
        render = run.query(out, kind="code-derived", category="render")
        self.assertTrue(any(item["id"] == "fact.render.lighting-speed-default-off" for item in render))
        verified_proposals = run.query(out, kind="proposal", status="verified")
        self.assertEqual(verified_proposals, [])
        historical = run.query(out, kind="historical")
        self.assertTrue(historical)


if __name__ == "__main__":
    unittest.main()
