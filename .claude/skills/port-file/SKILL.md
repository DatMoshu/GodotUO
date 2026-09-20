---
name: port-file
description: "Port one file or one subsystem from ClassicUO into the Godot project, following its audit tier. Handles namespace and using rewrites for verbatim/shim files, routes rewrite-tier files to the renderer specialist, builds, and re-measures. Use whenever moving code from sources/ClassicUO into godot/GUO."
argument-hint: "<upstream path or subsystem> [--tier verbatim|shim|rewrite]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash, Write, Edit, Task, AskUserQuestion
model: sonnet
---

# Port File

Moves code from the read-only ClassicUO reference into the Godot project,
correctly and without collateral changes.

The discipline this skill enforces exists for one reason: **upstream is still
alive**. Every gratuitous edit made during porting is an edit that must be
reconciled by hand every time a ClassicUO fix is pulled across. Faithful
ports stay mergeable; "improved" ports do not.

---

## 1. Establish the tier

Never guess. Check `docs/port_status.md`, or re-measure:

```
launchers\pipeline\03_port_audit.bat
```

| Tier | Test | Treatment |
|---|---|---|
| `verbatim` | No `Microsoft.Xna` reference | Renamespace only |
| `shim` | Only `using Microsoft.Xna.Framework;` | Renamespace + `using GUO.Compat;` |
| `rewrite` | Graphics / Input / Audio / Media | Reimplement on Godot |

`--tier` overrides the audit only when you have read the file and the audit
is demonstrably wrong. Say so explicitly if you use it.

---

## 2. Check dependencies first

List what the file references and confirm those are already ported. Porting
a loader before its reader produces a file that cannot compile and cannot be
tested.

The dependency order for this project is
`Utility` → `IO` → `Assets` → `Game/Data` → everything else.

If a dependency is missing: **stop and report it**. Do not stub it out. A
forgotten stub is a bug that surfaces much later, in a much more confusing
place.

---

## 3. Port

Read the whole upstream file first — never port from a fragment.

Destination is `godot/GUO/src/<area>/`, where `<area>` comes from the
audit. Preserve upstream's internal folder structure within an area.

### verbatim and shim

Apply exactly these changes and no others:

- `namespace ClassicUO.X.Y` → `namespace GUO.X.Y`
- update `using ClassicUO.*` lines to match
- **shim only:** `using Microsoft.Xna.Framework;` → `using GUO.Compat;`

`Vector2`, `Vector3` and `Vector4` are globally aliased to Godot's types and
need no import.

**Do not** reformat, rename, modernise, add `var`, reorder members, or fix
bugs. If you find a real bug, record it in the report and leave it in place.

### rewrite

Do not attempt it inline. Delegate to `uo-render-engineer` (rendering,
input, audio) via Task, with the upstream file and what it must do. Rewrites
need parity judgement, not translation.

---

## 4. Build early and often

```
launchers\dev\build.bat
```

Build after each small batch, not after fifty files. Errors are cheap to
diagnose one file at a time and expensive in bulk.

If a `shim` file needs a Compat type that does not exist:

- **a value type** (a struct with no engine behaviour) — add it to
  `godot/GUO/src/Compat`, following the rules in that folder's README,
  and say that you did.
- **anything touching a device, texture or node** — the file is
  misclassified. Escalate to `uo-port-strategist`. Never add engine behaviour
  to Compat to force a compile.

---

## 5. Verify and re-measure

```
launchers\dev\smoke.bat
launchers\pipeline\03_port_audit.bat
```

---

## 6. Report

- Files ported, with tiers and destinations.
- Any Compat additions, with justification.
- Any tier misclassifications found.
- Any bugs spotted and deliberately left alone.
- New audit numbers.
- **What you verified.** "Builds" is not "works". Be precise about which one
  you actually established.

---

## Licensing

ClassicUO is BSD 2-Clause. Ported files must keep their upstream copyright
header. `docs/upstream/` records the licence and the exact commit this port
derives from — keep it accurate, and re-pin after a sync.
