# GUO deep review contract

This is the shared procedure for the GUO review skills. The
[section map](sections.md) bounds each review. These reviews use tracked project
sources and standard project tools; private coordination systems are optional local
integrations, never required inputs.

## Scope and evidence

Record revision, relevant dirty files, feature layer, supported targets, requested
workflow and inspected entry points. Read current source and applicable accepted
ADRs. Resolve apparent conflicts against later decisions; an old plan or a comment
may describe a superseded implementation. Preserve unrelated work and read-only
upstream sources.

Trace registration -> caller -> state owner -> side effect -> consumer -> cleanup.
Include failure, cancellation, repeat use and persistence. A class-level inspection
without its callers and consumers cannot establish end-to-end correctness. Inventory
uninspected paths explicitly. Use small follow-up reviews rather than a blanket
“whole project passed” conclusion.

Separate these forms of evidence:

| Evidence | What it establishes |
|---|---|
| Source trace | A concrete reachable behavior or a hypothesis needing runtime proof |
| Build | Compilation for that configuration; no interaction or parity claim |
| Fixture/unit check | The tested invariant; note copies/stubs versus production code |
| Integration scenario | The exercised path with stated data and dependencies |
| Viewed capture and interaction | The observed layout/input/result at stated size and scale |
| Matched performance run | Measurements for that workload and configuration only |
| Clean-environment run | Setup for that environment without ambient local dependencies |

Keep captures, logs and baselines outside the checkout under the configured build
directory or a user-selected external evidence directory. Use synthetic or permitted
local fixtures. Do not publish client art, credentials, private traffic or local paths.
Link evidence in the local report; public summaries need sanitized reproducible steps.

## Findings and verdicts

For each actionable finding record:

```text
ID / priority / section / layer:
File and current line or symbol:
Trigger and prerequisites:
Observed behavior and evidence (or explicitly unverified hypothesis):
User impact and violated contract:
Smallest proposed correction:
Acceptance check, including failure/off-state behavior:
```

Use P0 for immediate catastrophic failure, P1 for blocking data loss, security or
core-workflow failures, P2 for material correctness/performance/usability defects,
and P3 for low-impact polish. Priority follows demonstrated impact and reachability.
Do not fill a quota, report stylistic preferences as defects, or claim an unmeasured
speed improvement. Separate design options from findings. “No findings” must include
coverage limits and tests run.

For readiness use READY, EXPERIMENTAL or NOT READY, scoped to the workflow and targets.
Record **not tested** for missing acceptance evidence. Existing implementation,
historical screenshots, an accepted ADR and a green build do not prove current
readiness. A proposed gate in this document is not an implemented CI enforcement.

## Main exposure gate

Normal main-branch menus, tabs and workflows must expose only ready features.
Required evidence depends on the change: complete workflow; clear setup/empty/error
states; safe save/undo/recovery; cancellation/reload; parity; representative performance;
supported targets; and export separation. Use the
[readiness skill](../../.claude/skills/guo-readiness-review/SKILL.md) to apply it.

L1 and L4 retain their existing toggle/scenario/doctor/documentation requirements.
L3 remains editor-only, with AI work additionally behind `AiFeatures`; manual tools
must still work with AI disabled. Hidden UI must not secretly start optional services.
Unfinished features stay on feature branches, or in suitable explicitly opted-in
isolation where that boundary has itself been tested. “Extra Tabs” is navigation,
not isolation. Do not announce a feature ready because its button has been hidden.

## Validation selection

Read tool READMEs before running target-specific probes. Useful existing entry points:

- `python tools/agent_skills/run.py --check` and `python tools/docs_lint/run.py` for
  skill/documentation changes; launcher lint and privacy scan for affected setup files.
- `dotnet build godot/GUO/GUO.csproj` for C#; the project smoke launcher before a commit.
- `python tools/editor_smoke/run.py --reload` for editor lifecycle; visual interaction
  needs separate evidence and a desktop session permitted by the environment.
- `dotnet run --project tools/network_tests` plus the production diagnostics probe
  described in [network tests](../../tools/network_tests/README.md).
- The [performance gate](../perf/performance_gate.md) for count regressions, with
  separate representative latency and gameplay evidence.
- [Scenario runner](../../tools/scenario_run/README.md), server lab and export doctors
  for the applicable feature/target. Inspect existing cases before inventing tests.

Run checks proportional to the change. Record command, result, environment and what
was not tested. Review-only work does not require launching every engine/device or
installing every optional service. Fix requests still require appropriate validation;
do not merge, publish, change remote state or operate a live shard merely to review it.
