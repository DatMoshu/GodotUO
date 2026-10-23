# Atlas-owned page metadata at the batcher boundary

Layer: L0 backend optimization; no new client option or rendering semantics.
The owner authorized priorities 1–3 with bank/docks comparisons. This first
change extends the earlier land-page dimension cache to `UltimaBatcher2D.Measure`.
`TextureAtlas` registers dimensions and the RenderingServer RID at page creation,
including dedicated oversized textures, and removes metadata before disposal.
Content uploads retain the metadata. Unregistered, potentially resizable textures
keep the existing fresh-query path. Nearest sampling, geometry and shaders are
unchanged. The GPU experiment remains Debug/Labs-only and off by default.

## Validation

The optimized Labs build passed 650 batcher checks; the actual Windows Release
export passed all 320 applicable checks. Twelve new checks exercise rectangular
pages, dedicated dimensions, unchanged uploads, A/B/A texture changes with zero
native measurements, rendered UV/color parity, standalone resize and disposal.
Full smoke passed. The diagnostic is guarded out of the shared CUO compilation;
a separate out-of-tree CUO Release build verified that guard without replacing
the benchmark control. This final preprocessor guard has no effect on GUO code.

Frozen before/after world runs each sampled 360 active GPU frames per scene, with
zero fallback. All 16 same-run RD/canvas image comparisons were byte-exact over
3,686,400 pixels each. Direct cross-build canvas comparison was exact at docks
and dungeon. Bank differed at 2,599 pixels, all in town-guard system-message
lines; forest differed at 3,695 pixels, all in server world-save message lines.
Both pairs were inspected. No world-art differences were found. Raw differences
remain recorded; this is not a claim of identical cross-run HUD messages or new
whole-client CUO pixel acceptance.

## Separate Release observations

Twelve sequential observations alternate the prior `333e3e43` Release export,
this candidate and the preserved optimized CUO control. Two observations per
client per scene use reversed order, 2560x1440, zoom 2 and cap 250, with classic
lighting and post-processing off. Both GUO exports use optimized Release Godot
bindings and the Release engine. No prototype result enters this table.

| Scene | Prior GUO frame ms | Candidate GUO frame ms | CUO frame ms |
|---|---:|---:|---:|
| Britain bank | 8.300–13.008 | 7.729–11.731 | 5.029–5.052 |
| Britain docks | 5.015–6.347 | 5.543–11.241 | 4.374–5.173 |

Frame time is the existing profiler's wall-frame interval, including its pacing,
averaged over its 60-entry ring. It is separate from the elapsed CPU scopes below.

| Scene | Prior GUO CPU ms | Candidate GUO CPU ms | CUO CPU ms |
|---|---:|---:|---:|
| Britain bank | 6.110–10.003 | 5.657–8.840 | 4.783–4.819 |
| Britain docks | 3.588–4.710 | 4.006–8.621 | 3.997–4.925 |

CPU means `RENDER_FRAME + UPDATE_WORLD`, not FPS or GPU execution time. Every run
is **under shared load**; other projects were unrestricted. Host CPU maxima range
from 45.8% to 83.6%. World-preparation time, untouched by this patch, varies
substantially. These mixed observations do not establish a causal FPS improvement,
and the docks observations must not be discarded. The candidate reports five
remaining native texture measurements in each final sampled frame; synthetic
atlas-only draws prove zero native queries. The old build lacks that counter,
so no inferred before-count is presented.

The separate Labs world pair uses optimized GUO C# with Debug Godot bindings.
Its host CPU peaks were 42.2% and 51.2%; prestage GPU peaks were 29% and 34%.
It is visual/diagnostic evidence, not a fair Release/CUO throughput comparison.

## Reproduction and evidence

Private labels: `release4-metadata-paired-01-control` through
`release4-metadata-paired-12-cuo`, and
`rd12-atlas-shared-valid-world-before-1` / `world-after-1`.
The frozen Labs control is `7089290d`; DLLs and unchanged shader inputs were
verified before and after each world run. Owned clients were cleaned up.
One earlier world attempt failed on sandbox output permissions before launch;
its failure receipt is retained separately. Private numeric results, captures,
source patches, identities and load receipts remain outside the checkout.
