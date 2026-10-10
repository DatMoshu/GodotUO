# tools/port_triage

Asks whether a `rewrite`-tier file really is one. The audit tiers by imports;
this reads the bodies and reports which FNA types each rewrite file actually
names, flagging those that name none as mechanical after all. A diagnostic
that produces candidates for a human to confirm, deliberately separate from
the audit's score.

## Run

```
python tools\port_triage\run.py
```

## Tests

No tests: a read-only report.
