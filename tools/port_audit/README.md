# tools/port_audit

Measures the port: walks every upstream C# file, classifies it `verbatim`,
`shim` or `rewrite` (see `docs/port_plan.md`), works out where it belongs in
`godot/GUO` and reports done against left. With `--out docs/port_status.md`
(what the launcher does) it rewrites the committed status page.
Progress claims come from here, not estimates.

## Run

```
python tools\port_audit\run.py [--out docs/port_status.md] [--json FILE]
launchers\pipeline\03_port_audit.bat
```

## Tests

No unit tests. CI reruns the audit on every push and fails when the committed
`docs/port_status.md` is stale.
