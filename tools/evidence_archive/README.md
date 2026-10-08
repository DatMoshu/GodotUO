# evidence_archive

Copies a worktree's gate evidence to a safe folder before the worktree is removed.
`git worktree remove` deletes ignored files, and the smoke logs, reports and captures a
worker produces live in its ignored `build/` folder.

The evidence folder is the setting `UO_EVIDENCE_DIR` (environment, then `config.local.bat`,
then `config.bat`). Left unset, it is the main checkout's `build/director_evidence`, which
no linked worktree contains. `--dir` overrides it for one run.

```
python tools/evidence_archive/run.py archive WORKTREE NAME [--paths build/worker_a ...] [--max-mb 2048]
python tools/evidence_archive/run.py verify NAME
python tools/evidence_archive/run.py list
```

`archive` copies `build/` (or the given worktree-relative `--paths`) to `<evidence>/<NAME>`,
keeping the relative layout. It writes `SOURCE.json` (worktree name, branch, commit, file
count, time) and a `MANIFEST.sha256` that `sha256sum -c` also reads, then re-reads the copy
against both the manifest and the source. It refuses:

- an evidence folder inside any linked worktree, or inside the source;
- a `NAME` that already exists;
- paths outside the worktree, and copies over `--max-mb`.

It never changes or deletes the source. Remove the worktree only after the worker who owns it
says it is safe. `verify` re-checks an archive later.

Exit codes: 0 ok, 1 refused or a hash mismatch, 2 bad input.

Tests: `python -m pytest tools/evidence_archive`.
