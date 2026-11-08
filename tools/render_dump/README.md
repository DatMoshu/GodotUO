# tools/render_dump

`inject.targets`: compiles GUO's `RenderDump.cs`, `PerfDump.cs` and
`ShotDump.cs` into the out-of-tree ClassicUO build, so ClassicUO writes the
same dumps GUO does without a line under `sources/` changing. It also adds the test-only
automation server of `tools/cuo_automation` (inert unless `GUO_MCP_PORT` and `GUO_MCP_TOKEN` are set). `tools/ab_compare`
imports it with `-p:CustomAfterMicrosoftCommonTargets`. Used by
`tools/render_diff`, `tools/perf_dump` and `tools/visual_ab`.

## Run

```
python tools\ab_compare\run.py --build     builds ClassicUO with this file; nothing to run here
```

## Tests

No tests of its own. A ClassicUO build that writes `build/render_dump/NAME/cuo.json` proves it.
