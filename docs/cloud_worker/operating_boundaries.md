# Cloud worker operating boundaries

## What this role is

Moshu appointed a cloud worker to review GUO, write documentation, keep a
fact database, and propose isolated follow-up tasks. The worker works only
in this repository. It starts from the default branch (`main`), records the
baseline SHA, and treats older notes (including the 31 October lighting
pull request) as historical until the current tree confirms them.

The project rules in `AGENTS.md` still apply. In particular: do not edit
`sources/`, do not commit client art or credentials, do not grow
`GUO.Compat` into an engine, and do not treat a filename match as proof
that a feature works.

## What a cloud worker may change

- Documentation under `docs/`, including this set.
- Python tools and their tests under `tools/`, when the change is
  deterministic and does not need the UO install.
- A fact row in `docs/facts/facts.jsonl`, with a source path that exists
  and an excerpt that the validator can find.
- A pull request that proposes a later change, without implementing that
  change in the same branch unless Moshu asked for the implementation.

A new tool folder needs a `README.md`. `tools/docs_lint` checks that.
A new launcher needs a `.bat` and `.sh` pair. This review's query tool is
invoked as `python tools/project_facts/run.py` and has no launcher.

## What stays off this role

- Merges, releases, deploys, and force-pushes.
- Messages to people, issues, or chat systems. A pull request description
  is the handoff.
- Org settings, fleet settings, bot skills, and bot memory.
- Machine-local config (`launchers/_shared/config.local.bat` and
  `config.local.sh`), shard secrets, and anything under the user workspace.
- Importing or generating Ultima Online art, mul/uop/idx files, or captures
  made from them.
- Runtime behaviour of the client: rendering, input, packets, profile
  defaults, and exported builds.
- Editing `sources/ClassicUO`. It is a read-only pin, and it is not even
  in a fresh clone (`sources/` is gitignored).

Feature work that ClassicUO would not ship belongs in L2–L4, not in the
core port. Lighting speed mode is already an opt-in L1-style option and
ADR-0048 is still Proposed. A cloud worker does not turn it on by default
and does not wire the inverse shadow mask without an owner decision.

## What this environment cannot prove

Checked on the review VM, 2026-11-08:

| Need | State here |
|---|---|
| Python | 3.12.3, usable |
| .NET SDK | not installed |
| Godot 4.7.2 mono | not installed; `tools/godot` has the Windows `.cmd` stubs |
| `sources/ClassicUO` | absent |
| UO client data | not present, and must not be copied in |
| GPU / shard | not used |

So a green Python test on this VM is not a smoke test, not a compile of
`godot/GUO/GUO.csproj`, and not a visual qualification. The Windows CI
build job on the baseline SHA did succeed; the guard job did not. Both
are recorded in the [runbook](build_test_runbook.md).

## How to refresh a fact

Facts are claims with a file excerpt or an external URL. When the cited
lines change, `python tools/project_facts/run.py validate` fails until the
excerpt or the statement is updated. That failure is the stale signal.
Do not pad the file to raise a count. A proposal row is not a fact about
current behaviour; its status is `unknown`.
