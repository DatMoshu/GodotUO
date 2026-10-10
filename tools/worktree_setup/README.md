# tools/worktree_setup

Makes a fresh git worktree ready to build, run and smoke with no directory
links: copies the gitignored per-user files it lacks from the main checkout
(`launchers/_shared/config.local.bat`, `tools/privacy_scan/deny.local.txt`;
never overwriting), checks the engine and the upstream reference resolve to
the main checkout's copies, and runs the console engine once with
`--headless --import`.

## Run

```
python tools/worktree_setup/run.py [--no-import]
launchers\dev\worktree_setup.bat
```

## Tests

No unit tests. The worktree fallback it relies on is covered by `python tools/guo/test_config.py`. Exit 0 when everything resolved and the import passed.
