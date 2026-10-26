// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace GUO.Renderer
{
    /// <summary>
    /// ADR-0044 switches. Either one bakes statics into their chunk mesh
    /// under upstream's rules, as a depth buffer allows, and takes them out
    /// of the canvas sort, its covering land and its per-sprite submission.
    /// </summary>
    public static class DepthWorldProbe
    {
        /// <summary>
        /// Spike step 0, a diagnostic ceiling, off unless
        /// <c>GUO_PERF_DW_CEILING=1</c>: nothing draws those statics, so the
        /// frame it times is what remains once their canvas work is gone.
        /// Never a rendering mode; the picture is knowingly incomplete.
        /// </summary>
        public static readonly bool Ceiling = Environment.GetEnvironmentVariable("GUO_PERF_DW_CEILING") == "1";

#if GUO_LABS
        /// <summary>
        /// <c>--depth-world</c> (L4, Labs Debug builds only): the world drawn
        /// by the RenderingDevice pass with a depth buffer, its chunk land and
        /// statics resident on the GPU (src/Labs/GpuRenderer, DepthWorldResident).
        /// </summary>
        public static readonly bool Enabled = Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--depth-world") >= 0;

        /// <summary>
        /// <c>--uncapped</c> (Labs only): vsync off, no frame cap, and the
        /// debug gump opened in its detailed view with the profiler on, so a
        /// play session shows what the frame really costs.
        /// </summary>
        public static readonly bool Uncapped = Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--uncapped") >= 0;
#else
        public const bool Enabled = false;
        public const bool Uncapped = false;
#endif

        public static bool Active => Ceiling || Enabled;
    }
}
