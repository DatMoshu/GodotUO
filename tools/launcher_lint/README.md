# launcher_lint

Checks that everything under `launchers/` is what DirectorDeck's Run panel
expects: a `.bat`/`.sh` pair per launcher, each saying in one sentence what it
does and what a person sees.

```
python tools/launcher_lint/run.py            check; exit 1 on any problem
python tools/launcher_lint/run.py --list     every pair with its description
python tools/launcher_lint/run.py --json     the same as JSON
python tools/launcher_lint/test_launcher_lint.py
```

Launcher: `launchers/dev/launcher_lint.bat` / `.sh`. The smoke runs it as its
last step, the pre-push hook (`tools/git_hooks`) runs it on every pushed tip,
and CI runs it and its tests. Standard library and `git` only.

## The rule it checks

For every `launchers/<group>/<name>` (never `_shared`):

| | `.bat` | `.sh` |
|---|---|---|
| exists | yes, beside its twin | yes, beside its twin |
| line endings | CRLF | LF |
| first lines | `@echo off`, then `rem <one sentence>.` | `#!/usr/bin/env bash`, then `# <the same sentence>.` |
| flags it takes | optional `rem args: ...` next | the same `# args: ...` next |
| shared logic | calls `_shared\common.bat` (or `config.bat`, before the engine exists) | `set -euo pipefail`, sources `_shared/common.sh` |
| Windows only | -- | says why and `exit 2` instead of sourcing `common.sh` |
| stdin | no `pause`, `set /p`, `choice` | no `read` |
| git | -- | mode 100755 (`git update-index --chmod=+x`) |

And `launchers/_shared/config.sh` defines exactly the keys `config.bat` does,
with `config.local.sh.example` and `common.sh` beside it.

The description is the button's hint in the deck, so it says what happens:
a window opens, a file is written, it stays in the foreground until Ctrl+C,
or it detaches (only GUI programs such as the editor or a browser do). The
lint cannot check a launcher's exit code or whether it stays in the
foreground; the review of a new launcher does.
