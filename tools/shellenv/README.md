# tools/shellenv

The resolved configuration as POSIX shell exports, for the `.sh` launchers:
`launchers/_shared/common.sh` evaluates it, the shell twin of `common.bat`
calling `config.bat`. Every setting is resolved by `tools/guo/config.py` in the
usual order, so the `.bat` files stay the one place settings are written on
every OS. A value still holding a Windows-only `%VAR%` is left out.

## Run

```
eval "$(python3 tools/shellenv/run.py)"
```

## Tests

Resolution is covered by `python tools/guo/test_config.py`; this file only prints it.
