# MapGen integration handoff — compiler lifetime acceptance fix

## Revision and scope

Branch: `codex/cdda-uo-layouts`, local and unpushed.
Final implementation revision: `9eece4cbeb81fbedce755d5d7adbc15d3dd590fd`.
The subsequent handoff-only commit changes this document, not the implementation.
The retained layout/runtime baseline remains `b9336b271a218d71fc27454e956533f794e1b6b6`.

Implementation files changed in this bounded package:

- `tools/layout_import/acceptance.py`: importer-owned acceptance orchestration
  with a process-scoped compiler setting, distinct logs/reports, failure
  propagation, refusal to overwrite evidence, and editor project restoration.
- `tools/layout_import/run.py`: `acceptance --out <NEW_DIR> --repeats 1..3`.
- `tools/layout_import/README.md`: replay instructions and compiler lifetime
  explanation.
- `tools/layout_import/test_layout_import.py`: regressions for failed-child
  reporting/project restoration and refusal to overwrite retained evidence.

No source-layout census, native geometry, shared launcher/runtime, engine binary,
retail data or configuration change was made in this package. No merge, push,
deployment or publication was performed.

## Root cause and measured reproduction

The retained failure was not a still-running Godot engine. Its Windows console
wrapper was waiting for a persistent Roslyn compiler descendant after the engine
had exited. The pinned engine's [console wrapper source](https://github.com/godotengine/godot/blob/ed1daf0bf/platform/windows/console_wrapper_windows.cpp#L150)
waits until the job has no active descendants before returning the engine's exit
code.

The bounded baseline recorded only descendants of its newly spawned wrapper:
engine PID 56628 exited by 12.609 seconds, but wrapper PID 66048 and its descendant
`VBCSCompiler.exe` PID 34980 remained at the 45-second limit. Cleanup terminated
only that Popen-owned wrapper; Windows closed its owned job descendants. No
name-based process kill or machine-wide build-server shutdown was used.

A forced C# rebuild with only `UseSharedCompilation=false` in that child
environment used a transient `csc.exe` instead of the persistent server and
returned code 0 in 15.344 seconds. A broader build-server setting trial also
passed, but the implemented fix uses only the demonstrated compiler property.
The caller's environment is copied; machine and shared configuration remain
unchanged.

Private reproduction evidence, relative to this checkout:

- `build/layout_import/shutdown/baseline/report.json`
- `build/layout_import/shutdown/compiler-only-forced/report.json`
- `build/layout_import/shutdown/reproduce.py` (bounded diagnostic helper)
- `build/layout_import/shutdown/retained-final-smoke.log`
- `build/layout_import/shutdown/retained-editor-report.json`
- `build/layout_import/shutdown/retained-editor.log`

These files contain machine paths/provenance and stay local. The original failed
smoke has not been rewritten into a pass.

## Actual acceptance results

`build/layout_import/acceptance/shutdown-fix/report.json` records `passed:true`:

| Check | Outcome | Elapsed |
|---|---|---:|
| Unchanged `launchers/dev/smoke.bat`, pass 1 | exit 0, all six steps and full editor suite passed | 92.797 s |
| Same full smoke, pass 2 | exit 0, all six steps and full editor suite passed | 88.672 s |
| Full headless editor build/reload | exit 0, checks passed before and after reload; assembly unload confirmed | 163.813 s |
| Forced real C# build with compiler property | exit 0, transient compiler exited | 15.344 s |
| Importer tests | 25 passed | 1.422 s |
| Staged privacy and Markdown links | clean; zero README link issues | — |

The successful repeated acceptance checks required no process termination.
Each smoke has its own command log and retained editor report/log. Reload logs
are under `build/layout_import/acceptance/shutdown-fix/reload/`.

The reload still emits exit warnings for 24 Canvas RIDs, 746 CanvasItem RIDs and
1,982 ObjectDB instances. The same counts occur in the retained earlier successful
reload. They are not fixed by this compiler lifetime package; the process still
exited normally and the full reload tool returned 0.

## Replay commands

From the checkout root, with Python 3.12 or the configured interpreter:

```powershell
python tools/layout_import/run.py acceptance --out build/layout_import/acceptance/<NEW_RUN_NAME> --repeats 2
python tools/layout_import/test_layout_import.py
```

Use a new acceptance output directory. The command runs the real shared smoke
launcher twice and the full editor reload tool. It supplies the current Python
executable to its child launchers and scopes `UseSharedCompilation=false` to its
child environment. It does not replay the gameplay tour.

The prior native district can be replayed independently, without changing its
inputs:

```powershell
python tools/layout_import/run.py district-prove --built build/layout_import/districts/seeded_two_storey --stage build/uodata/seeded_two_storey --out build/layout_import/proof/<NEW_REPLAY_NAME> --clip build/layout_import/proof/<NEW_REPLAY_NAME>/walk.mp4
```

This uses the checkout's private shard and configured nonshared ports. It
requires the retained local archives, retail installation and free-memory check.

## Preserved runtime evidence and limitations

The 72×72 native district loaded three multis with 23 functional doors including
gates. Its whole-district tour passed 88/88 stops with zero room teleport tags,
covering both CDDA houses, streets, the seeded Zomboid house's two floors,
descent and exit. A real height-20 impassable stone wall blocked the negative
target. This package preserved that proof; it did not record a new runtime tour.

- Proof: `build/layout_import/proof/seeded_two_storey/report.json`
  SHA-256 `5096a6b095dc7212560cedf8f6d586a927b2a63e804712970e1d23cc2b16a147`.
- Continuous publication derivative:
  `build/layout_import/publication/guo-hybrid-two-storey-runtime.mp4`, 101 seconds,
  8,851,441 bytes, SHA-256
  `2066b66ecaf199594f4bfd90f50f790e71cc8fc41cbb2bd6842a2797923a0f72`.
- Earlier detailed media handoff:
  `build/layout_import/publication/DIRECTOR_HANDOFF.md` (local-only paths).

Large-market staircase adaptation, cross-cell Zomboid buildings, unsupported
stair forms, remaining variants and loot/trap/vehicle/field/spawn gameplay
mappings remain incomplete. Source census counts do not imply conversion
coverage. Source licensing, attribution and retail-data restrictions still apply;
generated media/data/archives remain local.

## Exact shared-file proposal for Director

The importer acceptance path is fixed and tested. A direct invocation of the
unmodified shared smoke launcher without this property can still retain the
compiler descendant. Project-wide adoption belongs to the shared tooling owner.
The following is a proposed handoff only; neither shared file was edited:

1. `launchers/dev/smoke.bat`: immediately after `@echo off`, before `common.bat`,
   add `setlocal` and `set "UseSharedCompilation=false"`. `setlocal` keeps the
   compiler property and resolved launcher environment within this invocation.
   Existing six checks and their failure handling remain intact.
2. `tools/guo/process.py` and `tools/editor_smoke/run.py`: if the tooling owner
   wants standalone editor smoke/reload protected as well, add a shared child
   environment helper that copies `os.environ` and sets only
   `UseSharedCompilation=false` on Windows. Pass that copy as `env=` to the
   editor tool's direct build and Godot Popen calls. Keep the setting out of
   persistent configuration and interactive runtime defaults. The importer can
   then delegate its environment policy to that shared helper.

Acceptance for any shared adoption: cold/forced Godot build exits normally,
two full unchanged smoke passes exit 0, full editor reload exits 0, and existing
layout runtime evidence remains unchanged. Do not mask an abnormal exit as a
pass or shut down unrelated compiler processes.
