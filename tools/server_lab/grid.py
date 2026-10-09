"""The lab's grid (backend x case), the wiki page made from it, and a row's Discord card. Pure functions over JSON.

grid.json (build/server_lab/, gitignored) is docs/data_formats.md section 36. A cell is one of RESULTS; a failed run
reads "FAIL (untriaged)" until triage.json in this folder says whose failure it is (SV7).
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

RESULTS = ("PASS", "FAIL (GUO)", "FAIL (server gap)", "FAIL (untriaged)", "n/a", "not run")
TRIAGE = {"guo": "FAIL (GUO)", "server-gap": "FAIL (server gap)", "n/a": "n/a"}
CARD_BODY_LIMIT = 900


def empty() -> dict:
    return {"version": 1, "updated": None, "client_version": "", "commit": None, "rows": {}}


def load(path: Path) -> dict:
    if not path.is_file():
        return empty()
    data = json.loads(path.read_text(encoding="utf-8"))
    return data if data.get("version") == 1 else empty()


def save(path: Path, grid: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(grid, indent=2) + "\n", encoding="utf-8")
    tmp.replace(path)


def now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def ensure_row(grid: dict, backend: dict, era: str) -> dict:
    """The row of a backend (a backends.json entry with `lab`), refreshed with its pin and era."""
    lab = backend["lab"]
    row = grid["rows"].setdefault(backend["id"], {"cells": {}})
    row.update({"name": backend["name"], "row": lab["row"], "era": era,
                "pins": [{"role": r["role"], "commit": r["commit"][:9], "date": r["date"]} for r in lab["repos"]]})
    return row


def cell_from_run(case: dict, exit_code: int, run_id: str | None, failed: list[str], note: str = "") -> dict:
    """One case's outcome from the scenario runner: exit 0 PASS, 2 could not start (not run), anything else a failure."""
    if exit_code == 0 and run_id:
        result = "PASS"
    elif exit_code == 2 or not run_id:
        result = "not run"
    else:
        result = "FAIL (untriaged)"
    cell = {"result": result, "scenario": case["scenario"], "run_id": run_id,
            "run_dir": f"build/runs/{run_id}" if run_id else None, "failed_steps": failed, "when": now()}
    if note:
        cell["note"] = note[:300]
    return cell


def apply_triage(grid: dict, triage: dict) -> None:
    """triage.json: {"<backend>": {"<case n>": {"verdict": "guo" | "server-gap" | "n/a", "why": "..."}}}. A verdict
    turns an untriaged failure into its owner's failure; `n/a` also covers a case the backend lacks, run or not."""
    for backend, cases in triage.items():
        row = grid["rows"].get(backend)
        if row is None:
            continue
        for n, entry in cases.items():
            verdict = TRIAGE.get(entry.get("verdict", ""))
            if verdict is None:
                continue
            cell = row["cells"].get(str(n))
            if verdict == "n/a":
                row["cells"][str(n)] = {**(cell or {}), "result": "n/a", "why": entry.get("why", "")}
            elif cell and cell["result"] == "FAIL (untriaged)":
                cell.update({"result": verdict, "why": entry.get("why", "")})


def result(row: dict | None, case: dict) -> str:
    if row is None:
        return "not run"
    cell = row["cells"].get(str(case["n"]))
    return cell["result"] if cell else "not run"


def wiki_page(grid: dict, cases: list[dict], backends: list[dict]) -> str:
    """docs/wiki/Server-Compatibility.md: which server works with GUO for what, with the pins and client on top."""
    lab = sorted((b for b in backends if "lab" in b), key=lambda b: b["lab"]["row"])
    lines = [
        "# Server compatibility",
        "",
        "GUO is meant to work with any Ultima Online server, not only the ModernUO shard it is developed against. The",
        "server lab runs the same scripted cases against each server and records what happened. This page is generated",
        "by `python tools/server_lab/run.py wiki` from the lab's latest results; do not edit it by hand.",
        "",
        f"Last updated: {(grid.get('updated') or 'never')[:10]}. GUO client version: {grid.get('client_version') or 'unknown'}.",
        "",
        "## Servers and versions",
        "",
        "| Server | Version tested | Era |",
        "|---|---|---|",
    ]
    for b in lab:
        row = grid["rows"].get(b["id"])
        pins = ", ".join(f"{r['role']} `{r['commit'][:9]}` ({r['date']})" for r in b["lab"]["repos"])
        lines.append(f"| {b['name']} | {pins} | {row['era'] if row else b['lab']['era']['lab']} |")
    lines += [
        "",
        "## Results",
        "",
        "PASS: the case ran to the end. FAIL (GUO): a GUO bug, filed as a story. FAIL (server gap): the server does",
        "not do this the way the client expects; a local patch is kept for it. FAIL (untriaged): failed, not yet looked",
        "at. n/a: the server does not have the feature (often its era). not run: the case has no scenario yet, or this",
        "server has not been set up in the lab yet.",
        "",
        "| # | Case | " + " | ".join(b["name"] for b in lab) + " |",
        "|---|---|" + "---|" * len(lab),
    ]
    for case in cases:
        cells = [result(grid["rows"].get(b["id"]), case) for b in lab]
        lines.append(f"| {case['n']} | {case['title']} | " + " | ".join(cells) + " |")
    lines += [
        "",
        "## How to run it",
        "",
        "`launchers\\dev\\server_lab.bat row modernuo` sets the server up (fetch at its pinned version, build,",
        "configure on this computer only, with a generated admin account), starts it, runs every case that has a",
        "scenario, stops it and rewrites this page. Details: `tools/server_lab/README.md` and",
        "[the pins table](../server_lab.md).",
        "",
    ]
    return "\n".join(lines)


def card(grid: dict, backend: dict, cases: list[dict]) -> dict:
    """A plain-language card for a completed row: no ids, paths or hashes. Built only; nothing is sent."""
    row = grid["rows"].get(backend["id"], {"cells": {}})
    scripted = [c for c in cases if c["scenario"]]
    counts = {r: 0 for r in RESULTS}
    for case in scripted:
        counts[result(row, case)] += 1
    passed = [c["title"] for c in scripted if result(row, c) == "PASS"]
    failed = [c["title"] for c in scripted if result(row, c).startswith("FAIL")]
    body = f"GUO against {backend['name']}: {counts['PASS']} of {len(scripted)} scripted cases pass"
    body += f" ({len(cases) - len(scripted)} more cases are still to be written)."
    if passed:
        body += " Working: " + "; ".join(passed) + "."
    if failed:
        body += " Not yet: " + "; ".join(failed) + "."
    if len(body) > CARD_BODY_LIMIT:
        body = body[:CARD_BODY_LIMIT - 1] + "…"
    return {"title": f"Server lab: {backend['name']}", "ok": not failed and counts["PASS"] == len(scripted),
            "passed": counts["PASS"], "scripted": len(scripted), "body": body}
