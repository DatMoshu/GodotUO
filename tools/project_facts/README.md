# Project facts

Builds and queries the database described in
[docs/facts/README.md](../../docs/facts/README.md). Standard library only.
The JSONL file is the source. SQLite is an index you can delete.

## Run

From the repository root:

```
python tools/project_facts/run.py validate
python tools/project_facts/run.py build --out build/project_facts.sqlite
python tools/project_facts/run.py query --out build/project_facts.sqlite --kind code-derived --category render
python tools/project_facts/run.py query --out build/project_facts.sqlite --status verified --category security
python tools/project_facts/run.py query --out build/project_facts.sqlite --kind proposal
python tools/project_facts/run.py query --out build/project_facts.sqlite --kind historical
python tools/project_facts/run.py query --out build/project_facts.sqlite --id fact.render.lighting-speed-default-off
python tools/project_facts/run.py examples
```

`validate` checks JSON, required fields, enums, duplicate ids, that each
cited file exists, and that `source_excerpt` sits inside the cited line
range. Exit 0 prints `ok N facts`. Exit 1 lists `file:line` issues.

`build` refuses to write if validation fails. Running it twice on the
same JSONL yields the same logical dump (row contents in id order). The
SQLite file bytes are not the contract.

`query` prints one JSON object per line, sorted by id, including that
row's evidence list. Proposals are `kind=proposal` and `status=unknown`,
so `--status verified` does not return them.

`examples` rebuilds `build/project_facts.sqlite` and prints three sample
queries. `build/` is gitignored.

## Tests

```
python tools/project_facts/test_project_facts.py
python -m pytest -q tools/project_facts
```

The tests cover the seeded corpus, a bad excerpt, a missing file, a
duplicate id, a credential-shaped row, proposal status, and two SQLite
builds with the same logical dump. They do not need Godot, client data,
or the .NET SDK.

## When to edit

Add or change a row in `docs/facts/facts.jsonl` in the same commit as the
source change that made the old excerpt false. The narrative for the
2026-11-08 review lives in
[docs/cloud_worker](../../docs/cloud_worker/README.md).
