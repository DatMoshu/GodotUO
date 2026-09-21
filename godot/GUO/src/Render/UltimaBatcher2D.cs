// SPDX-License-Identifier: BSD-2-Clause
//
// Reimplementation of ClassicUO's UltimaBatcher2D on Godot's canvas item API.
// See docs/architecture/ADR-0002-batcher-on-godot-canvas.md; the reasoning for
// every deviation below lives there rather than being restated here.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;

// Godot's Color is what a canvas modulate takes. Compat's is the byte-packed
// XNA one. Both are in scope; say which this file means.
using Color = Godot.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// Draws ClassicUO's sprites. Upstream builds vertex buffers and hands them
    /// to FNA; this builds canvas item commands and hands them to Godot. The
    /// public surface is upstream's, unchanged, so its ~120 call sites port
    /// without edits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO) — the constructor. Upstream takes a
    /// <c>GraphicsDevice</c>; Godot has none. It takes the canvas item every
    /// sprite hangs under instead, which is the equivalent handle.
    /// </para>
    /// <para>
    /// PORT DEVIATION (GUO) — <c>SetBlendState</c>, <c>SetSampler</c> and
    /// <c>EnableScissorTest</c> are absent. All three take FNA pipeline-state
    /// objects, and nothing in the port calls any of them: the only
    /// <c>SetStencil</c> references in the tree are commented out upstream. A
    /// method that exists and does nothing is worse than one that is missing,
    /// because the missing one is a compile error at the call site that needs
    /// it. ADR-0002 originally said these would become GUO enums; nothing
    /// asked for them, so they are not here yet.
    /// </para>
    /// <para>
    /// The <c>depth</c> argument every draw takes is a sort key, not a Z write
    /// — Godot's 2D canvas has no depth buffer and paints in submission order.
    /// ADR-0001 keeps <c>RenderLists</c>, which already sorts by
    /// <c>CalculateDepthZ()</c> before anything reaches here, so submission
    /// order is the sorted order and the argument is accepted and ignored.
    /// </para>
    /// </remarks>
    public sealed class UltimaBatcher2D : IDisposable
    {
        private const string SHADER_PATH = "res://src/Render/shaders/uo_hue.gdshader";

        private static readonly float[] _cornerOffsetX = new float[] { 0.0f, 1.0f, 0.0f, 1.0f };
        private static readonly float[] _cornerOffsetY = new float[] { 0.0f, 0.0f, 1.0f, 1.0f };

        // A quad as two triangles, in upstream's winding (GenerateIndexArray).
        private static readonly int[] _quadIndices = { 0, 1, 2, 1, 3, 2 };

        private readonly Rid _parent;
        private readonly ShaderMaterial _material;

        // Every draw goes into a canvas item. A new one is started whenever a
        // clip opens or closes, because a Godot canvas item paints its own
        // commands before any of its children -- so interleaving clipped and
        // unclipped work inside one item would reorder it. Items are pooled
        // and reused frame to frame; the pool index doubles as the draw index,
        // which is what makes a later item paint over an earlier sibling.
        private readonly List<Rid> _items = new List<Rid>();
        private int _itemCount;

        private readonly List<Rid> _clipStack = new List<Rid>();
        private Rid _current;

        private bool _started;
        private Vector2 _worldOffset;

        // Reused by the quad path so a rotated or mirrored sprite does not
        // allocate four arrays per draw.
        private readonly Vector2[] _quadPoints = new Vector2[4];
        private readonly Vector2[] _quadUVs = new Vector2[4];
        private readonly Color[] _quadColors = new Color[4];

        public UltimaBatcher2D(Rid parentCanvasItem)
        {
            _parent = parentCanvasItem;

            var shader = GD.Load<Shader>(SHADER_PATH);

            _material = new ShaderMaterial { Shader = shader };
        }

        /// <summary>
        /// hues.mul as a texture: 16 palettes across, 1024 down, 32 entries
        /// each. Until this is set every hued sprite samples nothing.
        /// </summary>
        public Texture2D HueTexture
        {
            set => _material.SetShaderParameter("hue_texture", value);
        }

        /// <summary>The coloured-light table, sampled by SHADER_LIGHTS.</summary>
        public Texture2D LightTexture
        {
            set => _material.SetShaderParameter("light_texture", value);
        }

        /// <summary>
        /// Where the circle of transparency is centred, in canvas pixels. Not
        /// upstream's: its shader derives this from the viewport, which Godot
        /// gives the shader in different terms.
        /// </summary>
        public Vector2 CircleOfTransparencyCenter
        {
            set => _material.SetShaderParameter("circle_of_transparency_center", value);
        }

        public int TextureSwitches, FlushesDone;

        public void Dispose()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                RenderingServer.FreeRid(_items[i]);
            }

            _items.Clear();
            _material?.Dispose();
        }

        public void SetBrightlight(float f)
        {
            _material.SetShaderParameter("brightlight", f);
        }

        public void SetCircleOfTransparencyRadius(float radius)
        {
            _material.SetShaderParameter("circle_of_transparency_radius", radius);
        }


        // ==========================
        // === Frame ================
        // ==========================

        public void Begin()
        {
            EnsureNotStarted();

            _started = true;
            _worldOffset = Vector2.Zero;
            _clipStack.Clear();
            _itemCount = 0;

            TextureSwitches = 0;
            FlushesDone = 0;

            // Everything in the pool, not just what this frame ends up using:
            // an item left over from a busier frame would otherwise keep
            // painting last frame's sprites.
            for (int i = 0; i < _items.Count; i++)
            {
                RenderingServer.CanvasItemClear(_items[i]);
            }

            Cut();
        }

        public void End()
        {
            EnsureStarted();

            _started = false;
        }

        public void SetWorldOffset(int offsetX, int offsetY)
        {
            _worldOffset = new Vector2(-offsetX, -offsetY);

            Cut();
        }

        public void ResetWorldOffset()
        {
            _worldOffset = Vector2.Zero;

            Cut();
        }

        public bool ClipBegin(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            // Upstream runs the rect through ScissorStack.CalculateScissors to
            // get it into screen space, and intersects it with the enclosing
            // scissor by hand. Neither is needed here: these items already sit
            // under the node carrying the camera transform, and nesting the
            // clip item inside the enclosing one is what intersects them.
            Rid clip = NewItem(CurrentParent);

            RenderingServer.CanvasItemSetCustomRect(clip, true, new Rect2(x, y, width, height));
            RenderingServer.CanvasItemSetClip(clip, true);

            _clipStack.Add(clip);

            Cut();

            return true;
        }

        public void ClipEnd()
        {
            if (_clipStack.Count > 0)
            {
                _clipStack.RemoveAt(_clipStack.Count - 1);
            }

            Cut();
        }


        // ==========================
        // === UO drawing methods ===
        // ==========================

        public struct YOffsets
        {
            public int Top;
            public int Right;
            public int Left;
            public int Bottom;
        }

        public void DrawStretchedLand
        (
            Texture2D texture,
            Vector2 position,
            Rectangle sourceRect,
            ref YOffsets yOffsets,
            ref Vector3 normalTop,
            ref Vector3 normalRight,
            ref Vector3 normalLeft,
            ref Vector3 normalBottom,
            Vector3 hue,
            float depth
        )
        {
            // The four normals are per-vertex lighting input for the shader's
            // LAND modes. A canvas quad's only per-vertex channel is the
            // modulate colour, and ADR-0002 spends all four of its bytes on
            // the hue. Stretched land belongs to the world mesh, where a real
            // vertex format is available.
            throw new NotSupportedException(
                "DrawStretchedLand needs per-vertex normals, which a Godot canvas quad " +
                "cannot carry. It belongs to the world mesh path; see ADR-0002.");
        }

        public void DrawShadow(Texture2D texture, Vector2 position, Rectangle sourceRect, bool flip, float depth)
        {
            float width = sourceRect.Width;
            float height = sourceRect.Height * 0.5f;
            float translatedY = position.Y + height - 10;
            float ratio = height / width;

            _quadPoints[0] = new Vector2(position.X + width * ratio, translatedY);
            _quadPoints[1] = new Vector2(position.X + width * (ratio + 1f), translatedY);
            _quadPoints[2] = new Vector2(position.X, translatedY + height);
            _quadPoints[3] = new Vector2(position.X + width, translatedY + height);

            CalculateHalfPixelUVs(sourceRect, texture.GetWidth(), texture.GetHeight(),
                out float sourceX, out float sourceY, out float sourceW, out float sourceH);

            byte effects = (byte)((flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None) & (SpriteEffects)0x03);

            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);

            // Hue index 0, SHADER_SHADOW, fully opaque -- upstream's constant.
            Vector3 hue;
            hue.X = 0;
            hue.Y = ShaderHueTranslator.SHADER_SHADOW;
            hue.Z = 1f;

            AddQuad(texture, hue);
        }

        public void DrawCharacterSitted
        (
            Texture2D texture,
            Vector2 position,
            Rectangle sourceRect,
            Vector3 mod,
            Vector3 hue,
            bool flip,
            float depth
        )
        {
            float h03 = sourceRect.Height * mod.X;
            float h06 = sourceRect.Height * mod.Y;
            float h09 = sourceRect.Height * mod.Z;

            float sittingOffset = flip ? -8.0f : 8.0f;

            float width = sourceRect.Width;
            float widthOffset = sourceRect.Width + sittingOffset;

            if (mod.X != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.X,
                    position.X + sittingOffset, position.Y,
                    position.X + widthOffset, position.Y,
                    position.X + sittingOffset, position.Y + h03,
                    position.X + widthOffset, position.Y + h03,
                    0f);
            }

            if (mod.Y != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.Y,
                    position.X + sittingOffset, position.Y + h03,
                    position.X + widthOffset, position.Y + h03,
                    position.X, position.Y + h06,
                    position.X + width, position.Y + h06,
                    h03);
            }

            if (mod.Z != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.Z,
                    position.X, position.Y + h06,
                    position.X + width, position.Y + h06,
                    position.X, position.Y + h09,
                    position.X + width, position.Y + h09,
                    h06);
            }
        }

        private void DrawSittedSection(
            Texture2D texture,
            ref Rectangle sourceRect,
            ref Vector3 hue,
            bool flip,
            float depth,
            float modValue,
            float x0, float y0,
            float x1, float y1,
            float x2, float y2,
            float x3, float y3,
            float uvYOffset)
        {
            _quadPoints[0] = new Vector2(x0, y0);
            _quadPoints[1] = new Vector2(x1, y1);
            _quadPoints[2] = new Vector2(x2, y2);
            _quadPoints[3] = new Vector2(x3, y3);

            CalculateHalfPixelUVs(sourceRect, texture.GetWidth(), texture.GetHeight(),
                out float sourceX, out float sourceY, out float sourceW, out float sourceH);

            float invH = 1f / texture.GetHeight();
            sourceY += uvYOffset * invH;
            sourceH -= uvYOffset * invH;

            byte effects = (byte)((flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None) & (SpriteEffects)0x03);

            // The bottom two corners take a shortened V: upstream scales their
            // sourceH by modValue and leaves the top two alone.
            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH * modValue, effects);

            AddQuad(texture, hue);
        }

        public void DrawTiled
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle sourceRectangle,
            Vector3 hue,
            float layerDepth
        )
        {
            int h = destinationRectangle.Height;

            Rectangle rect = sourceRectangle;
            Vector2 pos = new Vector2(destinationRectangle.X, destinationRectangle.Y);

            while (h > 0)
            {
                pos.X = destinationRectangle.X;
                int w = destinationRectangle.Width;

                rect.Height = Math.Min(h, sourceRectangle.Height);

                while (w > 0)
                {
                    rect.Width = Math.Min(w, sourceRectangle.Width);

                    Draw
                    (
                        texture,
                        pos,
                        rect,
                        hue,
                        layerDepth
                    );

                    w -= sourceRectangle.Width;
                    pos.X += sourceRectangle.Width;
                }

                h -= sourceRectangle.Height;
                pos.Y += sourceRectangle.Height;
            }
        }

        public bool DrawRectangle
        (
            Texture2D texture,
            int x,
            int y,
            int width,
            int height,
            Vector3 hue,
            float depth
        )
        {
            Rectangle rect = new Rectangle(x, y, width, 1);
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X += width;
            rect.Width = 1;
            rect.Height += height;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X = x;
            rect.Y = y + height;
            rect.Width = width;
            rect.Height = 1;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X = x;
            rect.Y = y;
            rect.Width = 1;
            rect.Height = height;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            return true;
        }

        public void DrawLine
        (
            Texture2D texture,
            Vector2 start,
            Vector2 end,
            Vector3 color,
            float stroke,
            float depth
        )
        {
            var radians = GUO.Utility.MathHelper.AngleBetweenVectors(start, end);

            // Upstream calls Vector2.Distance(ref, ref, out); Godot's Vector2
            // spells the same thing as an instance method.
            float length = start.DistanceTo(end);

            Draw
            (
                texture,
                start,
                new Rectangle(0, 0, texture.GetWidth(), texture.GetHeight()),
                color,
                radians,
                Vector2.Zero,
                new Vector2(length, stroke),
                SpriteEffects.None,
                depth
            );
        }


        // ==========================
        // === Draw overloads =======
        // ==========================

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Vector3 color,
            float depth
        )
        {
            AddSprite(texture, 0f, 0f, 1f, 1f, position.X, position.Y, texture.GetWidth(), texture.GetHeight(), color, 0f, 0f, 0f, 1f, depth, 0);
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float depth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;
            float destW, destH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVs(sourceRectangle.Value, texture.GetWidth(), texture.GetHeight(),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                destW = sourceRectangle.Value.Width;
                destH = sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                destW = texture.GetWidth();
                destH = texture.GetHeight();
            }

            AddSprite(texture, sourceX, sourceY, sourceW, sourceH, position.X, position.Y, destW, destH, color, 0.0f, 0.0f, 0.0f, 1.0f, depth, 0);
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            float scale,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;
            float destW = scale;
            float destH = scale;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, texture.GetWidth(), texture.GetHeight(),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                destW *= sourceRectangle.Value.Width;
                destH *= sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                destW *= texture.GetWidth();
                destH *= texture.GetHeight();
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                position.X,
                position.Y,
                destW,
                destH,
                color,
                origin.X / sourceW / (float)texture.GetWidth(),
                origin.Y / sourceH / (float)texture.GetHeight(),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            Vector2 scale,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, texture.GetWidth(), texture.GetHeight(),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                scale.X *= sourceRectangle.Value.Width;
                scale.Y *= sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                scale.X *= texture.GetWidth();
                scale.Y *= texture.GetHeight();
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                position.X,
                position.Y,
                scale.X,
                scale.Y,
                color,
                origin.X / sourceW / (float)texture.GetWidth(),
                origin.Y / sourceH / (float)texture.GetHeight(),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Vector3 color,
            float layerDepth
        )
        {
            AddSprite(
                texture,
                0.0f,
                0.0f,
                1.0f,
                1.0f,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                0.0f,
                0.0f,
                0.0f,
                1.0f,
                layerDepth,
                0
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle? sourceRectangle,
            Vector3 color,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVs(sourceRectangle.Value, texture.GetWidth(), texture.GetHeight(),
                    out sourceX, out sourceY, out sourceW, out sourceH);
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                0.0f,
                0.0f,
                0.0f,
                1.0f,
                layerDepth,
                0
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, texture.GetWidth(), texture.GetHeight(),
                    out sourceX, out sourceY, out sourceW, out sourceH);
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                origin.X / sourceW / (float)texture.GetWidth(),
                origin.Y / sourceH / (float)texture.GetHeight(),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }


        // ==========================
        // === The one choke point ==
        // ==========================

        private void AddSprite
        (
            Texture2D texture,
            float sourceX,
            float sourceY,
            float sourceW,
            float sourceH,
            float destinationX,
            float destinationY,
            float destinationW,
            float destinationH,
            Vector3 color,
            float originX,
            float originY,
            float rotationSin,
            float rotationCos,
            float depth,
            byte effects
        )
        {
            EnsureStarted();

            if (texture == null)
            {
                return;
            }

            int textureWidth = texture.GetWidth();
            int textureHeight = texture.GetHeight();

            if (rotationSin == 0f && rotationCos == 1f && effects == 0)
            {
                // The overwhelmingly common case: an upright, unmirrored rect.
                // One canvas command, no per-draw arrays.
                RenderingServer.CanvasItemAddTextureRectRegion
                (
                    _current,
                    new Rect2(
                        destinationX - originX * destinationW,
                        destinationY - originY * destinationH,
                        destinationW,
                        destinationH),
                    texture.GetRid(),
                    new Rect2(
                        sourceX * textureWidth,
                        sourceY * textureHeight,
                        sourceW * textureWidth,
                        sourceH * textureHeight),
                    Encode(color),
                    false,
                    // Upstream clamps nothing, so neither does this.
                    false
                );

                return;
            }

            // Rotated or mirrored. The corner maths is upstream's SetVertex,
            // component for component, so a rotated sprite lands on the same
            // pixels it does there.
            float cornerX = -originX * destinationW;
            float cornerY = -originY * destinationH;
            _quadPoints[0] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = (1.0f - originX) * destinationW;
            cornerY = -originY * destinationH;
            _quadPoints[1] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = -originX * destinationW;
            cornerY = (1.0f - originY) * destinationH;
            _quadPoints[2] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = (1.0f - originX) * destinationW;
            cornerY = (1.0f - originY) * destinationH;
            _quadPoints[3] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);

            AddQuad(texture, color);
        }

        /// <summary>
        /// Fills the four UVs the way upstream does, by indexing the corner
        /// offset tables with <c>i ^ effects</c> — which is how a mirrored
        /// sprite gets its corners swapped rather than its rect negated.
        /// </summary>
        /// <param name="sourceHBottom">
        /// The V extent the bottom two corners use. Equal to
        /// <paramref name="sourceH"/> for everything except a sitted
        /// character, whose lower corners take a shortened slice.
        /// </param>
        private void SetQuadUVs(float sourceX, float sourceY, float sourceW, float sourceH, float sourceHBottom, byte effects)
        {
            _quadUVs[0] = new Vector2(
                (_cornerOffsetX[0 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[0 ^ effects] * sourceH) + sourceY);
            _quadUVs[1] = new Vector2(
                (_cornerOffsetX[1 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[1 ^ effects] * sourceH) + sourceY);
            _quadUVs[2] = new Vector2(
                (_cornerOffsetX[2 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[2 ^ effects] * sourceHBottom) + sourceY);
            _quadUVs[3] = new Vector2(
                (_cornerOffsetX[3 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[3 ^ effects] * sourceHBottom) + sourceY);
        }

        /// <summary>
        /// Submits <see cref="_quadPoints"/> and <see cref="_quadUVs"/> as two
        /// triangles. Used for anything a rect command cannot express: a
        /// rotation, a mirror, a shadow's parallelogram, a sitted section.
        /// </summary>
        private void AddQuad(Texture2D texture, Vector3 color)
        {
            EnsureStarted();

            if (texture == null)
            {
                return;
            }

            Color modulate = Encode(color);

            _quadColors[0] = modulate;
            _quadColors[1] = modulate;
            _quadColors[2] = modulate;
            _quadColors[3] = modulate;

            RenderingServer.CanvasItemAddTriangleArray
            (
                _current,
                _quadIndices,
                _quadPoints,
                _quadColors,
                _quadUVs,
                null,
                null,
                texture.GetRid()
            );
        }

        /// <summary>
        /// Packs upstream's (hue index, shader mode, alpha) into the four
        /// bytes of a modulate colour. ADR-0002 explains why the index needs
        /// two channels and why the circle-of-transparency flag moved into the
        /// mode byte.
        /// </summary>
        private static Color Encode(Vector3 color)
        {
            int index = (int)color.X;
            int mode = (int)color.Y;
            float alpha = color.Z;

            // GetHueVector signals circle-of-transparency by adding 1f to the
            // alpha. A colour channel cannot hold that, so it is undone here
            // and carried in the mode byte's top bit -- modes reach 30.
            if (alpha > 1f)
            {
                mode |= 0x80;
                alpha -= 1f;
            }

            return new Color(
                ((index >> 8) & 0xFF) / 255f,
                (index & 0xFF) / 255f,
                (mode & 0xFF) / 255f,
                alpha);
        }


        // ==========================
        // === Canvas item pool =====
        // ==========================

        private Rid CurrentParent => _clipStack.Count > 0 ? _clipStack[_clipStack.Count - 1] : _parent;

        /// <summary>
        /// Ends the current run of commands and starts a fresh item under the
        /// current clip. Called whenever ordering would otherwise be lost.
        /// </summary>
        private void Cut()
        {
            _current = NewItem(CurrentParent);

            if (_worldOffset != Vector2.Zero)
            {
                RenderingServer.CanvasItemSetTransform(
                    _current, new Transform2D(0f, _worldOffset));
            }
        }

        private Rid NewItem(Rid parent)
        {
            Rid item;

            if (_itemCount < _items.Count)
            {
                item = _items[_itemCount];
            }
            else
            {
                item = RenderingServer.CanvasItemCreate();
                RenderingServer.CanvasItemSetMaterial(item, _material.GetRid());
                _items.Add(item);
            }

            RenderingServer.CanvasItemSetParent(item, parent);
            RenderingServer.CanvasItemSetTransform(item, Transform2D.Identity);
            RenderingServer.CanvasItemSetClip(item, false);
            RenderingServer.CanvasItemSetCustomRect(item, false);

            // Siblings paint in draw-index order, and the pool index only ever
            // goes up within a frame, so a later item paints over an earlier one.
            RenderingServer.CanvasItemSetDrawIndex(item, _itemCount);

            _itemCount++;

            return item;
        }


        // ==========================
        // === UV helpers ===========
        // ==========================

        private static void CalculateUVsSafe(
            Rectangle source,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = source.X * invW;
            sourceY = source.Y * invH;
            sourceW = Math.Sign(source.Width) * Math.Max(Math.Abs(source.Width), GUO.Utility.MathHelper.MachineEpsilonFloat) * invW;
            sourceH = Math.Sign(source.Height) * Math.Max(Math.Abs(source.Height), GUO.Utility.MathHelper.MachineEpsilonFloat) * invH;
        }

        private static void CalculateUVs(
            Rectangle source,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = source.X * invW;
            sourceY = source.Y * invH;
            sourceW = source.Width * invW;
            sourceH = source.Height * invH;
        }

        private static void CalculateHalfPixelUVs(
            Rectangle sourceRect,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = (sourceRect.X + 0.5f) * invW;
            sourceY = (sourceRect.Y + 0.5f) * invH;
            sourceW = (sourceRect.Width - 1f) * invW;
            sourceH = (sourceRect.Height - 1f) * invH;
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void EnsureStarted()
        {
            if (!_started)
            {
                throw new InvalidOperationException("UltimaBatcher2D: Begin() has not been called.");
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void EnsureNotStarted()
        {
            if (_started)
            {
                throw new InvalidOperationException("UltimaBatcher2D: End() has not been called.");
            }
        }
    }
}
