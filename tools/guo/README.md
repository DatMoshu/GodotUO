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
