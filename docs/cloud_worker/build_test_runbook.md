# Build and test runbook

Commands below were run from `/workspace` on the review VM unless the row
says GitHub Actions. "Not run" means this review did not run them. Do not
treat a blank as a pass.

Python was 3.12.3. `dotnet` was not on `PATH`. `godot-console` was not a
Linux binary (`tools/godot` contains `godot.cmd` and `godot-console.cmd`).

## Commands run in this review

| Command | Result |
|---|---|
| `git rev-parse HEAD` | `0aec6eb8ccc53a01655fbb8872211ce2358c9992` on `main`, tracking `origin/main` |
| `git branch -a` | `origin/main` and `origin/work/guo-lighting-next-20261031`. No `origin/integration` |
| `gh pr list --state open --limit 40` | `[]` |
| `gh pr view 179 --json state,mergedAt,baseRefName` | `MERGED`, `2026-10-31T19:17:56Z`, base `integration` |
| `gh run view 42511454234 --json jobs` | Baseline push. `guard` failure. `build`, `perf`, `server-adapters` success |
| `gh run view 42511454234 --log-failed` | `python3 tools/web/test_run.py` on the runner's Python 3.14: `SSLCertVerificationError`, Missing Authority Key Identifier |
| `gh run view 42498982602 --log` (desktop step) | `dotnet build godot/GUO/GUO.csproj -c Debug -warnaserror:NU1605` printed `Build succeeded.` Log also shows runtime 10.0.14 already on the image and SDKs 8.0.427 and 9.0.320 installed by the action. No `dotnet --version` line was captured |
| `python3 tools/web/test_run.py` | Exit 0. Last line `test_run: all ok`. cryptography 41.0.7 |
| `python3 tools/project_facts/run.py validate` | `ok 55 facts` after the seed was corrected |
| `python3 tools/project_facts/test_project_facts.py` | 6 tests, OK |
| `python3 tools/docs_lint/test_docs_lint.py` | 11 tests, OK |
| `python3 tools/docs_lint/run.py` | `docs_lint: 398 documents, 0 issues` |
| `python3 tools/launcher_lint/test_launcher_lint.py` | 13 tests, OK |
| `python3 tools/launcher_lint/run.py` | `[launcher_lint] OK: 116 launcher pairs` |
| `python3 tools/privacy_scan/run.py` | `[privacy_scan] clean` |
| `python3 tools/guo/test_config.py` | 43 tests, OK |
| `python3 -m pytest -q tools/project_facts` | `6 passed in 0.08s` after `pip install pytest` (pytest was not preinstalled) |

## Commands not run

| Command | Why |
|---|---|
| `dotnet build godot/GUO/GUO.csproj` | No .NET SDK on the VM. Windows CI `build` job on this SHA succeeded; that is the substitute, not a local compile |
| `launchers/dev/smoke.sh` or `smoke.bat` | Needs Godot, and several steps need client data. Also needs `dotnet` for the engine-free C# probes |
| `python tools/port_audit/run.py` | Reads `sources/ClassicUO`, which is not in the checkout |
| `ReceiverHeightProbe.tscn`, `NativeWeatherSceneProbe` | Need `godot-console`. No native frame was captured |
| Playtest, screenshot, parity diff | No shard, no art, no GPU session |
| Full `python -m pytest -q` | Not run. The pool installs pytest, jsonschema, pillow, pefile, numpy and cryptography on CI. This review ran the new fact tests and the guard-tier scripts it could finish |

## What each CI workflow actually proves

From `.github/workflows/` as read, plus the two Actions runs above.

| Workflow | Job | Proves | Does not prove |
|---|---|---|---|
| `ci.yml` | `guard` (ubuntu-latest) | Privacy, docs links, launcher pairs, a handful of Python scripts, using the image's `python3` | A compile, a frame, Python 3.12 specifically. **Red on the baseline SHA** |
| `ci.yml` | `perf` | Synthetic perf-gate contracts | FPS |
| `ci.yml` | `build` (windows-latest) | Fetch upstream pin, `dotnet build` Release and Debug Labs, guoasset, port drift, asset-store smoke, pooled pytest, scenario validate, three C# test programs, port_status freshness | Smoke, art, a device |
| `ci.yml` | `server-adapters` | Compile-only against pinned ServUO and RunUO APIs | A running shard |
| `build.yml` | `desktop` | `dotnet build` Debug of the client on Windows, when the path filter matches | SDK 10 requested by the file. The file asks for 8.0.x and 9.0.x. The 8 November run still printed Build succeeded |
| `build.yml` | `android` | `dotnet publish` for `android-arm64` and a plugin-host leak check | An APK or a device |
| `build.yml` | `web` | The web tool files exist | An export. ADR-0008: official Godot cannot export this C# project to the web |
| `release.yml` | export | Windows, Deck and Android artifacts on a tagged release | Not exercised here |
| `pages.yml`, `wiki.yml` | site | Docs publish | Not exercised here |

`ci.yml` still lists `integration` as a push branch. That ref was not on
`origin` during this review. Recreating the branch would start CI again.
It is not an open integration queue.

## Local loop when the machine has the toolchain

The everyday loop in `AGENTS.md` is unchanged:

```
dotnet build godot/GUO/GUO.csproj
launchers/dev/smoke.bat
```

Smoke is the health check before a commit that touches the client. A
docs-only change still wants docs lint, launcher lint and the privacy
scan. The fact tool joins that list:

```
python tools/project_facts/run.py validate
python tools/project_facts/test_project_facts.py
```

## Validation log for this pull request

Run on the review VM after the documents and `docs/facts/facts.jsonl` were
in the tree. Python 3.12.3.

```
python3 tools/docs_lint/test_docs_lint.py
  Ran 11 tests, OK
python3 tools/docs_lint/run.py
  docs_lint: 398 documents, 0 issues
python3 tools/launcher_lint/test_launcher_lint.py
  Ran 13 tests, OK
python3 tools/launcher_lint/run.py
  [launcher_lint] OK: 116 launcher pairs
python3 tools/privacy_scan/run.py
  [privacy_scan] clean
python3 tools/guo/test_config.py
  Ran 43 tests, OK
python3 tools/project_facts/run.py validate
  ok 55 facts
python3 tools/project_facts/test_project_facts.py
  Ran 6 tests, OK
python3 -m pytest -q tools/project_facts
  6 passed in 0.08s
python3 tools/web/test_run.py
  exit 0, test_run: all ok
```

Not run, and not claimed: `dotnet build`, `launchers/dev/smoke.bat`,
`launchers/dev/smoke.sh`, `python tools/port_audit/run.py`, the pooled
`python -m pytest -q` over all of `tools/`, any Godot scene, any shard.
