# Code review findings

Baseline `0aec6eb8ccc53a01655fbb8872211ce2358c9992`. A finding is listed
only where this review could point at a file or a log. Counts of "issues"
were not invented. Hypotheses are labelled.

Severity is impact on the project if left alone: high (CI on `main` is
red, or a player-facing contract is wrong), medium (a documented gate
does not match its twin), low (a reader or a later agent will copy a
stale sentence).

## How a row is shaped

Each finding has the source, the impact, a way to reproduce or the
contract it breaks, a contained remedy, and the regression check that
should land with the remedy. The remedy is not in this pull request.

## F1. Guard job is red: LAN CA versus Python 3.14

- **Severity:** high. **Status:** verified on Actions; not reproduced locally.
- **Impact:** Every push to `main` fails `.github/workflows/ci.yml` job
  `guard` while the Windows `build` job can stay green. The failure is
  the web LAN certificate test, not the game client.
- **Evidence:** Actions run
  [42511454234](https://github.com/DatMoshu/GodotUO/actions/runs/42511454234)
  on this SHA. The failed step is `python3 tools/web/test_run.py`. The
  traceback is `ssl.SSLCertVerificationError: certificate verify failed:
  Missing Authority Key Identifier` in `/usr/lib/python3.14/ssl.py`, at
  `HTTPSConnection` to `/GUO.html` (`tools/web/test_run.py` around the
  LAN request). The guard job never calls `actions/setup-python`; it
  uses the image `python3` (`.github/workflows/ci.yml` guard job).
- **Contract:** `tools/guo/lan.py` `ensure_certificates` builds the local
  CA with `BasicConstraints` and `KeyUsage` only (the builder at the
  `CertificateBuilder` chain). Phones install that CA from `/guo-ca.crt`.
- **Repro:** On a runner whose `python3` is 3.14, `python3 tools/web/test_run.py`.
  On this VM, Python 3.12.3 and cryptography 41.0.7, the same command
  exited 0 and printed `test_run: all ok`.
- **Remedy:** Add subject and authority key identifiers when the CA and
  the server certificate are built, and pin the guard job to Python 3.12
  so the job matches the rest of the project. Regenerating a CA changes
  the cert a phone already installed; the existing "CN mismatch rebuilds
  the CA" path is the place to force that once.
- **Regression:** `python3 tools/web/test_run.py` on Python 3.12 and on
  the runner's 3.14. No client art.
- **Hypothesis, separate:** a phone browser may already be stricter than
  Python 3.12. This review did not open a browser.

## F2. `smoke.sh` is a thinner health check than `smoke.bat`

- **Severity:** medium. **Status:** verified by reading both scripts. Not executed.
- **Impact:** `docs/wiki/Linux.md` says each `.sh` twin has the same
  behaviour as its `.bat`. A Linux or macOS pre-commit smoke skips three
  engine-free checks the Windows script runs, so a failure there waits
  for a Windows run or for CI's smaller C# list.
- **Evidence:**
  - `launchers/dev/smoke.bat` lines 51–55 run `tools/network_tests` and
    `tools/render_sort_tests` between art-capacity and diagnostics.
  - `launchers/dev/smoke.sh` lines 41–44 go from art-capacity straight to
    diagnostics.
  - `smoke.bat` lines 95–96 run `tools/server_lab/test_server_lab.py`.
    `smoke.sh` has no `server_lab` step.
  - `smoke.sh` lines 99–100 print the keyboard banner and then pass
    `--login-preflight-probe`. `smoke.bat` lines 114–117 give that probe
    its own "login preflight" banner, then run the keyboard probe.
- **Contract:** Linux wiki, "same behaviour". `launcher_lint` checks that
  the pair exists and that the first line is a description. It does not
  diff the bodies.
- **Remedy:** Add the three missing steps to `smoke.sh` and split the
  preflight banner. Add a small Python test that the two scripts name
  the same `dotnet run --project` paths and the same `server_lab` script.
- **Regression:** that comparison test, plus `dotnet run` of the two C#
  projects on a machine with the SDK. This VM cannot run them.
- **Not claimed:** the omitted tests are failing. They were not run.

## F3. ADR index is behind the files on disk

- **Severity:** low. **Status:** verified by listing `docs/architecture/`
  against the table in `docs/architecture/README.md`.
- **Impact:** The index is how agents pick the next ADR number and how
  the wiki tells readers to find later decisions. A missing row hides an
  Accepted decision (0045) and several Proposed ones that already have
  code and probes (0039, 0042, 0043).
- **Evidence:** The table jumps from `| 0038 |` to `| 0040 |`. Files
  present without a row include `ADR-0039-realtime-lights.md` (header:
  Proposed, 2026-10-19), `ADR-0042-ambient-particles.md` (Proposed),
  `ADR-0043-ordered-rendering-device-lab.md` (owner-authorized L4,
  off by default), `ADR-0045-wide-zoom-overview.md` (Accepted 2026-10-27).
  Unnumbered files `ADR-local-lighting-clock.md`, `ADR-uoplus-settings.md`,
  `ADR-uoplus-weather-lab.md` and `ADR-ux03-touch-inventory.md` are also
  outside the numbered table. The footnote already says 0045 exists and
  that 0050 is next. 0041 and 0046 have no file; 0046 is reserved in that
  footnote.
- **Remedy:** Add rows whose status words are copied from each header.
  Do not mark ADR-0007's original proposal Accepted. Its body still says
  Proposed, recommend reject; the Accepted block is the depth-buffer
  amendment.
- **Regression:** a test or a docs-lint extension is optional. A human
  diff of filenames against the table is enough for a docs pull request.

## F4. Tier counts in the plan and the wiki are the September table

- **Severity:** low. **Status:** verified as doc drift. The audit was not re-run.
- **Impact:** `docs/port_plan.md` and `docs/wiki/Architecture.md` still
  say verbatim 247 / shim 116 / rewrite 37, "from port_status.md,
  2026-09-23". Committed `docs/port_status.md` says generated
  2026-10-28T03:29:54+00:00 and verbatim 246 files (244 done), shim 114,
  rewrite 40. Someone planning from the wiki will quote the older table.
- **Contract:** `port_status.md` says it is generated and must not be
  hand-edited. CI's build job regenerates it and diffs ignoring the
  timestamp.
- **Remedy:** Replace the copied numbers with a pointer to
  `docs/port_status.md`. Re-run the audit only when `sources/ClassicUO`
  is available, and commit the result if it changed.
- **Not claimed:** the October file mismatches today's classifier. This
  review could not regenerate it.

## F5. Getting Started names the wrong second file for the `field` keyword

- **Severity:** low. **Status:** the doc sentence is verified; the
  compile failure on SDK 8 was not re-run.
- **Evidence:** `docs/wiki/Getting-Started.md` says an 8.0 SDK fails in
  `MessageManager.cs` and `SystemChatControl.cs`. `MessageManager.PromptData`
  uses `get => field` (`src/Game/Managers/MessageManager.cs`). A search
  for `field` in `src/Game/UI/Gumps/SystemChatControl.cs` returned no
  matches. `docs/wiki/Linux.md` states the SDK rule without naming
  `SystemChatControl.cs`.
- **Impact:** A person debugging an SDK 8 error will open the wrong
  second file. The SDK 10 requirement itself matches `LangVersion` latest
  plus the `field` use, and matches the Linux page.
- **Remedy:** Cite `MessageManager.cs` only, or name whatever second
  file a fresh SDK 8 build actually errors on.
- **Regression:** none beyond the doc lint. A compile on SDK 8 would
  close the hypothesis that another file still needs C# 14.

## F6. Bare Python config does not match the launcher contract it claims

- **Severity:** low. **Status:** verified by reading. No fixture executed.
- **Evidence:**
  - `tools/guo/config.py` says resolution "matches the launchers exactly"
    and lists environment, `config.local`, then `config.bat`. A search of
    that file found no `UO_COMMON_CONFIG`.
  - `launchers/_shared/common.sh` sources `UO_COMMON_CONFIG` before
    `config.sh` when the variable is set.
  - `load_config` uses `get("UO_CLIENT_VERSION", "7.0.15.1")`.
    `config.sh` and `config.bat` default `UO_CLIENT_VERSION` to
    `7.0.107.76`.
- **Impact:** A tool run from a shell that has not sourced `common.sh`
  can see a different client version, and it never sees the optional
  central config. Launcher runs export the variables first, so the
  usual `launchers/` path is unaffected.
- **Remedy:** One fallback string, plus an explicit decision on
  `UO_COMMON_CONFIG` for `load_config`. Extend `tools/guo/test_config.py`.
  This needs a human look because the docstring says the orders match.
- **Not claimed:** a current launcher bug. `launcher_lint` was expected
  to keep the `.bat` and `.sh` key sets aligned; this review did not
  treat a key mismatch as found.

## F7. `build.yml` does not ask for the SDK the docs require

- **Severity:** low. **Status:** file contradiction verified; failure not observed.
- **Evidence:** `build.yml` desktop and Android jobs request SDK `8.0.x`
  and `9.0.x` (`actions/setup-dotnet@v4`). `docs/wiki/Linux.md` says an
  8.0 or 9.0 SDK fails the build. Actions run
  [42498982602](https://github.com/DatMoshu/GodotUO/actions/runs/42498982602)
  printed `Build succeeded` for the desktop `dotnet build`. The same log
  shows runtime `10.0.14` already on the image and the action installing
  SDKs `8.0.427` and `9.0.320`.
- **Hypothesis:** the image's preinstalled SDK is what compiled
  `field`, and a cleaner image with only 8 and 9 would fail. This review
  did not capture `dotnet --version` from that step, so the hypothesis
  stays a hypothesis.
- **Remedy:** Request `10.0.x` in `build.yml` the way `ci.yml` and
  `release.yml` already do.
- **Why it is not F1:** `ci.yml`'s Windows build on the baseline SHA
  succeeded. The red job is the Ubuntu guard job.

## Confirmed, and not filed as new defects

**Pull request 179 is merged, not open.** The water shader uses
`distance_art / 4`, `CloudLighting.FromMinute` matches the daylight note,
and static sun stamps clip to `CoverZ`. `LightingSpeedMode` defaults to
false. `ReceiverContract.md` still says the GPU inverse mask is unwired.
No native rendered qualification was run then, and none was run here.
That is a remaining proof, listed as proposal P8, not a regression found
in the shader text.

**ADR-0048 is still Proposed** while the code is on `main`. That matches
the index line "opt-in; qualification ongoing". Off remains the classic
frame. Do not promote the ADR to Accepted from this review.

**`TradePartner`** documents a fixed dev-shard account as "not a
credential". The values were left in that source file and were not copied
into the fact database. It is a local probe helper, not the login screen.

**DFI content defaults on** for the dev shard (`UO_SHARD_DFI=1`), described
as server patches, not a client pack. Pull request 193 carried that onto
`main`. Older DFI pull requests 172–178 were closed without merging.
This review did not read the patch series.

**No tracked private keys or cloud tokens** turned up in the searches
noted in the coverage ledger. That is a negative search of limited
scope, not a security certification.
