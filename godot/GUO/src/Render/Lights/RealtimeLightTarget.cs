// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): draws the light field into a light target of its own.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The Realtime lights target: a <see cref="RenderTarget2D"/> the size of
    /// the classic light target, filled each frame with the land as iso quads
    /// and the tall statics as their own sprites, each pixel lit from the
    /// field's samples at its own world position (the field rides in as a
    /// texture). <c>RenderTargets.Draw</c> blends its texture where it blends
    /// the classic light target.
    /// </summary>
    /// <remarks>
    /// A vertex's colour carries where it is, not its colour: R, G the field
    /// texture coordinate of its world point, B its height above the face's
    /// base ((px + 256) / 1024), A = 1. A = 0 is a flat vertex whose RGB is the
    /// light itself (roofs, what stands above every light).
    /// </remarks>
    internal sealed class RealtimeLightTarget : IDisposable
    {
        private const string ShaderCode = @"shader_type canvas_item;
render_mode blend_disabled;
// ADR-0039: the light comes from the field texture at each pixel's world
// point; the art texture only masks it. The vertex data rides a varying:
// fragment COLOR is already multiplied by the texture sample, and a
// sprite's own colours must not tint its light.
uniform sampler2D field : filter_linear, repeat_disable;
uniform vec3 roof_ambient = vec3(1.0);
varying vec4 data;
void vertex() { data = COLOR; }
void fragment() {
    float a = texture(TEXTURE, UV).a;
    if (a < 0.5) { discard; }
    vec3 light;
    if (data.a > 0.5) {
        vec3 e = texture(field, data.xy).rgb;
        light = e * e * 8.0;
        float m = max(light.r, max(light.g, light.b));
        if (m > 1.0) { light /= m; }
        // Light falls off going up a wall: its top course takes less, and
        // 30 Z above the floor (a gable, a roof edge) only the roof ambient.
        float h = data.z * 1024.0 - 256.0;
        light = mix(light, roof_ambient, smoothstep(0.0, 120.0, h));
    } else {
        light = data.rgb;
    }
    COLOR = vec4(light, 1.0);
}";

        private RenderTarget2D _target;
        private Rid _item;
        private ShaderMaterial _material;
        private Image _fieldImage;
        private ImageTexture _fieldTexture;
        private int _fieldW, _fieldH;
        private byte[] _uploadedField = Array.Empty<byte>();
        private int _originX, _originY, _gridW = 1, _gridH = 1;
        private readonly List<Vector2> _points = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _indices = new();
        private readonly List<(Rect2 rect, Texture2D texture, Rect2 source, Color c0, Color c1, Color c2, Color c3)> _sprites = new();
        private readonly List<Vector2> _spritePoints = new();
        private readonly List<Vector2> _spriteUvs = new();
        private readonly List<Color> _spriteColors = new();
        private readonly List<int> _spriteIndices = new();
        private bool _disposed;

        public bool IsBuilt => _target != null && !_target.IsDisposed && !_disposed;
        public Texture2D Texture => _target?.Texture;
        public int Width => _target?.Width ?? 0;
        public int Height => _target?.Height ?? 0;

        /// <summary>Land quads and sprite quads sent by the last <see cref="End"/> (the cost log).</summary>
        public int LastQuads { get; private set; }
        public int LastSprites { get; private set; }
        internal int FieldUploads { get; private set; }

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
            // The art masks stay nearest; only the field uniform is linear.
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

        /// <summary>
        /// This frame's field: <paramref name="rgba"/> from <see cref="RealtimeLightField.FillTexture"/>,
        /// <paramref name="width"/> x <paramref name="height"/> samples over the tiles
        /// [originX, originX + gridW) x [originY, originY + gridH).
        /// </summary>
        public void SetField(byte[] rgba, int width, int height, int originX, int originY, int gridW, int gridH, Color roofAmbient)
        {
            if (!IsBuilt) return;
            _originX = originX;
            _originY = originY;
            _gridW = Math.Max(1, gridW);
            _gridH = Math.Max(1, gridH);
            bool resized = _fieldImage == null || _fieldW != width || _fieldH != height;
            bool changed = resized || !rgba.AsSpan().SequenceEqual(_uploadedField);
            if (resized)
            {
                _fieldTexture?.Dispose();
                _fieldImage?.Dispose();
                _fieldImage = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
                _fieldTexture = ImageTexture.CreateFromImage(_fieldImage);
                _fieldW = width;
                _fieldH = height;
                _material.SetShaderParameter("field", _fieldTexture);
            }
            else if (changed)
            {
                _fieldImage.SetData(width, height, false, Image.Format.Rgba8, rgba);
                _fieldTexture.Update(_fieldImage);
            }
            if (changed)
            {
                if (_uploadedField.Length != rgba.Length) _uploadedField = new byte[rgba.Length];
                rgba.CopyTo(_uploadedField, 0);
                FieldUploads++;
            }

            _material.SetShaderParameter("roof_ambient", new Vector3(roofAmbient.R, roofAmbient.G, roofAmbient.B));
        }

        /// <summary>A vertex's data for the world point (wx, wy) in tiles, <paramref name="height"/> pixels above its base.</summary>
        public Color At(float wx, float wy, float height = 0f)
            => new((wx - _originX) / _gridW, (wy - _originY) / _gridH, (height + 256f) / 1024f, 1f);

        /// <summary>A flat vertex: this light, wherever it is.</summary>
        public static Color Flat(Color light) => new(light.R, light.G, light.B, 0f);

        /// <summary>One land tile: top, right, bottom, left corners and their data (<see cref="At"/>).</summary>
        public void AddLand(Vector2 top, Vector2 right, Vector2 bottom, Vector2 left,
            Color cTop, Color cRight, Color cBottom, Color cLeft)
        {
            int b = _points.Count;
            _points.Add(top); _points.Add(right); _points.Add(bottom); _points.Add(left);
            _colors.Add(cTop); _colors.Add(cRight); _colors.Add(cBottom); _colors.Add(cLeft);
            _indices.Add(b); _indices.Add(b + 1); _indices.Add(b + 2);
            _indices.Add(b); _indices.Add(b + 2); _indices.Add(b + 3);
        }

        /// <summary>
        /// One quad of a static's sprite: <paramref name="rect"/> on screen,
        /// <paramref name="source"/> in the art texture, and each corner's data
        /// (top left, top right, bottom right, bottom left).
        /// </summary>
        public void AddSprite(Rect2 rect, Texture2D texture, Rect2 source, Color c0, Color c1, Color c2, Color c3)
        {
            if (texture == null) return;
            _sprites.Add((rect, texture, source, c0, c1, c2, c3));
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

            // Sprites go as textured quads with data per corner, one
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
                _spriteColors.Add(s.c0); _spriteColors.Add(s.c1); _spriteColors.Add(s.c2); _spriteColors.Add(s.c3);
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

        public void Teardown()
        {
            if (_item.IsValid)
            {
                RenderingServer.FreeRid(_item);
                _item = default;
            }

            _material?.Dispose();
            _material = null;
            _fieldTexture?.Dispose();
            _fieldTexture = null;
            _fieldImage?.Dispose();
            _fieldImage = null;
            _fieldW = _fieldH = 0;
            _uploadedField = Array.Empty<byte>();
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
