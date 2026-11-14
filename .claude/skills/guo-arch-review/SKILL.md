---
name: guo-arch-review
description: Deep source-based architecture review of a GUO subsystem or cross-system change, with dependency tracing, ClassicUO parity, lifecycle analysis, and reproducible findings. Use for GUO code reviews and architectural audits, rather than GDD coverage reviews.
---

# GUO architecture review

Read [the section map](../../../docs/review/sections.md) and
[the review contract](../../../docs/review/contract.md). Choose the requested
section or trace the requested file into its owning section. For a whole-project
request, inventory coverage first; do not claim an exhaustive audit from a sample.

Read `AGENTS.md`, `docs/port_plan.md`, the matching path rules and accepted ADRs.
Resolve later accepted ADRs before treating an older plan as current architecture.
Inspect the working-tree status and record the revision and relevant dirty files.

Trace one complete operation from entry point through state ownership, data IO,
render/network side effects, persistence, cancellation and teardown. Inspect callers
and consumers, not only the class named in the request. Draw a dependency sketch
when ownership is unclear. Include failure and repeated-use paths.

Check the boundaries that matter in GUO:

- L0 keeps upstream behavior and marked deviations; Compat holds values, not engine
  ownership. Compare against the available read-only upstream revision.
- Renderer ordering and picking share depth semantics; new caches need invalidation
  on world, facet, overlay, profile and resource changes as applicable.
- Editor code is behind `TOOLS`; Labs is explicit Debug-only opt-in. Inspect build
  conditions and export contents before claiming an export boundary works.
- Loaders, Godot objects, native handles, subscriptions and services have an owner,
  thread rule and bounded lifetime. Rebuild/hot reload must not duplicate them.
- Client installs are read-only. Authoring stages overlays or packs; rendering a
  result does not authorize installation, publication or shard mutation.

Use the relevant companion review for UI, performance, networking, portability or
readiness; read only what the scope needs. A review is not permission to reformat
ported code or redesign an unrelated subsystem. If fixes are requested, make small
changes and prove the original failing behavior plus affected boundary behavior.

Return evidence-backed findings using the contract. Include inspected entry points,
unreviewed neighbors, tests actually run, and a suggested next bounded review.
