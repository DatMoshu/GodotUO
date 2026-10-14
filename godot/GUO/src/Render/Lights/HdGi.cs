// GUO-owned (HD light Phase 4): the torch-GI governor state.
// The hd_gi PostFx pass solves coarse radiance cascades over the torch
// light buffer (emissive) + the sun-lit albedo + the height buffer, all in
// one stateless fragment pass (ADR-0023: no inter-frame feedback). This
// class only carries the per-frame march governor, like HdSun.Steps: the
// GameScene sets it from the zoom/mobile tier, the PostFx stack pushes it
// into the gi_steps uniform. Off-state (no enabled hd_gi pass) never reads
// it, so Classic pays nothing.

namespace GUO.Renderer.Lights
{
    internal static class HdGi
    {
        /// <summary>March taps this frame, set by the GameScene governor (desktop 10: all 3 rings, mobile/zoom 6: near+mid).</summary>
        public static int Steps = 10;

        /// <summary>Pure function of the tier, probe-safe: fewer rings off the desktop and at extreme zooms.</summary>
        public static int For(bool mobile, float zoom) =>
            mobile || zoom < 0.6f || zoom > 1.5f ? 6 : 10;
    }
}
