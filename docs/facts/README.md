# Project fact database

Machine-readable notes from the 2026-11-08 cloud review, and the place
later reviews add rows. The human narrative is
[docs/cloud_worker](../cloud_worker/README.md). This folder is the
citation index.

## Four kinds of row

| `kind` | What it is | What it is not |
|---|---|---|
| `code-derived` | A statement checked against a file in this repo, with a line excerpt the tool re-reads | A guess, or a summary of a file nobody opened |
| `test-result` | A command that was actually run, or an Actions log that was actually fetched, on the date in `observed_at` | A test the author expects will pass |
| `historical` | A git or GitHub observation (SHA, merged pull request, empty open-PR list) | A reason to keep treating that pull request as current work |
| `proposal` | A follow-up task. `status` is always `unknown`. `classification` is cloud-ready, needs-coordination, needs-local-validation, or blocked | A claim that the code already does that work |

`status` on the first three kinds is `verified`, `inferred`, or `unknown`.
This seed uses `verified` only where the excerpt or the log was in hand.
There is no `inferred` row in the seed. Add one when a later pass has a
real hypothesis worth keeping and a limitation that says so.

## Files

| File | Role |
|---|---|
| `facts.jsonl` | Source of truth. One JSON object per line. Commit this |
| `schema.json` | The field list, in JSON Schema draft 2020-12. The checker in `tools/project_facts/run.py` enforces the same required keys without a third-party library |
| SQLite | Generated. Do not commit it. `python tools/project_facts/run.py build --out build/project_facts.sqlite` |

## Fields that keep a row honest

- `id` is stable (`fact.` or `prop.` plus dotted words). Do not renumber.
- `baseline_sha` is the commit the row was checked against.
- `source_path` plus `source_line` and `source_excerpt` must match the
  file. An empty path is allowed only with a `source_url` (a GitHub
  observation that is not a file in the tree).
- `observed_at` is UTC `YYYY-MM-DDTHH:MM:SSZ`.
- `evidence` names the command or the other fact id.
- `limitations` says what was not done.
- `stale_policy` says when to re-read the row.

Refreshing is mechanical: edit the source, then
`python tools/project_facts/run.py validate`. A moved line fails the
excerpt check. Fix the row or delete it. Do not add rows to hit a count.

## What must not be stored

No Ultima Online art, no account passwords, no tokens, no
`config.local` paths, no home-directory paths. The validator rejects a
few obvious patterns (private-key banners, cloud token prefixes,
`C:\Users\`). A dev-only account that already lives in source is cited
by the comment around it, not by copying the secret into the row.

The query tool and its tests are documented in
[tools/project_facts/README.md](../../tools/project_facts/README.md).
