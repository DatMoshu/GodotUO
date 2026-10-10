# selftest

Runs the Python tool self-tests one after another and says which failed.

```
python tools/selftest/run.py                 every one
python tools/selftest/run.py --list          the list, nothing run
python tools/selftest/run.py --only store    only those whose path holds "store"
```

Launcher: `launchers/dev/selftest.bat` / `.sh`.

The list is every `python tools/.../test_*.py` line in
`.github/workflows/ci.yml`, so this and CI always run the same self-tests:
add a test to CI and it is here too. None needs client data, a shard or the
engine. Standard library only.
