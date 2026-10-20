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
    /// quads (one colour per corner and one at the centre) and the tall
    /// statics as their own sprites in their tile's colour. A shadow mask of
    /// the same size, rendered first, holds what each shadow takes away; the
    /// land subtracts it. <c>RenderTargets.Draw</c> blends the texture where
    /// it blends the classic light target.
    /// </summary>
    internal sealed class RealtimeLightTarget : IDisposable
    {
        private const string SpriteShaderCode = @"shader_type canvas_item;
render_mode blend_disabled;
// ADR-0039: the vertex colour is the light; the texture only masks it.
// The light rides a varying from the vertex stage: fragment COLOR is
// already multiplied by the texture sample (as the batcher's id shader
// notes), and a sprite's own colours must not tint its light.
varying vec3 light;
void vertex() { light = COLOR.rgb; }
void fragment() {
    float a = texture(TEXTURE, UV).a;
    if (a < 0.5) { discard; }
    COLOR = vec4(light, 1.0);
}";

        // The land: its vertex light, capped inside a shadow by the light
        // the shadow leaves there. The mask is the same size as this target,
        // so SCREEN_UV addresses it texel for texel; where no shadow was
        // drawn it is transparent and the land keeps its light. A cap, not a
        // subtraction: a shadow can never darken below what is left, nor
        // shift the hue where the land took less light than its caster's foot.
        private const string LandShaderCode = @"shader_type canvas_item;
render_mode blend_disabled;
uniform sampler2D shadow_mask : filter_nearest;
uniform sampler2D ambient_mask : filter_nearest;
uniform vec3 ambient_light = vec3(1.0);
uniform bool debug_mask = false;
void fragment() {
    vec4 m = texture(shadow_mask, SCREEN_UV);
    vec3 lit = m.a > 0.5 ? min(COLOR.rgb, m.rgb) : COLOR.rgb;
    // The ambient mask is white, multiplied down where ambient shadows fall:
    // that share of the sky's light is taken away.
    float kept = texture(ambient_mask, SCREEN_UV).r;
    lit = max(lit - ambient_light * (1.0 - kept), vec3(0.0));
    COLOR = debug_mask ? vec4(m.a > 0.5 ? m.rgb : vec3(1.0, 0.0, 1.0), 1.0) * kept : vec4(lit, 1.0);
}";

        // An ambient shadow: the share of the sky it keeps, multiplied in.
        private const string AmbientShaderCode = @"shader_type canvas_item;
render_mode blend_mul;
uniform float kept = 0.6;
void fragment() {
    float a = texture(TEXTURE, UV).a;
    if (a < 0.5) { discard; }
    COLOR = vec4(vec3(kept), 1.0);
}";

        private RenderTarget2D _target;
        private RenderTarget2D _shadow, _ambient;
        private Rid _landItem, _item, _shadowItem, _ambientItem;
        private ShaderMaterial _material, _landMaterial, _ambientMaterial;
        private readonly List<(Texture2D texture, Rect2 source, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl)> _ambientShadows = new();
        private readonly List<Vector2> _points = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _indices = new();
        private readonly List<(Rect2 rect, Texture2D texture, Rect2 source, Color bottom, Color top)> _sprites = new();
        private readonly List<(int start, Texture2D texture)> _shadows = new();
        private readonly List<Vector2> _shadowPoints = new();
        private readonly List<Vector2> _shadowUvs = new();
        private readonly List<Color> _shadowColors = new();
        private readonly List<Vector2> _spritePoints = new();
        private readonly List<Vector2> _spriteUvs = new();
        private readonly List<Color> _spriteColors = new();
        private readonly List<int> _spriteIndices = new();
        private bool _disposed;

        public bool IsBuilt => _target != null && !_target.IsDisposed && !_disposed;
        public Texture2D Texture => _target?.Texture;
        public int Width => _target?.Width ?? 0;
        public int Height => _target?.Height ?? 0;

        /// <summary>Land quads, sprites and shadows sent by the last <see cref="End"/> (the cost log).</summary>
        public int LastQuads { get; private set; }
        public int LastSprites { get; private set; }
        public int LastShadows { get; private set; }

        public void EnsureSize(Node parent, int width, int height)
        {
            if (parent == null || width <= 0 || height <= 0) return;
            if (_target != null && !_target.IsDisposed && _target.Width == width && _target.Height == height) return;

            Teardown();
            _target = new RenderTarget2D(parent, width, height);
            // Nested under the light target's viewport: Godot renders a child
            // viewport before its parent, so the mask is ready when the land reads it.
            _shadow = new RenderTarget2D(_target.ViewportNode, width, height);
            _ambient = new RenderTarget2D(_target.ViewportNode, width, height) { ClearColor = Colors.White };
            _material = new ShaderMaterial { Shader = new Shader { Code = SpriteShaderCode } };
            _ambientMaterial = new ShaderMaterial { Shader = new Shader { Code = AmbientShaderCode } };
            _landMaterial = new ShaderMaterial { Shader = new Shader { Code = LandShaderCode } };
            _landMaterial.SetShaderParameter("shadow_mask", _shadow.Texture);
            _landMaterial.SetShaderParameter("ambient_mask", _ambient.Texture);
            _landMaterial.SetShaderParameter("debug_mask", System.Environment.GetEnvironmentVariable("GUO_RTL_DEBUG") == "mask");

            // Created in draw order: the land, then the sprites over it.
            _landItem = NewItem(_target.CanvasItem, _landMaterial);
            _item = NewItem(_target.CanvasItem, _material);
            _shadowItem = NewItem(_shadow.CanvasItem, _material);
            _ambientItem = NewItem(_ambient.CanvasItem, _ambientMaterial);
        }

        private static Rid NewItem(Rid parent, ShaderMaterial material)
        {
            Rid item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(item, parent);
            RenderingServer.CanvasItemSetMaterial(item, material.GetRid());
            RenderingServer.CanvasItemSetDefaultTextureFilter(item, RenderingServer.CanvasItemTextureFilter.Nearest);
            return item;
        }

        /// <summary>
        /// Start a frame: everything outside the drawn land is <paramref name="clear"/>,
        /// the open ambient, of which an ambient shadow takes <paramref name="ambientShadow"/>.
        /// </summary>
        public void Begin(Transform2D view, Color clear, float ambientShadow)
        {
            if (!IsBuilt) return;
            _target.ClearColor = new Color(clear.R, clear.G, clear.B, 1f);
            _landMaterial.SetShaderParameter("ambient_light", new Vector3(clear.R, clear.G, clear.B));
            _ambientMaterial.SetShaderParameter("kept", 1f - ambientShadow);
            _ambientShadows.Clear();
            ClearItem(_landItem, view);
            ClearItem(_item, view);
            ClearItem(_shadowItem, view);
            ClearItem(_ambientItem, view);
            _points.Clear();
            _colors.Clear();
            _indices.Clear();
            _sprites.Clear();
            _shadows.Clear();
            _shadowPoints.Clear();
            _shadowUvs.Clear();
            _shadowColors.Clear();
        }

        private static void ClearItem(Rid item, Transform2D view)
        {
            RenderingServer.CanvasItemClear(item);
            RenderingServer.CanvasItemSetTransform(item, view);
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
        /// One land tile with a light at its centre as well: four triangles
        /// fanned from the centre, so a pool peaks where its light stands.
        /// </summary>
        public void AddLand(Vector2 top, Vector2 right, Vector2 bottom, Vector2 left, Vector2 centre,
            Color cTop, Color cRight, Color cBottom, Color cLeft, Color cCentre)
        {
            int b = _points.Count;
            _points.Add(top); _points.Add(right); _points.Add(bottom); _points.Add(left); _points.Add(centre);
            _colors.Add(Opaque(cTop)); _colors.Add(Opaque(cRight)); _colors.Add(Opaque(cBottom)); _colors.Add(Opaque(cLeft)); _colors.Add(Opaque(cCentre));
            for (int k = 0; k < 4; k++)
            {
                _indices.Add(b + 4); _indices.Add(b + k); _indices.Add(b + (k + 1) % 4);
            }
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

        /// <summary>
        /// One ambient shadow: the sprite's quad at the four screen corners
        /// (its top row on <paramref name="tl"/>..<paramref name="tr"/>).
        /// </summary>
        public void AddAmbientShadow(Texture2D texture, Rect2 source, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl)
        {
            if (texture == null) return;
            _ambientShadows.Add((texture, source, tl, tr, br, bl));
        }

        /// <summary>Vertices per side of a shadow's grid.</summary>
        public const int ShadowGrid = ShadowCells + 1;

        /// <summary>Cells per side of a shadow's grid: the light left is sampled at every vertex.</summary>
        public const int ShadowCells = 3;

        /// <summary>
        /// One shadow: a sprite's silhouette laid on the ground as a
        /// <see cref="ShadowGrid"/> x <see cref="ShadowGrid"/> grid, rows from
        /// the sprite's top row to its bottom row, columns left to right; each
        /// vertex carries the light left there once the shadow's light is
        /// taken away. Later shadows overwrite earlier ones where they overlap.
        /// </summary>
        public void AddShadow(Texture2D texture, Rect2 source, ReadOnlySpan<Vector2> points, ReadOnlySpan<Color> shades)
        {
            if (texture == null) return;
            float tw = Math.Max(1, texture.GetWidth()), th = Math.Max(1, texture.GetHeight());
            _shadows.Add((_shadowPoints.Count, texture));
            for (int j = 0; j < ShadowGrid; j++)
            {
                for (int i = 0; i < ShadowGrid; i++)
                {
                    int k = j * ShadowGrid + i;
                    _shadowPoints.Add(points[k]);
                    _shadowUvs.Add(new Vector2(
                        (source.Position.X + source.Size.X * i / ShadowCells) / tw,
                        (source.Position.Y + source.Size.Y * j / ShadowCells) / th));
                    _shadowColors.Add(Opaque(shades[k]));
                }
            }
        }

        /// <summary>Send the frame: the land first, the sprites over it in the order added; the shadows to the mask.</summary>
        public void End()
        {
            if (!IsBuilt) return;
            LastQuads = _indices.Count / 12;
            LastSprites = _sprites.Count;
            LastShadows = _shadows.Count + _ambientShadows.Count;
            if (_points.Count > 0)
            {
                RenderingServer.CanvasItemAddTriangleArray(
                    _landItem, _indices.ToArray(), _points.ToArray(), _colors.ToArray());
            }

            // Sprites go as textured quads with a colour per corner, one
            // triangle array per run of the same atlas page, in order.
            Texture2D run = null;
            foreach (var s in _sprites)
            {
                if (!ReferenceEquals(run, s.texture))
                {
                    Flush(_item, run);
                    run = s.texture;
                }

                AddQuad(s.texture, s.source,
                    s.rect.Position, new Vector2(s.rect.End.X, s.rect.Position.Y), s.rect.End, new Vector2(s.rect.Position.X, s.rect.End.Y),
                    s.top, s.top, s.bottom, s.bottom);
            }

            Flush(_item, run);

            run = null;
            foreach (var (start, texture) in _shadows)
            {
                if (!ReferenceEquals(run, texture))
                {
                    Flush(_shadowItem, run);
                    run = texture;
                }

                int b = _spritePoints.Count;
                for (int k = 0; k < ShadowGrid * ShadowGrid; k++)
                {
                    _spritePoints.Add(_shadowPoints[start + k]);
                    _spriteUvs.Add(_shadowUvs[start + k]);
                    _spriteColors.Add(_shadowColors[start + k]);
                }

                for (int j = 0; j < ShadowCells; j++)
                {
                    for (int i = 0; i < ShadowCells; i++)
                    {
                        int v = b + j * ShadowGrid + i;
                        _spriteIndices.Add(v); _spriteIndices.Add(v + 1); _spriteIndices.Add(v + ShadowGrid + 1);
                        _spriteIndices.Add(v); _spriteIndices.Add(v + ShadowGrid + 1); _spriteIndices.Add(v + ShadowGrid);
                    }
                }
            }

            Flush(_shadowItem, run);

            run = null;
            foreach (var a in _ambientShadows)
            {
                if (!ReferenceEquals(run, a.texture))
                {
                    Flush(_ambientItem, run);
                    run = a.texture;
                }

                AddQuad(a.texture, a.source, a.tl, a.tr, a.br, a.bl, Colors.White, Colors.White, Colors.White, Colors.White);
            }

            Flush(_ambientItem, run);
        }

        /// <summary>A textured quad: top-left, top-right, bottom-right, bottom-left of the source rect.</summary>
        private void AddQuad(Texture2D texture, Rect2 source, Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl,
            Color ctl, Color ctr, Color cbr, Color cbl)
        {
            float tw = Math.Max(1, texture.GetWidth()), th = Math.Max(1, texture.GetHeight());
            float u0 = source.Position.X / tw, v0 = source.Position.Y / th;
            float u1 = source.End.X / tw, v1 = source.End.Y / th;
            int b = _spritePoints.Count;
            _spritePoints.Add(tl); _spritePoints.Add(tr); _spritePoints.Add(br); _spritePoints.Add(bl);
            _spriteUvs.Add(new Vector2(u0, v0)); _spriteUvs.Add(new Vector2(u1, v0));
            _spriteUvs.Add(new Vector2(u1, v1)); _spriteUvs.Add(new Vector2(u0, v1));
            _spriteColors.Add(ctl); _spriteColors.Add(ctr); _spriteColors.Add(cbr); _spriteColors.Add(cbl);
            _spriteIndices.Add(b); _spriteIndices.Add(b + 1); _spriteIndices.Add(b + 2);
            _spriteIndices.Add(b); _spriteIndices.Add(b + 2); _spriteIndices.Add(b + 3);
        }

        private void Flush(Rid item, Texture2D texture)
        {
            if (_spritePoints.Count == 0 || texture == null)
            {
                _spritePoints.Clear(); _spriteUvs.Clear(); _spriteColors.Clear(); _spriteIndices.Clear();
                return;
            }

            RenderingServer.CanvasItemAddTriangleArray(
                item, _spriteIndices.ToArray(), _spritePoints.ToArray(), _spriteColors.ToArray(),
                _spriteUvs.ToArray(), null, null, texture.GetRid());
            _spritePoints.Clear(); _spriteUvs.Clear(); _spriteColors.Clear(); _spriteIndices.Clear();
        }

        private static Color Opaque(Color c) => new(c.R, c.G, c.B, 1f);

        public void Teardown()
        {
            foreach (Rid item in new[] { _landItem, _item, _shadowItem, _ambientItem })
            {
                if (item.IsValid) RenderingServer.FreeRid(item);
            }
            _landItem = _item = _shadowItem = _ambientItem = default;

            _material?.Dispose();
            _material = null;
            _landMaterial?.Dispose();
            _landMaterial = null;
            _ambientMaterial?.Dispose();
            _ambientMaterial = null;
            _shadow?.Dispose();
            _shadow = null;
            _ambient?.Dispose();
            _ambient = null;
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
