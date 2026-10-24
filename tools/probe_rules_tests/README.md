# probe_rules_tests

The perf probe's preflight rules (`godot/GUO/src/Bootstrap/LoginPreflight.cs`),
compiled on their own and run with dotnet: which credential a measuring run
may use (the allocated account's configured secret, never the account text),
what typing into a prefilled login field does, which profile identity counts
as the expected one, and where a scene counts as reached (x, y, z and map).
No engine, no shard, no login.

```
dotnet run --project tools\probe_rules_tests\ProbeRulesTests.csproj -c Release
```

The smoke runs it. The same rules run inside the client as
`--login-preflight-probe` (headless, no shard), which the smoke runs too.
