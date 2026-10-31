// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace GUO.Renderer
{
    /// <summary>
    /// ADR-0044 production selection. An admitted frame bakes statics into its chunk mesh
    /// under upstream's rules, as a depth buffer allows, and takes them out
    /// of the canvas sort, its covering land and its per-sprite submission.
    /// </summary>
    public static class DepthWorldProbe
    {
        // Production selection is explicit on Compatibility and when --canvas-world
        // is requested. Diagnostic probes retain their explicit backend controls.
        public static readonly bool Enabled =
            Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--canvas-world") < 0
            && Godot.RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility"
            && (Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--batcher-probe") < 0
                || Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--depth-world") >= 0);

        // Per-frame capability admission precedes the visibility walk. Resident
        // failures recover from the complete ordered canvas source (ADR-0044).
        internal static bool FrameAdmitted;
        internal static bool ResidentEnabled => Enabled && FrameAdmitted;

#if GUO_LABS
        public static readonly bool Ceiling = Environment.GetEnvironmentVariable("GUO_PERF_DW_CEILING") == "1";
        /// <summary>
        /// <c>--uncapped</c> (Labs only): vsync off, no frame cap, and the
        /// debug gump opened in its detailed view with the profiler on, so a
        /// play session shows what the frame really costs.
        /// </summary>
        public static readonly bool Uncapped = Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--uncapped") >= 0;
#else
        public const bool Ceiling = false;
        public const bool Uncapped = false;
#endif

        /// <summary>
        /// PORT DEVIATION (GUO): <c>--zoom-max=N</c> lets the game camera zoom
        /// out past upstream's 2.5, to N. The production Depth World path
        /// defaults it to 5; canvas keeps upstream's range unless explicitly set.
        /// </summary>
        public static readonly float ZoomMax = ReadZoomMax();

        private static float ReadZoomMax()
        {
            foreach (string arg in Godot.OS.GetCmdlineUserArgs())
                if (arg.StartsWith("--zoom-max=", StringComparison.Ordinal)
                    && float.TryParse(arg.AsSpan(11), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float max))
                    return max;
            return Enabled ? 5f : 0f;
        }

        public static bool Active => Ceiling || ResidentEnabled;
    }
}
