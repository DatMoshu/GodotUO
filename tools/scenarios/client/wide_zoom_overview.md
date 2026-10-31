# Experimental zoom and cached overview

Inject wide_zoom_overview.cs.fixture as Bootstrap/OverviewFixture.cs into an
external source stage and add its Node to Main. Set GUO_OVERVIEW_FIXTURE_DIR.
Run --play --window-size 1280x720 without credentials, on forward_plus and
gl_compatibility. The fixture explicitly enables the profile toggle; no
--zoom-max flag is required. It disables only its own controller setup offer.

Checks: new-profile default off, exact native 5x/overview above 5x boundary,
wheel steps, whole-facet geometry, bounded cache/source reads/uploads, warm
return, map invalidation, hidden selection, no distant detailed chunks, and
toggle-off at full-map zoom restoring standard 2.5 maximum. Capture each of
the four styles and inspect the prominent player pin and floating city label.
Fixture city names are synthetic. Counts include per-frame budget checks;
they vary with loading and are correctness assertions, not benchmarks.

Expected smoke line: [overview] PASS ... checks; 5x gate, whole facet, LOD,
read budget, warm return, native return. Remove injected sources and Main hook
before full smoke and export. Cold terrain fills progressively.
