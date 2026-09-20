# ADR-0001: Scene-Description Seam Between World State and Presentation

## Status

Accepted

## Date

2026-09-20

## Last Verified

2026-09-20 — decision checked against upstream at pin
`007ef8c3e13dec13fcc02387fe4817ef6f371c85`.

## Decision Makers

Project owner; `uo-port-strategist`; `uo-render-engineer`.

## Summary

The `rewrite` tier replaces ClassicUO's FNA renderer, and we must decide
whether world traversal, draw order and mouse picking get fused into the new
draw code. We keep them separate by preserving and hardening the seam
upstream **already has** — `RenderLists` plus the shared `CalculateDepthZ()`
depth function — and put exactly one presenter behind it (`ClassicPresenter`,
strict pixel parity), building no second presenter.

## Engine Compatibility

| Field | Value |
|-------|-------|
| **Engine** | Godot 4.7.2 stable, mono/.NET |
| **Domain** | Rendering |
| **Knowledge Risk** | **HIGH — post-cutoff, must verify** |
| **References Consulted** | `docs/engine-reference/godot/VERSION.md`; upstream `Game/Scenes/GameSceneDrawingSorting.cs`, `Game/Scenes/RenderLists.cs`, `Game/GameObjects/Views/MobileView.cs`, `ClassicUO.Renderer/PixelPicker.cs` |
| **Post-Cutoff APIs Used** | `RenderingServer` canvas-item batching; `MultiMesh` 2D instancing; shader palette lookup semantics — **all unverified against 4.7.2** |
| **Verification Required** | (1) `MultiMesh` 2D honours `default_texture_filter=0` (nearest) — not just `Sprite2D`; (2) 8-bit hue palette lookup in a fragment shader is bit-exact against client output; (3) instanced draw preserves strict per-instance ordering at UO item counts. |

> **Note**: Knowledge Risk is HIGH. Godot 4.7 post-dates the assistant's
> knowledge cutoff. Every API named here must be confirmed against the pinned
> build before it is relied on, and this ADR re-validated on any engine upgrade.

## ADR Dependencies

| Field | Value |
|-------|-------|
| **Depends On** | None |
| **Enables** | Hue/palette shader ADR; any future alternate-presenter ADR (not planned) |
| **Blocks** | Epic: Render Tier (`rewrite` tier, 88 files / 31,170 lines) |
| **Ordering Note** | Must be Accepted before `src/Render` work begins. Retrofitting a seam after the renderer exists is the expensive order. |

## Context

### Problem Statement

The `rewrite` tier — the FNA renderer, input and audio — is 88 files and
31,170 lines, 20% of the codebase. It is the only tier where we author new
code instead of moving existing code, so it is where architecture is actually
decided.

Two forces pull against each other:

1. **Strict pixel parity is the bar.** The audience is the classic freeshard
   community. They will compare screenshots. "Close enough" is a failed port.
2. **The owner wants a modern renderer to be *possible* later**, built by
   themselves or by others — but explicitly does **not** want it built now.

The naive reading of (2) is "build two renderers", which doubles the hardest
tier before the first one is correct. The naive reading of (1) is "port the
FNA render path straight across", which risks fusing *what is in the scene*
to *how it is drawn* — so that any later renderer must re-derive world
traversal, draw order and picking, and will get them subtly wrong.

### Current State

Nothing under `src/Render` exists yet; the port is at 0/429 files. Before
deciding, we read the upstream code rather than assuming its shape. Three
findings changed this ADR:

**1. Upstream already separates the renderer into its own assembly.**
`src/ClassicUO.Renderer` is a distinct project (`Batching/`, `Arts/`,
`Animations/`, `Gumps/`, `Lights/`, `shaders/`, `PixelPicker.cs`). The
client/renderer split we need is largely upstream's existing boundary, and it
maps cleanly onto our `rewrite` tier.

**2. Upstream already has an ordered render queue.** `RenderLists` is, in its
own words, *"an ordered queue of GameObjects to be rendered. The order is
determined by the draw order, not by the insertion order."* It buckets by
object class — `_tiles`, `_stretchedTiles`, `_statics`, `_animations`,
`_effects`, `_transparentObjects` — which is a batching decision, and holds
live `GameObject` references rather than snapshots.

**3. Draw order and picking already share one depth function.** Picking is not
a separate pass. Each object's view implements `CheckMouseSelection()` —
a bounds test against `FrameInfo`, then a `PixelPicker` alpha test — and
`GameSceneDrawingSorting` resolves the winner inline during the same
traversal that fills the render queue:

```csharp
if (allowSelection && ... && obj.CheckMouseSelection())
{
    if (SelectedObject.Object is GameObject prev)
    {
        if (obj.CalculateDepthZ() >= prev.CalculateDepthZ())
            SelectedObject.Object = obj;
    }
    else
        SelectedObject.Object = obj;
}
```

The topmost hit wins by `CalculateDepthZ()` — **the same function that
determines draw order**. This is the property worth protecting, and it
already holds. Our job is not to invent it; it is to avoid destroying it.

The one place upstream's seam genuinely leaks is the gump layer, which queues
`Func<UltimaBatcher2D, bool>` delegates — closures bound to the FNA batcher.
Upstream's own comment calls this *"an intermediate, crappy solution"*.

### Constraints

- Pixel parity with the classic client is non-negotiable for the shipped mode.
- The port must stay mergeable with live upstream ClassicUO (BSD 2-Clause).
- Only one presenter will be written, reviewed and tested by this project.
- Godot 4.7.2; rendering is the area of highest knowledge risk.

### Requirements

- Draw order and pick results derive from **one** depth computation.
- A second presenter can be added without touching traversal, sorting or
  picking.
- The seam costs no measurable frame time versus direct draw calls.
- The seam does not become a plugin framework or an abstraction layer over
  Godot.

## Decision

**Preserve upstream's structure and harden its one leak.** Concretely:

1. **Port `RenderLists` and the sorting traversal into `src/Render/Scene` as
   `shim`-grade code**, keeping its bucketed shape, its ordering semantics and
   its `GameObject` references. It is not rewritten; it is renamespaced. This
   keeps upstream sorting fixes mergeable, which matters more than any
   cosmetic improvement we could make.

2. **Keep `CalculateDepthZ()` as the single source of depth**, used by both
   the render queue and selection. Any presenter reads this; no presenter
   recomputes it. This is the seam's actual contract.

3. **Replace the gump delegate path with a typed command struct.** The
   `Func<UltimaBatcher2D, bool>` closures are the only genuine FNA coupling in
   the queue, and the only part we redesign. Typed commands are also
   allocation-free, which is the improvement upstream wanted anyway.

4. **Define `IScenePresenter` and implement it exactly once**, as
   `ClassicPresenter`. The interface exists to keep the queue honest — to make
   "did rendering code leak into traversal?" a mechanical check — not because
   a second implementation is planned.

We ship one presenter. We do not stub a second.

### Architecture

```
  Game world  (GameObjects; ported verbatim/shim from ClassicUO)
            |
            v
  +----------------------------------------+
  |  SceneSorter        (shim-tier port of  |
  |                      GameSceneDrawing-  |
  |                      Sorting)           |
  |   - walk tiles / entities               |
  |   - CalculateDepthZ()  <-- ONE depth fn |
  |   - CheckMouseSelection() inline        |
  |   - fill RenderLists buckets            |
  +----------------------------------------+
            |                        |
            |  RenderLists           |  SelectedObject
            |  (ordered buckets of   |  (topmost by
            |   GameObject refs +    |   CalculateDepthZ)
            |   typed gump commands) |
            v                        v
  +--------------------+       hovered entity
  |  ClassicPresenter  |
  |  (THE renderer,    |
  |   pixel parity)    |
  +--------------------+
            |
            v
      Godot canvas

  [ a future alternate presenter attaches at IScenePresenter.
    Not built. Not stubbed. ]
```

### Key Interfaces

```csharp
namespace GUO.Render;

/// Implemented exactly once, by ClassicPresenter.
/// The presenter consumes the queue; it never fills it and never sorts it.
public interface IScenePresenter
{
    void BeginFrame(in ViewportInfo viewport);

    /// Buckets are drawn in upstream's fixed pass order. The presenter must
    /// not reorder within a bucket: the order IS the draw order.
    void Present(RenderLists lists);

    void EndFrame();
}

/// Replaces upstream's Func<UltimaBatcher2D, bool> gump closures.
/// Value type: the gump layer is refilled every frame.
public readonly struct GumpCommand
{
    public readonly GumpCommandKind Kind;   // Sprite | Text | Rect | ClipPush | ClipPop
    public readonly int   X, Y;
    public readonly float LayerDepth;
    public readonly float Alpha;
    public readonly ushort Hue;             // UO hue index, NOT a resolved colour
    public readonly ushort GraphicId;       // sprite/gump id
    public readonly RenderedText Text;      // Text kind only; null otherwise
}
```

### Implementation Guidelines

- **`SceneSorter` must not reference `Godot.Texture2D`, `CanvasItem`, or any
  Godot rendering type.** This single rule is what makes the seam real. A
  Godot render type appearing in the sorter is a build-breaking review
  finding, not a style nit.
- **Never recompute depth.** If presenter code calls anything other than
  `CalculateDepthZ()` to decide order, picking and rendering will disagree and
  the player will click the wrong object. This is the bug this ADR exists to
  prevent.
- Sort tie-breakers are ported **verbatim**, with the upstream line referenced
  in a comment. These rules look arbitrary and are not.
- Hue travels as the **UO hue index**, not a resolved colour. Palette lookup
  belongs in the shader: one texture fetch instead of a CPU conversion per item.
- Keep `GameObject` references. Do not convert the queue to value-type
  snapshots — it would cost a copy of every visible object per frame and
  break mergeability with upstream for no benefit we need.
- Do **not** add presenter-selection config, a presenter registry, or a
  `ModernPresenter` stub. The interface is the extension point; a stub is dead
  code that drifts.

## Alternatives Considered

### Alternative 1: Fuse traversal and drawing in the new renderer

- **Description**: Collapse `RenderLists` away and issue Godot draw calls
  directly from the sorting traversal.
- **Pros**: Fewest moving parts; slightly less indirection.
- **Cons**: Discards a separation upstream already maintains, so it is *more*
  work than keeping it, and it makes every future upstream sorting fix a
  manual reconciliation.
- **Estimated Effort**: ~1.05× the chosen approach — it is not even cheaper.
- **Rejection Reason**: Strictly worse. The seam is free because it already
  exists; removing it costs effort, mergeability and optionality at once.

### Alternative 2: Build both classic and modern presenters now

- **Description**: Implement the seam plus two presenters, toggleable at
  runtime, in the style of the *Monkey Island* remake.
- **Pros**: Proves the seam is genuinely renderer-agnostic rather than
  classic-shaped with an interface bolted on.
- **Cons**: Doubles the hardest tier before the first presenter is correct. A
  second presenter needs its own parity story, art direction and bug budget,
  maintained by nobody.
- **Estimated Effort**: ~2.1× the chosen approach.
- **Rejection Reason**: Explicitly declined by the project owner: *"Let's just
  do classic forget modern, I or others can make their modern tweaks later.
  Just make the framework there for it."*

### Alternative 3: Full abstraction layer over Godot rendering

- **Description**: A general `IRenderBackend` wrapping textures, shaders and
  draw calls, with Godot as one implementation.
- **Pros**: Maximum theoretical portability.
- **Cons**: Re-creates FNA — precisely the dependency this port exists to
  remove. Every Godot feature becomes reachable only through a wrapper.
- **Estimated Effort**: ~3× the chosen approach.
- **Rejection Reason**: Contradicts the purpose of the port.

### Alternative 4: Value-type snapshot buffer instead of GameObject references

- **Description**: Flatten the queue into a `SceneItem[]` of immutable structs
  so the presenter provably cannot mutate world state.
- **Pros**: Stronger isolation; trivially testable; presenter could run off
  the main thread.
- **Cons**: Copies every visible object every frame (thousands of items), and
  diverges structurally from upstream, making sorting fixes harder to merge.
- **Estimated Effort**: ~1.3× the chosen approach.
- **Rejection Reason**: Buys isolation we can get by convention and review, at
  the price of the mergeability this port depends on. Reconsider only if a
  threaded presenter is ever actually wanted.

## Consequences

### Positive

- Draw order and picking cannot disagree: one `CalculateDepthZ()`, already
  shared upstream.
- Upstream sorting fixes stay mergeable, because the queue keeps its shape.
- Sorting is testable headlessly — the sorter has no rendering dependency —
  and sorting is the part most likely to be subtly wrong.
- The gump layer gets allocation-free typed commands, an improvement upstream
  explicitly wanted.
- A future presenter is an additive change at a defined boundary.

### Negative

- `IScenePresenter` has exactly one implementation, which reads as
  over-engineering to anyone who has not read this ADR. Link it from the code.
- The gump command redesign is the one place we knowingly diverge from
  upstream, so gump-layer fixes will need manual reconciliation.
- Keeping `GameObject` references means the presenter *could* mutate world
  state. Prevented by review, not by the type system.

### Neutral

- `src/Render` file layout diverges from upstream's. It is `rewrite` tier and
  was never going to merge.

## Risks

| Risk | Probability | Impact | Mitigation |
|------|------------|--------|-----------|
| Sort tie-breakers ported incorrectly | **High** | **High** | Port verbatim; golden-image tests per object class. The single most likely source of "looks almost right" bugs. |
| A presenter recomputes depth and diverges from picking | Medium | **High** | One `CalculateDepthZ()`; grep check in Validation; called out in guidelines. |
| Gump command redesign loses a behaviour the closures encoded | Medium | Medium | Enumerate every existing `AddGumpWithAtlas` / `AddGumpNoAtlas` call site before designing the command set. |
| Seam is classic-shaped, so a second presenter does not fit | Medium | Low | Keep the queue in UO domain terms (hue index, graphic id). Residual risk accepted: not fully testable without the second presenter we are deliberately not building. |
| Godot 4.7 rendering APIs differ from assumption | Medium | Medium | Verify against the pinned build; Knowledge Risk is HIGH. |

## Performance Implications

Budgets, not measurements — nothing is built yet. The first implementation
must replace these with real numbers.

| Metric | Before | Expected After | Budget |
|--------|--------|---------------|--------|
| CPU (frame time) | n/a (no renderer) | ~0ms seam overhead (queue already exists upstream) | < 0.5ms of a 16.6ms frame |
| Memory | n/a | bucket lists of references, reused per frame | < 1MB steady state |
| Load Time | n/a | unchanged | no regression |

## Migration Plan

Greenfield, not a migration. Build order:

1. Port `RenderLists` + `SceneSorter` (shim tier). Headless test: ordering
   matches upstream for a fixed world sample, one case per tie-breaker class.
   **No drawing yet.**
2. `ClassicPresenter`: land tiles only. First pixels. Compare to client.
3. Statics, then mobiles, then multis, then effects — golden image per step.
4. Port `PixelPicker` and `CheckMouseSelection`; verify the hovered object
   matches the client for fixed cursor positions.
5. Gump layer last, with typed commands, once the call sites are enumerated.

**Rollback plan**: If the seam ever costs frame time we cannot recover,
collapse `ClassicPresenter` into `SceneSorter` and delete the interface —
Alternative 1, reachable at any point as a merge rather than a rewrite.

## Validation Criteria

- [ ] Headless: sorter output order matches upstream for a fixed world
      sample, including one case per tie-breaker class.
- [ ] Golden-image: land, statics, mobiles, multis, effects each match a
      client reference capture at identical camera position — **pixel exact**.
- [ ] Picking: for 100 sampled cursor positions, the resolved object matches
      the classic client's.
- [ ] `grep -rn "Godot\." src/Render/Scene/` returns nothing.
- [ ] Exactly one definition of depth: `grep -rn "CalculateDepthZ" src/` shows
      one implementation, many call sites.
- [ ] Seam overhead < 0.5ms at 8,000 visible items.

Reference captures come from the classic client and from the external project's `guoasset-mcp`
(`guoasset.get_image`), which renders art, gumps and multis directly from the
same client data — see `docs/external-reference.md`.

## GDD Requirements Addressed

Foundational — no GDD requirement. This project is a port, not an original
design; the specification is the behaviour of the existing classic client.

Enables: all `rewrite`-tier rendering work, mouse interaction with the world,
and any future alternate presentation mode.

## Related

- `docs/port_plan.md` — Phase 3 (Render) implements this ADR
- `docs/external-reference.md` — reference captures and asset tooling
- `docs/engine-reference/godot/VERSION.md` — knowledge risk for Godot 4.7.2
- Upstream (read-only reference, pin `007ef8c3`):
  - `src/ClassicUO.Client/Game/Scenes/GameSceneDrawingSorting.cs`
  - `src/ClassicUO.Client/Game/Scenes/RenderLists.cs`
  - `src/ClassicUO.Renderer/PixelPicker.cs`
