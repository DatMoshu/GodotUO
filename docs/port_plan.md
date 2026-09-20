# Porting ClassicUO to Godot — the plan

**Goal:** a complete Ultima Online Classic client running on Godot 4 .NET,
off FNA entirely, with behaviour matching the original.

**Approach:** transplant ClassicUO's C# rather than rewrite the game. The
network stack, file readers and game logic are kept; only the parts genuinely
bound to FNA are reimplemented.

Companion documents: `docs/port_status.md` (measured, regenerated),
`docs/data_formats.md` (the data contract).

---

## 1. Why a transplant, and not a rewrite

ClassicUO is roughly **155,000 lines across 429 C# files**. Rewriting that as
GDScript would mean reimplementing an MMO client by hand and permanently
losing the ability to take upstream bug fixes.

The measurement that decided the strategy: **FNA coupling is far shallower
than the file count suggests.**

| Upstream project | Files | Files touching XNA |
|---|---:|---:|
| `ClassicUO.Assets` | 23 | 0 |
| `ClassicUO.Bootstrap` | 4 | 0 |
| `ClassicUO.IO` | 13 | 2 |
| `ClassicUO.Utility` | 46 | 4 |
| `ClassicUO.Renderer` | 51 | 33 |
| `ClassicUO.Client` | 296 | 133 |

And of those 133 client files, **97 import only
`Microsoft.Xna.Framework`** — the maths and colour structs (`Point`,
`Color`, `Rectangle`, `Vector2/3`). Only 36 touch Graphics, Input or Audio.

Those structs have no engine behaviour behind them. Providing
API-compatible versions in a `UOPort.Compat` namespace turns a third of the
codebase from "rewrite" into "change one `using` line".

---

## 2. The three tiers

| Tier | Files | Lines | Share | Treatment |
|---|---:|---:|---:|---|
| `verbatim` | 243 | 74,522 | 48% | Renamespace `ClassicUO.*` → `UOPort.*` |
| `shim` | 102 | 50,513 | 33% | Renamespace + `using UOPort.Compat;` |
| `rewrite` | 88 | 31,170 | 20% | Reimplement on Godot |

**Four fifths of this port is mechanical.** That ratio is the reason the
project is tractable, and protecting it is the single most important
architectural constraint. The main way to lose it is by letting
`UOPort.Compat` grow engine behaviour: the moment it holds a texture or a
device, the boundary stops being reviewable and the rewrite tier starts
expanding.

Tiers are measured, not assumed — `launchers\pipeline\03_port_audit.bat`
recomputes them from the actual source.

---

## 3. `UOPort.Compat`

`godot/UOPort/src/Compat` provides XNA-compatible value types:

| Type | Approach |
|---|---|
| `Point` | Own struct — Godot's `Vector2I` differs in members |
| `Rectangle` | Own struct — half-open semantics, `Contains`/`Intersects` |
| `Color` | Own struct — byte channels; Godot's is normalised floats |
| `Vector2` / `Vector3` / `Vector4` | Aliased to Godot's own; API already matches |
| `Matrix` | **Not shimmed** — 36 uses, all inside rewrite-tier files |

Conversions to engine types are explicit (`ToGodot()`), never implicit, so
the port/engine boundary stays visible in a diff. Full rules in that folder's
README.

---

## 4. Order of work

Dependency order, not preference. Each phase is testable before the next
begins.

### Phase 0 — scaffold ✅ complete

Repo, pinned Godot 4.7.2 .NET, upstream reference, launchers, tooling,
`Compat` skeleton, audit baseline. `launchers\dev\smoke.bat` passes.

### Phase 1 — foundations
`Utility` → `IO`

Almost entirely verbatim. Ports the `.mul` / `.uop` readers, the memory-mapped
file access and the core collections. Keep the pointer arithmetic and the
memory mapping: they exist for throughput over hundreds of megabytes.

**Done when:** the client reads `tiledata.mul` and `hues.mul` and reports
correct counts.

### Phase 2 — asset loaders
`Assets`

Zero XNA imports upstream. The one real boundary is where a loader returns a
texture — that becomes Godot, and nothing above it changes.

**Done when:** art, gump and hue data decode, verified against known tile ids.

### Phase 3 — first pixels
`Render` (rewrite) + `Compat` hardening

The first genuinely hard phase. Rebuild sprite batching on `RenderingServer`
or `MultiMesh`, atlases as `ImageTexture`, and the hue system as a shader
sampling a LUT built from `hues.mul`.

Two constraints that are easy to get wrong and expensive to fix later:
- **Never filter pixel art.** The project pins
  `default_texture_filter=0`; any new viewport or material must preserve it.
- **Draw order is gameplay.** In an isometric world sort order decides what
  the player can see and click. Reproduce upstream's rules rather than
  substituting a generic depth sort.

**Done when:** a static scene of terrain and statics renders correctly,
evidenced by a screenshot.

### Phase 4 — the world
`Game/Data`, `Game/GameObjects`, `Game/Map`

Largely verbatim and shim. Terrain, statics, multis, mobiles.

**Done when:** a facet loads and displays, and the camera moves over it.

### Phase 5 — the shard
`Network`, `Configuration`

Engine-agnostic and ports cleanly, but unforgiving: the wire format is a
contract with real server software. Port packet definitions exactly —
lengths, endianness, fixed-width strings, seed and version negotiation,
compression, encryption variants.

**Done when:** login completes, the character list arrives, and the world
loads from a live shard.

### Phase 6 — interface
`Game/UI` (117 files, ~51k lines), `Input`

The largest single area. Mostly shim tier, but gump rendering depends on
Phase 3.

**Done when:** core gumps — paperdoll, backpack, status, skills — work.

### Phase 7 — parity and polish
`Audio`, lighting, effects, and the long tail.

**Done when:** a full play session is indistinguishable from the original
client.

---

## 5. Working rules

1. **Never edit `sources/`.** It is read-only reference.
2. **Port faithfully.** No reformatting, renaming, modernising or opportunistic
   bug fixing. Every gratuitous edit must be reconciled by hand on every
   future upstream merge. Note bugs; do not fix them mid-port.
3. **Build in small batches** (`launchers\dev\build.bat`). One file's error is
   cheap; fifty files' errors are not.
4. **Re-measure after each batch.** The audit is the only honest progress
   report.
5. **Parity before improvement.** Get it identical, then propose changes
   separately.
6. **"Ported" ≠ "working".** The audit matches filenames. Claims of working
   behaviour need a smoke test or a screenshot.

---

## 6. Upstream drift

ClassicUO is actively developed. `launchers\dev\sync_upstream.bat` updates the
reference and flags commits touching **already-ported** files — the ones that
rot silently. Assess, port across, then re-pin with `--pin`.

---

## 7. Licensing

- **ClassicUO** — BSD 2-Clause. Ported files keep their upstream copyright
  header; the licence and originating commit are recorded in
  `docs/upstream/`.
- **Claude Code Game Studios** — MIT; provenance in `docs/upstream/`.
- **UO client data** — proprietary, never redistributed. Users supply their
  own installation, read in place and never committed.
