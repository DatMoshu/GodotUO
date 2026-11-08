# Isolated work proposals

These are not implemented in the review pull request. Each one is small
enough to be its own pull request, except P8 and P9, which are
qualification and an owner decision rather than a cloud edit.

Order is the recommended sequence. Effort is relative: **small** (one
tool or doc surface, a reviewer can read it in one sitting), **medium**
(a few subsystems, still one feature), **large** (renderer behaviour,
art or shard proof). No durations.

Classification:

| Class | Meaning |
|---|---|
| cloud-ready | A cloud worker can land it with deterministic tests or doc diffs, and CI can prove the rest |
| needs-coordination | The contract is shared. Do not change it on a guess |
| needs-local-validation | The proof needs Godot, client art, a GPU, or a shard |
| blocked | Cannot be finished in a checkout that lacks that proof. P8 is this, on the review VM |

## Order

1. P1, because `main` CI is red.
2. P2, because the Linux health check silently drops tests.
3. P3 and P4 and P5, docs, in any order. They do not collide if they
   touch different files. P4 and P5 both touch wiki pages; do them as
   two pulls or one agreed docs pull.
4. P7, latent SDK pin. Independent of P1.
5. P10, after P1, so the guard job is worth adding a step to.
6. P6, only with a decision on config order.
7. P8 when a machine has Godot and an install. P9 only if Moshu selects it.

## P1. LAN certificate and guard Python

- **Id:** `prop.ci.lan-ca-and-python`
- **Class:** cloud-ready
- **Value:** Turns the red guard job green and makes the dev LAN CA match what Python 3.14's verifier requires.
- **Paths:** `tools/guo/lan.py` (`ensure_certificates`), `.github/workflows/ci.yml` guard job, existing `tools/web/test_run.py`.
- **Ownership:** web/tooling. No ported ClassicUO file. Conflicts with anyone editing `lan.py` or the guard job.
- **Depends on:** nothing.
- **Effort:** small.
- **Exclusions:** no change to packet crypto, no public CA, no committed cert or key, no client art.
- **Deliverables:** key identifiers on the CA and server cert; guard job uses Python 3.12; note in `tools/web/README.md` if a phone must reinstall the CA.
- **Acceptance:** `python3 tools/web/test_run.py` exits 0 on 3.12. The GitHub guard job exits 0 on the runner image. A negative check: the CA PEM contains an authority key identifier extension.
- **Rollout:** merging the workflow is the rollout. Rollback is reverting the commit. Phones that trusted an older CA install the new one from `/guo-ca.crt` after it regenerates.
- **Limit:** this VM did not run Python 3.14. The failing log is Actions run 42511454234.

## P2. Align `smoke.sh` with `smoke.bat`

- **Id:** `prop.test.smoke-sh-parity`
- **Class:** cloud-ready for the script and a static comparison test. Running the new steps needs the .NET SDK (CI's Windows job, or a dev machine).
- **Value:** Linux and macOS smoke stop dropping network, sort-order and server-lab checks, and the preflight probe is labelled as itself.
- **Paths:** `launchers/dev/smoke.sh`. A new assertion beside `tools/launcher_lint` or a tiny test under `tools/`. `launchers/dev/smoke.bat` is the reference, not a rewrite.
- **Ownership:** tools. Conflicts with anyone editing smoke.
- **Depends on:** nothing functional. Do not wait for P1.
- **Effort:** small.
- **Exclusions:** do not delete Windows steps to make the files match. Do not require client data for the three added steps; they are already synthetic on Windows.
- **Deliverables:** the three invocations and a preflight banner; a test that fails if the project paths diverge.
- **Acceptance:** the new test passes. On a machine with `dotnet`, `network_tests` and `render_sort_tests` exit 0. `server_lab/test_server_lab.py` exits 0 without a shard.
- **Rollout:** the next smoke on Unix runs more steps and can newly fail. Rollback is reverting `smoke.sh`.
- **Limit:** not executed here. See finding F2.

## P3. ADR index rows for files already on disk

- **Id:** `prop.docs.adr-index`
- **Class:** cloud-ready
- **Value:** The number board matches the directory, including Accepted ADR-0045.
- **Paths:** `docs/architecture/README.md` only.
- **Ownership:** port strategy. Conflicts with any branch that reserves the next ADR number in that table.
- **Depends on:** none. Do not renumber.
- **Effort:** small.
- **Exclusions:** do not change ADR bodies. Do not mark 0007's original proposal Accepted. Do not assign 0046 if the footnote still reserves it.
- **Deliverables:** rows for 0039, 0042, 0043, 0045, and a one-line note for the unnumbered ADR files. Status words copied from each header.
- **Acceptance:** every `ADR-00*.md` numbered file appears in the table or in an explicit "unnumbered" list. `docs_lint` passes.
- **Rollout:** docs only. Rollback is reverting the README.

## P4. Point tier counts at the generated audit

- **Id:** `prop.docs.tier-counts`
- **Class:** cloud-ready
- **Value:** The plan and the wiki stop teaching the 23 September counts.
- **Paths:** `docs/port_plan.md`, `docs/wiki/Architecture.md`.
- **Ownership:** docs. Leave `docs/port_status.md` generated.
- **Depends on:** none. A later audit refresh is a different pull, and it needs `sources/ClassicUO`.
- **Effort:** small.
- **Exclusions:** do not type a new tier table by hand.
- **Deliverables:** the September table replaced with a short pointer and the date of the committed `port_status.md`.
- **Acceptance:** docs lint, and a search that the 247/116/37 trio is gone from those two files or clearly marked historical.
- **Rollout:** docs only.

## P5. Field-keyword citation

- **Id:** `prop.docs.field-keyword-citation`
- **Class:** cloud-ready
- **Value:** Getting Started sends an SDK 8 failure to the file that contains `field`.
- **Paths:** `docs/wiki/Getting-Started.md`.
- **Ownership:** docs.
- **Depends on:** none. Can share a branch with P4 if both are wanted as one docs pull; otherwise keep it separate.
- **Effort:** small.
- **Exclusions:** no C# edit.
- **Deliverables:** the sentence no longer claims `SystemChatControl.cs` unless a compile log shows it.
- **Acceptance:** docs lint. Optional: an SDK 8 build log attached, if someone has that SDK.
- **Rollout:** docs only.

## P6. Config fallback and central file

- **Id:** `prop.config.version-and-common`
- **Class:** needs-coordination
- **Value:** A tool started without `common.sh` uses the same client version as the launchers, and the `UO_COMMON_CONFIG` story is either implemented or deleted from the "matches exactly" sentence.
- **Paths:** `tools/guo/config.py`, `tools/guo/test_config.py`. Launchers stay the contract.
- **Ownership:** tools. Conflicts with profile and launcher work.
- **Depends on:** a decision. Do not silently treat 7.0.15.1 as wrong if some tool fixture depends on it; the search of `test_config.py` in this review found neither version string.
- **Effort:** small once the decision is made.
- **Exclusions:** no profile schema change, no new config key.
- **Deliverables:** one fallback, tests for "env unset" and for a temp `UO_COMMON_CONFIG` if that path is supported.
- **Acceptance:** `python3 tools/guo/test_config.py` and the existing launcher lint.
- **Rollout:** affects bare `python tools/...` only. Rollback reverts `config.py`.

## P7. Ask `build.yml` for SDK 10

- **Id:** `prop.ci.build-yml-sdk-10`
- **Class:** cloud-ready
- **Value:** Desktop and Android CI jobs request the SDK the wiki says is required, instead of hoping the Windows image still has it.
- **Paths:** `.github/workflows/build.yml` both `setup-dotnet` blocks.
- **Ownership:** CI. `ci.yml` already installs `10.0.x` and is the pattern.
- **Depends on:** none. Not a fix for the red guard job.
- **Effort:** small.
- **Exclusions:** do not drop SDK 8 if the net8.0 runtime install depends on that line; check the workflow comment and keep the runtime.
- **Deliverables:** `10.0.x` in the desktop and Android jobs.
- **Acceptance:** the next `build.yml` run on a `godot/GUO` change prints Build succeeded, and the log shows SDK 10 was requested.
- **Rollout:** next path-filtered run. Rollback reverts the workflow.
- **Limit:** finding F7. The last observed desktop build was already green.

## P8. Native qualification of speed mode, no shader edit

- **Id:** `prop.qualify.speed-mode-native`
- **Class:** needs-local-validation. **Blocked** on this review VM (no Godot binary, no client data).
- **Value:** Closes the gap PR 179 left open: the height gate, cover clip and daylight curve are in source, and nobody has a native rendered pass/fail on this baseline.
- **Paths:** existing probes only: `NativeWeatherSceneProbe`, `ReceiverHeightProbe.tscn`, `SpeedModeWeatherProbe.tscn`. Read `docs/data_formats.md` section that already describes the witness. Add a short result note under `docs/` if the run happens. No shader change in the first pass.
- **Ownership:** render. ADR-0048 stays Proposed until Moshu accepts it.
- **Depends on:** a Windows or Linux machine with Godot 4.7.2 mono, .NET SDK 10, and a legal UO install in `UO_CLIENT_DATA`.
- **Effort:** medium, because the proof is operational, not because the diff is large.
- **Exclusions:** no default-on, no new profile field, no inverse-mask wiring (that is P9).
- **Deliverables:** probe logs and, if a frame is captured, a note of zoom, profile flags and whether lighting speed mode was on. Off-state must stay the classic frame.
- **Acceptance:** the probe process exits 0, or the log states the first failing check. A screenshot is required before anyone calls water reflections qualified.
- **Rollout:** none if the result is a doc. A follow-up code change is a new proposal.
- **Rollback:** not applicable to a read-only run.

## P9. Inverse mask and sky corridor

- **Id:** `prop.render.receiver-inverse-mask`
- **Class:** needs-local-validation, and blocked until Moshu selects it.
- **Value:** The contract file already says what is missing: per-pixel inverse mask, roof-plane receivers, a sun corridor that can reach SkyZ 512.
- **Paths:** `godot/GUO/src/Render/SpeedMode/Shadows/`, ADR-0049. Not this review.
- **Ownership:** render, with an ADR update. Conflicts with any lighting branch, including the still-published `work/guo-lighting-next-20261031`.
- **Depends on:** P8's record, and an owner decision. ADR-0048 is Proposed.
- **Effort:** large.
- **Exclusions:** do not start from a cloud worker. Do not enable the mode by default. Do not import reference-client art.
- **Deliverables:** only after selection: a design note, then a bounded change, then GPU evidence.
- **Acceptance:** not defined until the design note exists. Off-state parity remains mandatory.
- **Rollout:** profile default stays false. Rollback is the commit plus the profile flag.

## P10. Validate facts in the guard job

- **Id:** `prop.ci.facts-on-guard`
- **Class:** cloud-ready, after P1.
- **Value:** Excerpt drift fails on the Ubuntu guard job, not only when Windows pooled pytest collects `tools/project_facts`.
- **Paths:** `.github/workflows/ci.yml` guard job, one extra step.
- **Ownership:** CI. The tool already exists.
- **Depends on:** P1, so the job is green enough to see a new failure.
- **Effort:** small.
- **Exclusions:** do not upload a SQLite artifact from CI. JSONL stays the source.
- **Deliverables:** `python3 tools/project_facts/run.py validate` in the guard job.
- **Acceptance:** a deliberate bad excerpt fails the job; the real file passes.
- **Rollout:** next push. Rollback removes the step.

## Best three to start

P1, P2, and P3. They are cloud-ready, they do not change the game, and
they are independent enough to be three pulls. P1 is first because the
default branch's guard job is failing now.
