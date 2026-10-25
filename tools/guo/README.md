# tools/guo

The shared Python package every GUO tool imports instead of re-deriving paths,
re-parsing config or hardcoding UO file formats. It implements the contract in
`docs/data_formats.md`:

| Module | Holds |
|---|---|
| `config.py` | the settings, resolved environment > `config.local.bat` > `config.bat` > central config |
| `datasources.py` | which client data a run uses (ADR-0021) |
| `formats.py` | the client file registry and the required set |
| `uoread.py`, `uoart.py`, `uomap.py` | readers for the client's `.mul`/`.uop` data |
| `uorecord.py` | recording helpers for proof runs |
| `world_preflight.py`, `worldobjects.py` | world project checks and shard world objects |
| `build.py`, `process.py`, `lan.py` | builds, child processes, LAN addresses |
| `build_guard.py` | bounded headless build launcher, with owned process-tree cleanup |
| `shard_secrets.py` | per-user shard passwords, generated and never committed |

## Run

```
from guo import Config, load_config      # with tools/ on sys.path
```

## Tests

```
python tools\guo\test_config.py
cd tools && python -m unittest guo.test_world_preflight
```

`TESTING.md` lists what `test_config.py` covers. Both use the standard library
only and never read your `config.local.bat` or the client install; CI runs
`test_config.py`.

## Legacy output routing

Generated paths use `load_config(root).build`, including mapgen's CLI output,
trailer masters/work files, background-loop reports and Store-only loops,
MCP probe output, pre-push snapshot checkouts, content/Comfy probe scratch,
content proof images and the PostFX pack's default archive. Seeded Store loops
read from that same configured output. Explicit `--out`, `--stage` and `--clips`
arguments retain their meaning. Configure `UO_BUILD_DIR` outside the checkout;
the environment overrides local config as for other tools. No new setting is
introduced.

Built-in background and screensaver art remains in its product asset path.
Trailer source clips, authored voice profiles and fixed expansion/reference
inputs are not relocated or rediscovered by this migration. Existing generated
files are not moved or removed. Scenario-run path validation/reference lookup
and retention are a separate migration; avoid treating authored files under an
old `build/` directory as disposable. Mapgen's MSBuild intermediate directories
also retain their existing project settings.

Run `python -m pytest tools/guo/test_output_routing.py tools/asset_store/test_postfx_pack.py`
for routing regressions. The tests exercise external destinations, explicit
output precedence, pack verification, scratch cleanup and the pre-push snapshot
boundary without launching Godot, contacting generation services or reading UO
data. `tools/git_hooks/test_hooks.py` exercises the real Git/checker workflow.

## Headless build timeout

`launchers/dev/build.bat` and `build.sh` use the same stdlib Python guard.
`UO_BUILD_TIMEOUT` is an integer from 1 to 86400 seconds, default 180 in both
shared configs (also the direct guard default when unset or empty). Invalid
values, including overflow, fail before spawning a build, with exit 2.
Successful and failing builds retain their child exit code; timeout returns 124.
Output streams to the caller. Windows build children disable persistent Roslyn
compilation through `build_child_env()`.

The guard owns a Windows Job Object or a POSIX process group, so timeout and
normal completion clean up only this build's descendants. On Windows a waiting
helper joins the job before spawning the engine; if containment fails, no engine
is started. POSIX children must remain in the build's process group (normal
Godot/.NET children do); deliberately detached processes are outside that scope.
No GNU `timeout` or PowerShell dependency is needed. The guard's stdin is reserved
for its Windows handshake; headless builds must not prompt for input.

This Tools-layer change rebuilds only the timeout portion of MeesaJarJar's
`c9ab0e125a741548c05ee189f85dc376d737cf22`; the mixed runtime/editor changes
are excluded. Synthetic tests cover exit status, timeout, descendant cleanup,
unrelated-process survival, argument boundaries and invalid settings:

```
python -m pytest tools/guo/test_build_guard.py -q
```
