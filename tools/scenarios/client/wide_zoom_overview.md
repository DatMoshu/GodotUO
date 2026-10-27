# Wide-zoom overview

Owner-approved explicit extended zoom; ADR-0045. No new toggle, profile field,
external dependency or doctor. Ordinary maximum/default is unchanged.

Inject wide_zoom_overview.cs.fixture as Bootstrap/OverviewFixture.cs and add
its Node to Main in an external source stage only. Set GUO_OVERVIEW_FIXTURE_DIR
to a private evidence folder, launch --play --zoom-max=20 --window-size 1280x720
with no credentials. The fixture creates an offline player from local map
data, checks bounded cache/preparation and transition behavior, and saves
screenshots. Repeat with gl_compatibility. No network login or timing sweep.
Remove both the injected source and Main hook before required full smoke.

Expected smoke line: [overview] PASS ... checks. Output result.json records
checks, rich chunk counts, tile counts and pixel storage, not FPS. Inspect
5.1/20 and returned 2.5 screenshots for map coverage and restored native art.
