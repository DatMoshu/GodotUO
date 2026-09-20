---
name: port-status
description: "Measure and report ClassicUO to Godot port progress. Runs the port audit, checks upstream drift, verifies client data, and reports what is done, what is next, and what is at risk. Use to answer 'how far along is the port', at the start of a session, and before planning a subsystem."
argument-hint: "[--area IO|Assets|Network|Render|Game|...] [--brief]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# Port Status

Answers "how far along is the port, and what should happen next" with
measured numbers rather than impressions.

This project is large enough (~155,000 lines upstream) that intuition about
progress is worthless within a week. Everything below is measured fresh each
time it runs.

---

## Run the measurements

Run all three. They answer different questions and each is cheap.

```
launchers\pipeline\03_port_audit.bat
launchers\dev\sync_upstream.bat --no-fetch
launchers\pipeline\01_verify_client_data.bat --quiet
```

If a launcher fails because Godot or the upstream reference is missing, say
so and point at `launchers\pipeline\00_bootstrap.bat` rather than working
around it.

---

## Read the audit

`docs/port_status.md` is the output. The three tiers are the key to reading
it:

- **`verbatim`** — no FNA reference; copy and renamespace. Cheap.
- **`shim`** — only XNA maths/colour types; swap the `using` to
  `UOPort.Compat`. Cheap once Compat covers the types.
- **`rewrite`** — real FNA binding; reimplement on Godot. Expensive.

A percentage on its own is misleading, because the tiers differ in cost by
roughly an order of magnitude. **Always report progress per tier**, not just
overall. 60% overall with the rewrites untouched is a very different position
from 60% with them done.

---

## Report

Keep it short and concrete.

**1. Headline** — files and lines done overall, then the per-tier split.

**2. By area** — which subsystems are complete, in progress, untouched.
Call out whether the order respects the dependency chain
(`Utility` → `IO` → `Assets` → `Game/Data` → rest); work proceeding out of
order is worth flagging.

**3. Upstream drift** — number of unreviewed upstream commits, and
specifically any that touch **already-ported** files. Those are the ones that
rot silently. If there are none, say so in one line.

**4. Client data** — one line. Only elaborate if a required file is missing.

**5. Next actions** — three to five specific files or a named subsystem, with
their tiers, drawn from the audit's suggestions. Not "continue porting IO".

---

## Arguments

- `--area <name>` — restrict the report to one area from the audit.
- `--brief` — headline and next actions only; skip the per-area table.

---

## Honesty rules

- Never report a number you did not just measure.
- A file existing in the port is not the same as that file working. The audit
  matches by filename; it cannot tell you the port is correct. Say
  "ported" when you mean ported, and do not upgrade it to "working" unless a
  smoke test or a screenshot backs it.
- If the audit and your expectation disagree, the audit is right.
