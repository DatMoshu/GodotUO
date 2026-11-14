---
name: guo-readiness-review
description: Decide whether a GUO feature is ready for normal main-branch exposure using end-to-end, failure, performance, parity, clean-install and export evidence. Use before enabling tabs, options, tools or release features.
---

# GUO feature readiness review

Use [the review contract](../../../docs/review/contract.md). Classify the feature
L0/L1/L2/L3/L4/Tools and inventory every entry point: default tab/menu, shortcut,
search result, profile option, command, MCP method, startup registration and docs.
Inspect the default build and configuration, not only a developer's saved settings.

The policy is **no untested or rough features exposed through normal main workflows**.
`TOOLS` excludes editor code from client exports; it does not make an editor feature
ready. Off by default is necessary for experiments but is not sufficient proof of
safe isolation. An “experimental” label or an Extra Tabs menu is not a gate.

For a ready verdict, collect evidence for:

- A complete useful workflow and persisted result that reopens and is consumed by
  its intended reader, including actual input/visual checks where applicable.
- Empty/missing inputs, invalid data, cancellation, retry and recovery without loss.
- Undo/save safety, hot reload and repeated entry/exit for stateful editor features.
- Clean installation with documented prerequisites, dependency doctor and actionable
  failure messages; no private checkout, prepared catalog or maintainer-only binary.
- Applicable parity and representative performance checks, including off-state cost.
- Supported target behavior and export inspection excluding editor/Labs dependencies.
- Accurate docs, license/provenance and evidence free of credentials/proprietary data.

Give one verdict: READY (specified workflow/targets only), EXPERIMENTAL (isolated and
explicitly opted in), or NOT READY (normal exposure blocked). Missing proof cannot
be silently passed. Record blockers separately from optional polish and specify the
exact acceptance check for each blocker. Explain the verdict's tested scope.

Propose the smallest containment/fix. Keep unfinished work on a feature branch until
the required evidence exists, or use an existing appropriate isolation mechanism when
safe and permitted. Never put editor authoring into a runtime Labs folder merely to
hide it. Review mode does not authorize disabling features, merging or publishing.
