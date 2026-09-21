// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// How a texture is sampled when it is not being drawn at its native size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): upstream uses XNA's <c>SamplerState</c>, a
    /// device-owned object with filter, addressing and mip settings. Only two
    /// of its presets are ever named in ClassicUO, and only the filter part of
    /// them matters here, so this is those two and the Godot filter each maps
    /// to. The name and the call shape are upstream's.
    /// </para>
    /// <para>
    /// Project rule 7 is why this is a short list rather than a general
    /// setting: <see cref="PointClamp"/> is the default everywhere, and the
    /// single place that asks for anything else is compositing a render target
    /// at a fractional DPI scale, where the target is not pixel art any more
    /// but a whole already-rendered frame.
    /// </para>
    /// </remarks>
    public sealed class SamplerState
    {
        private SamplerState(RenderingServer.CanvasItemTextureFilter filter)
        {
            Filter = filter;
        }

        internal RenderingServer.CanvasItemTextureFilter Filter { get; }

        /// <summary>Nearest neighbour. The default, and what pixel art needs.</summary>
        public static readonly SamplerState PointClamp =
            new SamplerState(RenderingServer.CanvasItemTextureFilter.Nearest);

        /// <summary>Bilinear.</summary>
        public static readonly SamplerState LinearClamp =
            new SamplerState(RenderingServer.CanvasItemTextureFilter.Linear);
    }
}
