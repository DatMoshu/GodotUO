// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using GUO.Configuration;
using GUO.IO;
using GUO.Assets;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.GameObjects
{
    enum ObjectHandlesStatus
    {
        NONE,
        OPEN,
        CLOSED,
        DISPLAYING
    }

    internal abstract partial class GameObject
    {
        public byte AlphaHue;
        public bool AllowedToDraw = true;
        public bool InChunkMesh;
        public int MeshSpriteIndex = -1;
        public ObjectHandlesStatus ObjectHandlesStatus;
        public Rectangle FrameInfo;
        protected bool IsFlipped;

        public abstract bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float CalculateDepthZ()
        {
            int x = X;
            int y = Y;
            int z = PriorityZ;

            // Offsets are in SCREEN coordinates
            if (Offset.X > 0 && Offset.Y < 0)
            {
                // North
            }
            else if (Offset.X > 0 && Offset.Y == 0)
            {
                // Northeast
                x++;
            }
            else if (Offset.X > 0 && Offset.Y > 0)
            {
                // East
                z += Math.Max(0, (int)Offset.Z);
                x++;
            }
            else if (Offset.X == 0 && Offset.Y > 0)
            {
                // Southeast
                x++;
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // South
                z += Math.Max(0, (int)Offset.Z);
                y++;
            }
            else if (Offset.X < 0 && Offset.Y == 0)
            {
                // Southwest
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // West
            }
            else if (Offset.X == 0 && Offset.Y < 0)
            {
                // Northwest
            }

            return (x + y) + (127 + z) * 0.01f;
        }

        public Rectangle GetOnScreenRectangle()
        {
            Rectangle prect = Rectangle.Empty;

            prect.X = (int)(RealScreenPosition.X - FrameInfo.X + 22 + Offset.X);
            prect.Y = (int)(RealScreenPosition.Y - FrameInfo.Y + 22 + (Offset.Y - Offset.Z));
            prect.Width = FrameInfo.Width;
            prect.Height = FrameInfo.Height;

            return prect;
        }

        public virtual bool TransparentTest(int z)
        {
            return false;
        }

        protected static void DrawStatic(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth,
            bool isWet = false,
            VariantAtlas.VariantImage themed = null
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // PORT DEVIATION (GUO): a themed object draws its variant-atlas
            // PNG instead of archive art; the graphic (and its picking and
            // tiledata) stay original.
            Godot.Texture2D texture = themed?.Texture;
            Rectangle uv = default;
            if (texture != null)
            {
                uv = new Rectangle(0, 0, themed.Width, themed.Height);
            }
            else
            {
                if (artInfo.Texture == null)
                {
                    return;
                }

                texture = artInfo.Texture;
                uv = artInfo.UV;
            }

            {
                int w = (uv.Width >> 1) - 22;
                int h = uv.Height - 44;
                if (themed == null)
                {
                    ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                    index.Width = (short)w;
                    index.Height = (short)h;
                }

                x -= w;
                y -= h;

                var pos = new Vector2(x, y);
                var scale = Vector2.One;
                if (isWet)
                {
                    batcher.Draw(
                        texture,
                        pos,
                        uv,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                }

                batcher.Draw(
                    texture,
                    pos,
                    uv,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawGump(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth
        )
        {
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(graphic);

            if (gumpInfo.Texture != null)
            {
                batcher.Draw(
                    gumpInfo.Texture,
                    new Vector2(x, y),
                    gumpInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    1f,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawStaticRotated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            float angle,
            Vector3 hue,
            float depth,
            VariantAtlas.VariantImage themed = null
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // PORT DEVIATION (GUO): themed objects draw the variant-atlas PNG.
            Godot.Texture2D texture = themed?.Texture;
            Rectangle uv = default;
            if (texture != null)
            {
                uv = new Rectangle(0, 0, themed.Width, themed.Height);
            }
            else
            {
                if (artInfo.Texture == null)
                {
                    return;
                }

                texture = artInfo.Texture;
                uv = artInfo.UV;
            }

            int w = (uv.Width >> 1) - 22;
            int h = uv.Height - 44;
            if (themed == null)
            {
                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)w;
                index.Height = (short)h;
            }

            batcher.Draw(
                texture,
                new Rectangle(
                    x - w,
                    y - h,
                    uv.Width,
                    uv.Height
                ),
                uv,
                hue,
                angle,
                Vector2.Zero,
                SpriteEffects.None,
                depth + 0.5f
            );
        }

        protected static void DrawStaticAnimated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            bool shadow,
            float depth,
            bool isWet = false,
            VariantAtlas.VariantImage themed = null
        )
        {
            // PORT DEVIATION (GUO): a themed object draws its variant-atlas
            // PNG still (no animation frames for variants).
            if (themed?.Texture != null)
            {
                var vuv = new Rectangle(0, 0, themed.Width, themed.Height);
                int vw = (vuv.Width >> 1) - 22;
                int vh = vuv.Height - 44;
                Vector2 vpos = new Vector2(x - vw, y - vh);
                if (shadow)
                {
                    batcher.DrawShadow(themed.Texture, vpos, vuv, false, depth + 0.25f);
                }

                batcher.Draw(
                    themed.Texture,
                    vpos,
                    vuv,
                    hue,
                    0f,
                    Vector2.Zero,
                    Vector2.One,
                    SpriteEffects.None,
                    depth + 0.5f
                );

                return;
            }

            ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);

            graphic = (ushort)(graphic + index.AnimOffset);

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.UV.Width >> 1) - 22);
                index.Height = (short)(artInfo.UV.Height - 44);

                x -= index.Width;
                y -= index.Height;

                Vector2 pos = new Vector2(x, y);

                if (shadow)
                {
                    batcher.DrawShadow(artInfo.Texture, pos, artInfo.UV, false, depth + 0.25f);
                }

                var scale = Vector2.One;
                if (isWet)
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                }

                batcher.Draw(
                    artInfo.Texture,
                    pos,
                    artInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }
    }
}
