# tools/script_tests

The embedded scripting suite: `ScriptRunner` against a fake host (whole-script
validation before any action, quoting, comments, delays, size bounds) and the
script pack checks. A console project that compiles against the game assembly;
see `docs/embedded-scripting.md`.

## Run

```
dotnet run --project tools/script_tests/ScriptTests.csproj
```

## Tests

This folder is the test. One `PASS` line per check, non-zero exit on the first failure.
