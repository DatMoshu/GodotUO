# Wide-zoom / whole-facet overview

Owner-authorized at15 transition and entire-facet zoom, ADR-0045. Explicit
--zoom-max>=15 enables dynamic fit range; defaults and saved2.5 clamp unchanged.

Inject wide_zoom_overview.cs.fixture as Bootstrap/OverviewFixture.cs and add
its Node to Main in an external stage. Set GUO_OVERVIEW_FIXTURE_DIR, then run
--play --zoom-max=20 --window-size1280x720 without credentials. Repeat on
gl_compatibility. GUO_OVERVIEW_FULLSCREEN=1 checks only full-facet fit at a
resolution appropriate to the owner's fullscreen issue. No network or benchmark.

Checks include exact15 threshold, unchanged ordinary wheel and accelerated
overview wheel, all facet corners inside viewport across player positions,
multiresolution cache/source-read budgets, no visible-tile truncation, warm
return, actual offline world/full-map/native transitions and invalidation.
result.json records correctness counters, not FPS. Remove injection before
full smoke. Expected line: [overview] PASS ... checks. Inspect whole-facet and
returned-native screenshots. Cold map regions may fill progressively.
