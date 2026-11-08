# Cloud worker review, 2026-11-08

This set is a whole-project review of GUO as `main` stood on 8 November 2026.
It is for the next cloud worker and for Moshu choosing follow-up tasks. It
does not replace `AGENTS.md`, `docs/port_plan.md`, the ADRs, or
`docs/data_formats.md`. Those remain the contracts. These pages record what
was inspected, what was run, and what is worth doing next as a separate pull
request.

| Page | What it is |
|---|---|
| [Operating boundaries](operating_boundaries.md) | What a GUO cloud worker may change, and what this checkout cannot prove |
| [Architecture map](architecture_map.md) | Subsystems, languages, and the contracts a change has to keep |
| [Build and test runbook](build_test_runbook.md) | Commands actually run in this review, and the ones that were blocked |
| [Review findings](review_findings.md) | Reproducible findings with evidence, remedy, and a regression check |
| [Coverage ledger](coverage_ledger.md) | Inspected areas, unreviewed areas, confidence |
| [Work proposals](work_proposals.md) | Ranked isolated tasks. None of them are implemented here |
| [Fact database](../facts/README.md) | JSONL source of truth, schema, and the SQLite query tool |

Baseline commit: `0aec6eb8ccc53a01655fbb8872211ce2358c9992`
(`Merge pull request #193`, committed 2026-11-08 12:43:56 -0600).
Review observation time: 2026-11-08T19:38:21Z. No open pull requests at that
time. Pull request 179 had already merged into `integration` on
2026-10-31 and is history; the speed-mode code it described is on this
baseline and still defaults off. See [review findings](review_findings.md).

This pull request adds documentation and `tools/project_facts`. It does not
change client behaviour, shaders, packets, profiles, or CI.
