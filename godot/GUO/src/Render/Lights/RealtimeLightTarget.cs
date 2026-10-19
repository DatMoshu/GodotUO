// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): draws the light field into a light target of its own.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The Realtime lights target: a <see cref="RenderTarget2D"/> the size of
    /// the classic light target, filled each frame with the land as lit iso
    /// quads (one colour per corner) and the tall statics as their own
    /// sprites in their tile's colour. <c>RenderTargets.Draw</c> blends its
    /// texture where it blends the classic light target.
    /// </summary>
    internal sealed class RealtimeLightTarget : IDisposable
    {
        private const string ShaderCode = @"shader_type canvas_item;
render_mode blend_disabled;
// ADR-0039: the vertex colour is the light; the texture only masks it.
// Fragment COLOR here is the vertex colour alone (the default multiply by
// the texture happens only in the built-in fragment).
void fragment() {
    float a = texture(TEXTURE, UV).a;
    if (a < 0.5) { discard; }
    COLOR = vec4(COLOR.rgb, 1.0);
}";

        private RenderTarget2D _target;
        private Rid _item;
        private ShaderMaterial _material;
        private readonly List<Vector2> _points = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _indices = new();
        private readonly List<(Rect2 rect, Texture2D texture, Rect2 source, Color bottom, Color top)> _sprites = new();
        private readonly List<Vector2> _spritePoints = new();
        private readonly List<Vector2> _spriteUvs = new();
        private readonly List<Color> _spriteColors = new();
        private readonly List<int> _spriteIndices = new();
        private bool _disposed;

        public bool IsBuilt => _target != null && !_target.IsDisposed && !_disposed;
        public Texture2D Texture => _target?.Texture;
        public int Width => _target?.Width ?? 0;
        public int Height => _target?.Height ?? 0;

        /// <summary>Land quads and sprites sent by the last <see cref="End"/> (the cost log).</summary>
        public int LastQuads { get; private set; }
        public int LastSprites { get; private set; }

        public void EnsureSize(Node parent, int width, int height)
        {
            if (parent == null || width <= 0 || height <= 0) return;
            if (_target != null && !_target.IsDisposed && _target.Width == width && _target.Height == height) return;

            Teardown();
            _target = new RenderTarget2D(parent, width, height);
            _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
            _item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_item, _target.CanvasItem);
            RenderingServer.CanvasItemSetMaterial(_item, _material.GetRid());
            RenderingServer.CanvasItemSetDefaultTextureFilter(_item, RenderingServer.CanvasItemTextureFilter.Nearest);
        }

        /// <summary>Start a frame: everything outside the drawn land is <paramref name="clear"/>.</summary>
        public void Begin(Transform2D view, Color clear)
        {
            if (!IsBuilt) return;
            _target.ClearColor = new Color(clear.R, clear.G, clear.B, 1f);
            RenderingServer.CanvasItemClear(_item);
            RenderingServer.CanvasItemSetTransform(_item, view);
            _points.Clear();
            _colors.Clear();
            _indices.Clear();
            _sprites.Clear();
        }

        /// <summary>One land tile: top, right, bottom, left corners and their light.</summary>
        public void AddLand(Vector2 top, Vector2 right, Vector2 bottom, Vector2 left,
            Color cTop, Color cRight, Color cBottom, Color cLeft)
        {
            int b = _points.Count;
            _points.Add(top); _points.Add(right); _points.Add(bottom); _points.Add(left);
            _colors.Add(Opaque(cTop)); _colors.Add(Opaque(cRight)); _colors.Add(Opaque(cBottom)); _colors.Add(Opaque(cLeft));
            _indices.Add(b); _indices.Add(b + 1); _indices.Add(b + 2);
            _indices.Add(b); _indices.Add(b + 2); _indices.Add(b + 3);
        }

        /// <summary>
        /// One tall static: its sprite, lit <paramref name="bottom"/> at its base
        /// and <paramref name="top"/> at its top (light falls off going up a wall).
        /// </summary>
        public void AddSprite(Rect2 rect, Texture2D texture, Rect2 source, Color bottom, Color top)
        {
            if (texture == null) return;
            _sprites.Add((rect, texture, source, Opaque(bottom), Opaque(top)));
        }

        /// <summary>Send the frame: the land first, the sprites over it in the order added.</summary>
        public void End()
        {
            if (!IsBuilt) return;
            LastQuads = _points.Count / 4;
            LastSprites = _sprites.Count;
            if (_points.Count > 0)
            {
                RenderingServer.CanvasItemAddTriangleArray(
                    _item, _indices.ToArray(), _points.ToArray(), _colors.ToArray());
            }

            // Sprites go as textured quads with a colour per corner, one
            // triangle array per run of the same atlas page, in order.
            Texture2D run = null;
            foreach (var s in _sprites)
            {
                if (!ReferenceEquals(run, s.texture))
                {
                    FlushSprites(run);
                    run = s.texture;
                }

                float tw = Math.Max(1, s.texture.GetWidth()), th = Math.Max(1, s.texture.GetHeight());
                float u0 = s.source.Position.X / tw, v0 = s.source.Position.Y / th;
                float u1 = s.source.End.X / tw, v1 = s.source.End.Y / th;
                int b = _spritePoints.Count;
                _spritePoints.Add(s.rect.Position);
                _spritePoints.Add(new Vector2(s.rect.End.X, s.rect.Position.Y));
                _spritePoints.Add(s.rect.End);
                _spritePoints.Add(new Vector2(s.rect.Position.X, s.rect.End.Y));
                _spriteUvs.Add(new Vector2(u0, v0)); _spriteUvs.Add(new Vector2(u1, v0));
                _spriteUvs.Add(new Vector2(u1, v1)); _spriteUvs.Add(new Vector2(u0, v1));
                _spriteColors.Add(s.top); _spriteColors.Add(s.top); _spriteColors.Add(s.bottom); _spriteColors.Add(s.bottom);
                _spriteIndices.Add(b); _spriteIndices.Add(b + 1); _spriteIndices.Add(b + 2);
                _spriteIndices.Add(b); _spriteIndices.Add(b + 2); _spriteIndices.Add(b + 3);
            }

            FlushSprites(run);
        }

        private void FlushSprites(Texture2D texture)
        {
            if (_spritePoints.Count == 0 || texture == null)
            {
                _spritePoints.Clear(); _spriteUvs.Clear(); _spriteColors.Clear(); _spriteIndices.Clear();
                return;
            }

            RenderingServer.CanvasItemAddTriangleArray(
                _item, _spriteIndices.ToArray(), _spritePoints.ToArray(), _spriteColors.ToArray(),
                _spriteUvs.ToArray(), null, null, texture.GetRid());
            _spritePoints.Clear(); _spriteUvs.Clear(); _spriteColors.Clear(); _spriteIndices.Clear();
        }

        private static Color Opaque(Color c) => new(c.R, c.G, c.B, 1f);

        public void Teardown()
        {
            if (_item.IsValid)
            {
                RenderingServer.FreeRid(_item);
                _item = default;
            }

            _material?.Dispose();
            _material = null;
            _target?.Dispose();
            _target = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Teardown();
        }
    }
}
