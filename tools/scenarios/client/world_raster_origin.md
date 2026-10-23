# client.renderer.world-raster-origin — independent CUO reference

L0 parity repair under ADR-0001. No option or dependency was added. Use the
existing GUO/CUO screenshot tools and an authorized private scratch profile;
keep profiles, credentials, client artwork and captures outside the checkout.

1. Match the actual world-target dimensions (including OS DPI), facet, player
   position/Z, camera, classic art, terrain shadows and light settings in both
   clients. Capture twice without moving at zoom 1, 1.5 and 2. Use exact RGB
   comparisons on water and open grass regions that are stable between both
   clients' repeated frames. Do not shift, rescale or tolerate differences.
2. Inspect long diagonal tile-edge lines and short atlas-edge dashes. Compare
   with upstream CUO, not only GUO canvas versus GUO RD. Record the regions,
   stable pixel counts, excluded actors, hashes and residual differences.
3. Repeat at night with classic lighting, custom light override disabled and
   the same server light level. Inspect light centers and edges as well as
   terrain. Restore the server light level after the run. Check selected
   static/mobile edges at multiple zooms; Camera's CPU inverse is unchanged.
4. Run the normal smoke. In an optimized Debug Labs build, run the batcher
   probe and four-scene `--rd-world --perf-parity` test against the corrected
   canvas path. Require actual RD draws as well as pixel equality, including
   rollback, resize and toggle-off paths.

Initial independent water/open-grass regions matched CUO exactly at all three
zooms after correction. Eleven stable residual pixels around small objects in
a larger zoom-2 grass region remain separately attributed; this is a seam fix,
not a full-client pixel parity certificate. Record subsequent executed and
unexecuted checks with the private run evidence.
