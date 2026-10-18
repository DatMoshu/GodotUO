// GUO-owned (HD light Phase 3): the half-res height buffer.
// What it holds: per-pixel top height in Z units — tile Z from the land,
// Z + Height from tiledata statics, splat Z + victim height inside splat
// PickRects — encoded as (topZ + 128) / 255 in opaque gray. The hd_sun
// pass marches this buffer toward the sun: taller casters throw longer,
// softer shadows. Half res desktop is plenty (a tile is still ~22 px) and
// keeps wall edges on 2-px texels so the sun march's linear sampling +
// dithered penumbra reads as a smooth gradient instead of 4-px stairs;
// the mobile tier runs it at quarter.
//
// How it fills: flat opaque rects through one RenderingServer canvas item
// into its own small SubViewport (the id-buffer pattern, ADR-0023 section
// 3, but CPU-driven rects instead of mirrored sprites: no batcher change,
// no per-sprite cost, nothing in the world draw). Land stamps first as the
// base, then statics over them (taller wins by draw order), splats last.
// Nearest when stamping (crisp rects); the hd_sun sampler reads linear
// (bilinear) so texel edges interpolate. The buffer is never built unless
// an enabled pass declares height_tex, so Classic pays nothing.

using System;
using Godot;

namespace GUO.Renderer.Lights
{
    internal sealed class HdHeightBuffer : IDisposable
    {
        /// <summary>Full-res pixels per buffer pixel on the desktop (half res each axis).</summary>
        public const int DownscaleDesktop = 2;

        /// <summary>Full-res pixels per buffer pixel on the mobile tier (quarter res).</summary>
        public const int DownscaleMobile = 4;

        /// <summary>How many stamps a fill may lay down; land plus statics fit with room.</summary>
        public const int MaxStamps = 4096;

        private SubViewport _viewport;
        private ColorRect _base;
        private Node2D _host;
        private Rid _item;
        private int _width;
        private int _height;
        private int _scale;
        private int _stamps;
        private bool _disposed;

        public bool IsBuilt => _viewport != null && !_disposed && GodotObject.IsInstanceValid(_viewport);

        public Texture2D Texture => _viewport?.GetTexture();

        /// <summary>The downscale this build runs at (2 desktop, 4 mobile).</summary>
        public int Scale => _scale;

        /// <summary>Stamps laid by the last fill (the cost log).</summary>
        public int LastStamps => _stamps;

        /// <summary>Encode a top Z into the 0..1 gray the buffer stores.</summary>
        public static float Encode(int topZ) => Math.Clamp((topZ + 128) / 255f, 0f, 1f);

        /// <summary>Build (or rebuild) the buffer under <paramref name="host"/>.</summary>
        public void EnsureSize(Node host, int fullWidth, int fullHeight, bool mobile)
        {
            int scale = mobile ? DownscaleMobile : DownscaleDesktop;
            int w = Math.Max(1, (fullWidth + scale - 1) / scale);
            int h = Math.Max(1, (fullHeight + scale - 1) / scale);

            if (host == null || w <= 0 || h <= 0)
            {
                return;
            }

            if (_viewport != null)
            {
                if (_width == w && _height == h && _viewport.IsInsideTree())
                {
                    return;
                }

                Teardown();
            }

            _width = w;
            _height = h;
            _scale = scale;

            _viewport = new SubViewport
            {
                Size = new Vector2I(w, h),
                TransparentBg = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Disable3D = true,
                CanvasItemDefaultTextureFilter =
                    Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };

            _base = new ColorRect
            {
                Color = new Color(Encode(0), Encode(0), Encode(0)),
                Position = Vector2.Zero,
                Size = new Vector2(w, h),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };

            _host = new Node2D();

            _viewport.AddChild(_base);
            _viewport.AddChild(_host);
            host.AddChild(_viewport);

            _item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetDefaultTextureFilter(_item, RenderingServer.CanvasItemTextureFilter.Nearest);
            RenderingServer.CanvasItemSetParent(_item, _host.GetCanvasItem());
        }

        /// <summary>Start a fill: ground-gray base, empty stamp item.</summary>
        public void BeginFill()
        {
            if (!IsBuilt)
            {
                return;
            }

            _stamps = 0;

            if (_base != null)
            {
                float g = Encode(0);
                _base.Color = new Color(g, g, g);
                _base.Size = new Vector2(_viewport.Size.X, _viewport.Size.Y);
            }

            if (_item.IsValid)
            {
                RenderingServer.CanvasItemClear(_item);
            }
        }

        /// <summary>
        /// Lay one flat height rect in buffer pixels. Later stamps cover
        /// earlier ones, so callers draw base first, taller things after.
        /// </summary>
        public void Stamp(Rect2 bufferPx, int topZ)
        {
            if (!IsBuilt || !_item.IsValid || _stamps >= MaxStamps)
            {
                return;
            }

            if (bufferPx.Size.X <= 0 || bufferPx.Size.Y <= 0)
            {
                return;
            }

            float g = Encode(topZ);
            RenderingServer.CanvasItemAddRect(_item, bufferPx, new Color(g, g, g));
            _stamps++;
        }

        public void Teardown()
        {
            if (_item.IsValid)
            {
                try
                {
                    RenderingServer.FreeRid(_item);
                }
                catch
                {
                }

                _item = default;
            }

            _base = null;
            _host = null;

            if (_viewport != null)
            {
                try
                {
                    _viewport.QueueFree();
                }
                catch
                {
                }

                _viewport = null;
            }

            _width = 0;
            _height = 0;
            _scale = 0;
            _stamps = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Teardown();
        }
    }
}
