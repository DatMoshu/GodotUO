// SPDX-License-Identifier: BSD-2-Clause

using System;
using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// An off-screen surface the client draws into and then draws from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): XNA's <c>RenderTarget2D</c> is a texture the
    /// device can be pointed at. Godot has no such thing on the 2D canvas —
    /// the equivalent is a <see cref="SubViewport"/>, which renders its own
    /// subtree into its own texture. So this owns a viewport and a
    /// <see cref="Node2D"/> inside it, and exposes that node's canvas item for
    /// <see cref="UltimaBatcher2D"/> to parent its draws to.
    /// </para>
    /// <para>
    /// The implicit conversion to <see cref="Texture2D"/> is what lets the
    /// ported call sites read exactly as upstream writes them —
    /// <c>batcher.Draw(WorldRenderTarget, rect, hue, 0f)</c> is upstream's own
    /// line, unchanged.
    /// </para>
    /// <para>
    /// Project rule 7: the viewport is set to nearest sampling explicitly.
    /// A viewport created from code does not pick up the project's
    /// <c>default_texture_filter</c>, the same way a <c>RenderingServer</c>
    /// canvas item does not — measured, see UltimaBatcher2D.NewItem.
    /// </para>
    /// </remarks>
    public sealed class RenderTarget2D : IDisposable
    {
        private readonly SubViewport _viewport;
        private readonly Node2D _host;

        public RenderTarget2D(Node parent, int width, int height)
        {
            Width = width;
            Height = height;

            _viewport = new SubViewport
            {
                Size = new Vector2I(width, height),

                // The three targets are composited on top of each other, so
                // everything but the bottom one has to let what is underneath
                // show through.
                TransparentBg = true,

                // A UO frame is drawn every frame, and nothing here is 3D.
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Disable3D = true,

                CanvasItemDefaultTextureFilter =
                    Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };

            _host = new Node2D();

            _viewport.AddChild(_host);
            parent.AddChild(_viewport);
        }

        public int Width { get; }

        public int Height { get; }

        public bool IsDisposed { get; private set; }

        /// <summary>The canvas item a batcher parents its draws to.</summary>
        public Rid CanvasItem => _host.GetCanvasItem();

        public Texture2D Texture => _viewport.GetTexture();

        public static implicit operator Texture2D(RenderTarget2D target)
            => target?.Texture;

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;

            _viewport.QueueFree();
        }
    }
}
