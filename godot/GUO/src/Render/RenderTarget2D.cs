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

        // Upstream clears a render target through the device, immediately and
        // once per frame. A SubViewport has no clear colour to set, so the
        // clear is a solid rect on a canvas item that sits behind everything
        // the batcher draws and is repainted with the viewport. Same result:
        // the target is fully redrawn every frame either way.
        private readonly Node2D _clearHost;
        private Color _clearColor = new Color(0, 0, 0, 0);

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

            _clearHost = new Node2D();
            _host = new Node2D();

            // Added first, so it paints first.
            _viewport.AddChild(_clearHost);
            _viewport.AddChild(_host);
            parent.AddChild(_viewport);
        }

        public int Width { get; }

        public int Height { get; }

        public bool IsDisposed { get; private set; }

        /// <summary>The canvas item a batcher parents its draws to.</summary>
        public Rid CanvasItem => _host.GetCanvasItem();

        public Texture2D Texture => _viewport.GetTexture();

        /// <summary>The node the viewport hangs under; the post-processing stack
        /// (ADR-0023) puts its own viewport beside it.</summary>
        public Node Parent => _viewport.GetParent();

        /// <summary>
        /// What the target shows where nothing has been drawn. A fully
        /// transparent colour means the viewport's own transparent background,
        /// which is what upstream's <c>Clear(Color.Transparent)</c> leaves.
        /// </summary>
        public Color ClearColor
        {
            get => _clearColor;
            set
            {
                if (_clearColor == value)
                {
                    return;
                }

                _clearColor = value;

                Rid item = _clearHost.GetCanvasItem();

                RenderingServer.CanvasItemClear(item);

                if (value.A > 0f)
                {
                    RenderingServer.CanvasItemAddRect(
                        item, new Rect2(0, 0, Width, Height), value);
                }
            }
        }

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
