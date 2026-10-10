# tools/localization_tests

The runtime localization suite: the language catalogue and its aliases, the
journal translation session, and the providers' directions. A console project
that compiles against the game assembly; see `docs/localization-runtime.md`.

## Run

```
dotnet run --project tools/localization_tests/LocalizationTests.csproj
dotnet run --project tools/localization_tests/LocalizationTests.csproj -- --languages-live
```

## Tests

This folder is the test. Offline by default; `--languages-live` also calls the live translation services. One `PASS` line per check, non-zero exit on the first failure.
