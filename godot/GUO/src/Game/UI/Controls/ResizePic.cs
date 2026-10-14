// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;
using GUO.Renderer;
using GUO.Utility;
using GUO.Compat;
using System;
using System.Collections.Generic;

namespace GUO.Game.UI.Controls
{
    internal class ResizePic : Control
    {
        private int _maxIndex;

        public ResizePic(ushort graphic)
        {
            CanMove = true;
            CanCloseWithRightClick = true;
            Graphic = graphic;

            for (_maxIndex = 0; _maxIndex < 9; ++_maxIndex)
            {
                if (Client.Game.UO.Gumps.GetGump((ushort)(Graphic + _maxIndex)).Texture == null)
                {
                    break;
                }
            }
        }

        public ResizePic(List<string> parts) : this(UInt16Converter.Parse(parts[3]))
        {
            X = int.Parse(parts[1]);
            Y = int.Parse(parts[2]);
            Width = int.Parse(parts[4]);
            Height = int.Parse(parts[5]);
            IsFromServer = true;
        }

        public ushort Graphic { get; }

        public override bool Contains(int x, int y)
        {
            x -= Offset.X;
            y -= Offset.Y;

            var texture0 = GetTexture(0, out var bounds0);
            var texture1 = GetTexture(1, out var bounds1);
            var texture2 = GetTexture(2, out var bounds2);
            var texture3 = GetTexture(3, out var bounds3);
            var texture4 = GetTexture(4, out var bounds4);
            var texture5 = GetTexture(5, out var bounds5);
            var texture6 = GetTexture(6, out var bounds6);
            var texture7 = GetTexture(7, out var bounds7);
            var texture8 = GetTexture(8, out var bounds8);

            int offsetTop = Math.Max(bounds0.Height, bounds2.Height) - bounds1.Height;
            int offsetBottom = Math.Max(bounds5.Height, bounds7.Height) - bounds6.Height;
            int offsetLeft = Math.Abs(Math.Max(bounds0.Width, bounds5.Width) - bounds2.Width);
            int offsetRight = Math.Max(bounds2.Width, bounds7.Width) - bounds4.Width;

            if (PixelsInXY(ref bounds0, Graphic, x, y))
            {
                return true;
            }

            int DW = Width - bounds0.Width - bounds2.Width;

            if (DW >= 1 && PixelsInXY(ref bounds1, (ushort)(Graphic + 1), x - bounds0.Width, y, DW))
            {
                return true;
            }

            if (
                PixelsInXY(
                    ref bounds2,
                    (ushort)(Graphic + 2),
                    x - (Width - bounds2.Width),
                    y - offsetTop
                )
            )
            {
                return true;
            }

            int DH = Height - bounds0.Height - bounds5.Height;

            if (
                DH >= 1
                && PixelsInXY(
                    ref bounds3,
                    (ushort)(Graphic + 3),
                    x /*- offsetLeft*/
                    ,
                    y - bounds0.Height,
                    0,
                    DH
                )
            )
            {
                return true;
            }

            DH = Height - bounds2.Height - bounds7.Height;

            if (
                DH >= 1
                && PixelsInXY(
                    ref bounds4,
                    (ushort)(Graphic + 5),
                    x
                        - (
                            Width - bounds4.Width /*- offsetRight*/
                        ),
                    y - bounds2.Height,
                    0,
                    DH
                )
            )
            {
                return true;
            }

            if (PixelsInXY(ref bounds5, (ushort)(Graphic + 6), x, y - (Height - bounds5.Height)))
            {
                return true;
            }

            DW = Width - bounds5.Width - bounds2.Width;

            if (
                DH >= 1
                && PixelsInXY(
                    ref bounds6,
                    (ushort)(Graphic + 7),
                    x - bounds5.Width,
                    y - (Height - bounds6.Height - offsetBottom),
                    DW
                )
            )
            {
                return true;
            }

            if (
                PixelsInXY(
                    ref bounds7,
                    (ushort)(Graphic + 8),
                    x - (Width - bounds7.Width),
                    y - (Height - bounds7.Height)
                )
            )
            {
                return true;
            }

            DW = Width - bounds0.Width - bounds2.Width;
            DW += offsetLeft + offsetRight;
            DH = Height - bounds2.Height - bounds7.Height;

            if (
                DW >= 1
                && DH >= 1
                && PixelsInXY(
                    ref bounds8,
                    (ushort)(Graphic + 4),
                    x - bounds0.Width,
                    y - bounds0.Height,
                    DW,
                    DH
                )
            )
            {
                return true;
            }

            return false;
        }

        private static bool PixelsInXY(
            ref Rectangle bounds,
            ushort graphic,
            int x,
            int y,
            int width = 0,
            int height = 0
        )
        {
            if (x < 0 || y < 0 || width > 0 && x >= width || height > 0 && y >= height)
            {
                return false;
            }

            if (bounds.Width == 0 || bounds.Height == 0)
            {
                return false;
            }

            int textureWidth = bounds.Width;
            int textureHeight = bounds.Height;

            if (width == 0)
            {
                width = textureWidth;
            }

            if (height == 0)
            {
                height = textureHeight;
            }

            while (x >= textureWidth && width >= textureWidth)
            {
                x -= textureWidth;
                width -= textureWidth;
            }

            if (x < 0 || x > width)
            {
                return false;
            }

            while (y >= textureHeight && height >= textureHeight)
            {
                y -= textureHeight;
                height -= textureHeight;
            }

            if (y < 0 || y > height)
            {
                return false;
            }

            return Client.Game.UO.Gumps.PixelCheck(graphic, x, y);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    if (batcher.ClipBegin(x, y, Width, Height))
                    {
                        Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, Alpha, true);

                        DrawInternal(batcher, x, y, hueVector, layerDepth);
                        RenderLists childRenderLists = new();
                        base.AddToRenderLists(childRenderLists, x, y, ref layerDepth);
                        childRenderLists.DrawRenderLists(batcher, sbyte.MaxValue);

                        batcher.ClipEnd();
                    }

                    return true;
                }
            );

            return true;
        }

        private void DrawInternal(UltimaBatcher2D batcher, int x, int y, Vector3 color, float layerDepth)
        {
            var texture0 = GetTexture(0, out var bounds0);
            var texture1 = GetTexture(1, out var bounds1);
            var texture2 = GetTexture(2, out var bounds2);
            var texture3 = GetTexture(3, out var bounds3);
            var texture4 = GetTexture(4, out var bounds4);
            var texture5 = GetTexture(5, out var bounds5);
            var texture6 = GetTexture(6, out var bounds6);
            var texture7 = GetTexture(7, out var bounds7);
            var texture8 = GetTexture(8, out var bounds8);

            int offsetTop = Math.Max(bounds0.Height, bounds2.Height) - bounds1.Height;
            int offsetBottom = Math.Max(bounds5.Height, bounds7.Height) - bounds6.Height;
            int offsetLeft = Math.Abs(Math.Max(bounds0.Width, bounds5.Width) - bounds2.Width);
            int offsetRight = Math.Max(bounds2.Width, bounds7.Width) - bounds4.Width;

            // PORT DEVIATION (GUO): SuperRes 4x 9-slice (generic server gump
            // frames). HD never applies to gumps, so SR-or-native per piece:
            // corners single, edges/center tiled. Miss enqueues via TryGetSr
            // and stays native; SrCommit warms the Gump SR LRU. LinearClamp
            // on SR runs; toggle-off native. Picking stays native.
            DrawSingleSrOrNative(batcher, 0, texture0, bounds0, x, y, color, layerDepth);

            DrawTiledSrOrNative(
                batcher, 1, texture1, bounds1,
                new Rectangle(
                    x + bounds0.Width,
                    y,
                    Width - bounds0.Width - bounds2.Width,
                    bounds1.Height),
                color, layerDepth);

            DrawSingleSrOrNative(batcher, 2, texture2, bounds2, x + (Width - bounds2.Width), y + offsetTop, color, layerDepth);

            DrawTiledSrOrNative(
                batcher, 3, texture3, bounds3,
                new Rectangle(
                    x,
                    y + bounds0.Height,
                    bounds3.Width,
                    Height - bounds0.Height - bounds5.Height),
                color, layerDepth);

            DrawTiledSrOrNative(
                batcher, 4, texture4, bounds4,
                new Rectangle(
                    x + (Width - bounds4.Width),
                    y + bounds2.Height,
                    bounds4.Width,
                    Height - bounds2.Height - bounds7.Height),
                color, layerDepth);

            DrawSingleSrOrNative(batcher, 5, texture5, bounds5, x, y + (Height - bounds5.Height), color, layerDepth);

            DrawTiledSrOrNative(
                batcher, 6, texture6, bounds6,
                new Rectangle(
                    x + bounds5.Width,
                    y + (Height - bounds6.Height - offsetBottom),
                    Width - bounds5.Width - bounds7.Width,
                    bounds6.Height),
                color, layerDepth);

            DrawSingleSrOrNative(batcher, 7, texture7, bounds7, x + (Width - bounds7.Width), y + (Height - bounds7.Height), color, layerDepth);

            DrawTiledSrOrNative(
                batcher, 8, texture8, bounds8,
                new Rectangle(
                    x + bounds0.Width,
                    y + bounds0.Height,
                    (Width - bounds0.Width - bounds2.Width) + (offsetLeft + offsetRight),
                    Height - bounds2.Height - bounds7.Height),
                color, layerDepth);
        }

        private ushort PieceGraphic(int piece)
        {
            int idx = piece;
            if (idx >= 8)
            {
                idx = 4;
            }
            else if (idx >= 4)
            {
                ++idx;
            }

            return (ushort)(Graphic + idx);
        }

        private void DrawSingleSrOrNative(UltimaBatcher2D batcher, int piece, Texture2D nativeTex, Rectangle nativeBounds, int dx, int dy, Vector3 color, float depth)
        {
            if (nativeBounds.Width > 0 && nativeBounds.Height > 0 &&
                Client.Game.UO.Gumps.TryGetSr(PieceGraphic(piece), out Godot.Texture2D srTex, out Rectangle srUv, out _, out _))
            {
                if (srTex != null && srUv.Width > 0 && srUv.Height > 0)
                {
                    batcher.SetSampler(SamplerState.LinearClamp);
                    batcher.Draw(
                        srTex,
                        new Rectangle(dx, dy, nativeBounds.Width, nativeBounds.Height),
                        srUv,
                        color,
                        depth);
                    batcher.SetSampler(null);
                    return;
                }
            }

            if (nativeTex != null)
            {
                batcher.Draw(nativeTex, new Vector2(dx, dy), nativeBounds, color, depth);
            }
        }

        private void DrawTiledSrOrNative(UltimaBatcher2D batcher, int piece, Texture2D nativeTex, Rectangle nativeBounds, Rectangle dest, Vector3 color, float depth)
        {
            if (dest.Width <= 0 || dest.Height <= 0)
            {
                return;
            }

            if (nativeBounds.Width > 0 && nativeBounds.Height > 0 &&
                Client.Game.UO.Gumps.TryGetSr(PieceGraphic(piece), out Godot.Texture2D srTex, out Rectangle srUv, out int srNatW, out int srNatH))
            {
                if (srTex != null && srUv.Width > 0 && srUv.Height > 0 && srNatW > 0 && srNatH > 0)
                {
                    int tileW = srNatW;
                    int tileH = srNatH;
                    batcher.SetSampler(SamplerState.LinearClamp);
                    for (int oy = 0; oy < dest.Height; oy += tileH)
                    {
                        int dh = System.Math.Min(tileH, dest.Height - oy);
                        int sh = (int)(srUv.Height * (float)dh / tileH);
                        for (int ox = 0; ox < dest.Width; ox += tileW)
                        {
                            int dw = System.Math.Min(tileW, dest.Width - ox);
                            int sw = (int)(srUv.Width * (float)dw / tileW);
                            batcher.Draw(
                                srTex,
                                new Rectangle(dest.X + ox, dest.Y + oy, dw, dh),
                                new Rectangle(srUv.X, srUv.Y, sw, sh),
                                color,
                                depth);
                        }
                    }
                    batcher.SetSampler(null);
                    return;
                }
            }

            if (nativeTex != null)
            {
                batcher.DrawTiled(nativeTex, dest, nativeBounds, color, depth);
            }
        }

        private Texture2D GetTexture(int index, out Rectangle bounds)
        {
            if (index >= 0 && index <= _maxIndex)
            {
                if (index >= 8)
                {
                    index = 4;
                }
                else if (index >= 4)
                {
                    ++index;
                }

                ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(
                    (ushort)(Graphic + index)
                );

                bounds = gumpInfo.UV;
                return gumpInfo.Texture;
            }

            bounds = Rectangle.Empty;
            return null;
        }
    }
}
