# PerfProbe finite-result regression

Tools-only regression for the shared client report writer. Run with .NET8:

```
dotnet run --project tools/perf_probe/finite_results/FiniteResults.csproj --artifacts-path <absolute-output-folder-outside-checkout>
```

The client report keeps finite numbers unchanged. Nonfinite floating metrics become JSON null, with exact JSON-style paths and reasons in the top-level `invalid_metrics` array; `numeric_metrics_valid` is false. Literal text such as the string Infinity is preserved. This normalization is outside the timed measurement window.

Every scene also records `measurement_valid` and `measurement_issue`. No samples, nonpositive/nonfinite elapsed time, nonpositive/nonfinite mean time, or a nonfinite metric makes the scene unqualified and prevents PerfProbe from passing. Missing sample statistics stay nonfinite internally until report serialization emits null with reasons; they are never replaced by valid zero timing. Existing finite counters remain finite. The JSON file is written to a sibling temporary file and replaced only after serialization and write succeed.

These tests cover numeric report semantics and sample-validation decisions. They do not establish successful login, actual player/map/location, feature-positive rendering, frame-rate performance, leak freedom or Linux scene qualification. A successful JSON write is not a valid benchmark by itself. Keep exact source/build/scenario identities and compare against a baseline on the same machine.
