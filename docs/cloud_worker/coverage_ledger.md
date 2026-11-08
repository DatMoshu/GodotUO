# Coverage ledger

Baseline `0aec6eb8ccc53a01655fbb8872211ce2358c9992`, observed
2026-11-08T19:38:21Z. A broad review is not an exhaustive one. "Inspected"
means the file or log was opened and a claim in the findings or the fact
database cites it. "Sampled" means the area was entered and not read
through. "Not reviewed" means this pass did not open it.

Confidence: **high** if a cited line or a command output supports the
sentence; **medium** if the sentence summarises several files that were
sampled; **low** if it depends on a log line that did not include the
decisive detail.

## Inspected

| Area | Paths | Confidence | Note |
|---|---|---|---|
| Baseline and pull requests | `git`, `gh pr view 179`, `gh pr list` | high | 179 merged 31 Oct into `integration`. No open PRs. 193 is the tip |
| CI on this SHA | `.github/workflows/ci.yml`, Actions run 42511454234 | high | Guard red, Windows build green |
| Separate build workflow | `.github/workflows/build.yml`, run 42498982602 | medium | Desktop build succeeded. Compiler identity not printed |
| Project file and pin | `godot/GUO/GUO.csproj`, `docs/upstream/UPSTREAM_PIN.json`, `.gitignore` | high | SDK 4.7.2, net8.0, LangVersion latest, pin and ignore rules |
| Speed-mode contracts | `Profile.cs` LightingSpeedMode, `SpeedModeDebug.cs`, `speed_reflection.gdshaderinc`, `CloudLighting.cs`, `GameScene.SpeedStaticShadows.cs`, `ReceiverContract.md`, ADR-0048 header and 31 Oct note | high for those lines | No GPU run |
| Docs drift | `docs/port_plan.md`, `docs/port_status.md` header, `docs/wiki/Architecture.md`, `docs/wiki/Getting-Started.md`, `docs/wiki/Linux.md`, `docs/architecture/README.md` | high | Audit not regenerated |
| Smoke twins | `launchers/dev/smoke.bat`, `launchers/dev/smoke.sh` | high | Read, not executed |
| Config | `tools/guo/config.py` header and `load_config` fallback, `launchers/_shared/config.sh`, `common.sh` UO_COMMON_CONFIG | high for those lines | `test_config.py` not re-run before the doc pass; run in the validation log if present |
| LAN certificates | `tools/guo/lan.py` `ensure_certificates`, `tools/web/test_run.py` LAN request | high | Local 3.12 pass; 3.14 failure from the Actions log |
| Privacy rule | `AGENTS.md` rule 8, `TradePartner.cs` comment, `config.sh` DFI default | high | Secret-value searches were narrow; see below |
| Network entry | `src/Network/NetClient.cs` opening | medium | Class owns socket and Huffman. Handlers not read |
| Language census | `find` counts under `godot/GUO` and `tools/` | high | 1210 C#, 6 GDScript, 59 shader files, 359 Python files |

## Sampled, not closed

| Area | What was seen | What remains |
|---|---|---|
| ADR bodies | Headers for 0007, 0039, 0042, 0043, 0045, 0048. Index table | Most ADR bodies, validation sections, and whether code matches every decision |
| Wiki | Architecture, Getting Started, Linux, the end of Working with AI agents | The rest of `docs/wiki/` (about 40 pages) |
| Workflows | `ci.yml` and `build.yml` in full enough to name jobs. `release.yml` SDK line via search notes | Release, pages and wiki workflow steps |
| Security searches | No `BEGIN PRIVATE KEY` / `AKIA` style hits reported by a scoped search; loopback defaults in `config.bat` | Packet parser unsafe blocks, plugin ABI, export hardening |
| Speed-mode consumers | Cloud and static-sun call sites cited | Mobile sun stamps, wet-shader include graph, window shafts, physical-world probes |
| DFI | Config default and PR 193 merge | Patch contents under `tools/modernuo/patches/` |

## Not reviewed

- `sources/ClassicUO` (not in the checkout; read-only even when present).
- `PacketHandlers.cs` and the encryption implementations, line by line.
- The full `Profile.cs` field list and profile migration code.
- Editor panels, world authoring, mapgen, localization catalogues.
- Android, Steam Deck and web export execution.
- Asset Store publisher behaviour beyond the CI description.
- Runtime rendering, audio, input, and anything that needs a shard.
- Git history before the recent pull-request list, except PR 179 and the
  merges that explain why 179's code is on `main` (PR 190 promoted
  `integration` on 2026-11-05).

## Tests this ledger may cite

Only the commands in [the runbook](build_test_runbook.md). A fact of kind
`test-result` is one of those commands. A fact of kind `proposal` is not
a test and not a description of current behaviour. A fact of kind
`historical` is a git or GitHub observation with a time limit.

## Source inventory for a later plan

The durable list of citations is `docs/facts/facts.jsonl`. Each row has an
id, a kind, a source path or a URL, and the limits of the claim. Query
examples are in `tools/project_facts/README.md`. The proposals to turn
into work are `prop.*` rows and [work proposals](work_proposals.md).
