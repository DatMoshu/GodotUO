# tools/arg_trace_tests

SF7's regression suite: the client's argument trace (`ArgTrace` in the game
assembly) must never print a username or password, and must keep every other
option's value so the trace stays useful. A console project that compiles
against `godot/GUO/GUO.csproj`; no engine, client data or shard.

## Run

```
dotnet run --project tools\arg_trace_tests
```

## Tests

This folder is the test. It prints one `PASS` line per check and exits non-zero on the first failure.
