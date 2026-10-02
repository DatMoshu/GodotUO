# Godot (third-party, pinned)

| | |
|---|---|
| **Version** | 4.7.2-stable, **mono/.NET** build (`4.7.2.stable.mono.official.ed1daf0bf`) |
| **Origin** | <https://github.com/godotengine/godot/releases/tag/4.7.2-stable> |
| **Asset** | `Godot_v4.7.2-stable_mono_win64.zip` |
| **Installed** | 2026-09-20 |
| **Tracked in git?** | **No** — the extracted build is gitignored (~350 MB). The two `.cmd` shims *are* tracked. |

The `.NET`/mono build is required: this project ports ClassicUO's C# to Godot,
so the engine must be able to build C# assemblies. The standard (non-mono)
build will not work.

## Entry points

Add this folder to `PATH` so `godot` resolves everywhere:

```
<your clone>\tools\godot
```

| Command | Use it for |
|---|---|
| `godot` | Interactive editor. Returns immediately; no console output. |
| `godot-console` | **Scripts, agents and CI.** Blocks until exit and writes stdout/stderr. |

Both shims resolve the pinned build, so the version can be bumped in one place.

## Re-installing

The build is not in git. On a fresh clone, restore it with:

```
launchers\dev\fetch_godot.bat
```

On Linux, `launchers/dev/fetch_godot.sh` fetches
`Godot_v4.7.2-stable_mono_linux_x86_64.zip` instead. That build has no
separate console executable: the one binary blocks and writes to stdout, so
`tools/guo/config.py` uses it for both. The `.cmd` shims are Windows only.

## Upgrading

1. Edit `GODOT_VERSION` in `launchers\_shared\config.bat`.
2. Run `launchers\dev\fetch_godot.bat`.
3. Update the two `.cmd` shims and this README to match.
4. Re-run `launchers\dev\smoke.bat` before committing.
