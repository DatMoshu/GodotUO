// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;
using GUO.Input;
using GUO.Network;
using GUO.Renderer;
using GUO.Utility;
using GUO.Compat;
using System.Collections.Generic;

namespace GUO.Game.UI.Controls
{
    internal abstract class GumpPicBase : Control
    {
        private ushort _graphic;

        protected GumpPicBase()
        {
            CanMove = true;
            AcceptMouseInput = true;
        }

        public ushort Graphic
        {
            get => _graphic;
            set
            {
                _graphic = value;

                ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(_graphic);

                if (gumpInfo.Texture == null)
                {
                    Dispose();

                    return;
                }

                Width = gumpInfo.UV.Width;
                Height = gumpInfo.UV.Height;
            }
        }

        public ushort Hue { get; set; }
        public bool IsPartialHue { get; set; }


        public override bool Contains(int x, int y)
        {
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(_graphic);

            if (gumpInfo.Texture == null)
            {
                return false;
            }

            if (Client.Game.UO.Gumps.PixelCheck(Graphic, x - Offset.X, y - Offset.Y))
            {
                return true;
            }

            for (int i = 0; i < Children.Count; i++)
            {
                Control c = Children[i];

                // might be wrong x, y. They should be calculated by position
                if (c.Contains(x, y))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal class GumpPic : GumpPicBase
    {
        public GumpPic(int x, int y, ushort graphic, ushort hue)
        {
            X = x;
            Y = y;
            Graphic = graphic;
            Hue = hue;
            IsFromServer = true;
        }

        public GumpPic(List<string> parts)
            : this(
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                UInt16Converter.Parse(parts[3]),
                (ushort)(
                    parts.Count > 4
                        ? TransformHue(
                            (ushort)(
                                UInt16Converter.Parse(parts[4].Substring(parts[4].IndexOf('=') + 1))
                                + 1
                            )
                        )
                        : 0
                )
            )
        { }

        public bool ContainsByBounds { get; set; }

        public override bool Contains(int x, int y)
        {
            return ContainsByBounds || base.Contains(x, y);
        }

        private static ushort TransformHue(ushort hue)
        {
            if (hue <= 2)
            {
                hue = 0;
            }

            //if (hue < 2)
            //    hue = 1;
            return hue;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            if (IsDisposed)
            {
                return false;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(Hue, IsPartialHue, Alpha, true);

            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(Graphic);

            if (gumpInfo.Texture != null)
            {
                var texture = gumpInfo.Texture;
                var sourceRectangle = gumpInfo.UV;
                renderLists.AddGumpWithAtlas(
                    batcher =>
                    {
                        // PORT DEVIATION (GUO): SuperRes 4x for gump pics
                        // (paperdoll body/equipment, container chrome). HD
                        // never applies to gumps, so SR-or-native. Miss
                        // enqueues the main-thread copy via TryGetSr and stays
                        // native; SrCommit warms the Gump SR LRU. Toggle-off
                        // stays byte-identical. Picking stays native.
                        if (Client.Game.UO.Gumps.TryGetSr(Graphic, out Godot.Texture2D srTex, out Rectangle srUv, out _, out _))
                        {
                            if (srTex != null)
                            {
                                batcher.SetSampler(SamplerState.LinearClamp);
                                batcher.Draw(
                                    srTex,
                                    new Rectangle(x, y, Width, Height),
                                    srUv,
                                    hueVector,
                                    layerDepth
                                );
                                batcher.SetSampler(null);
                                return true;
                            }
                        }
                        batcher.Draw(
                            texture,
                            new Rectangle(x, y, Width, Height),
                            sourceRectangle,
                            hueVector,
                            layerDepth
                        );
                        return true;
                    });
            }

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }

    internal class VirtueGumpPic : GumpPic
    {
        private readonly World _world;

        public VirtueGumpPic(World world, List<string> parts) : base(parts)
        {
            _world = world;
        }

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                NetClient.Socket.Send_VirtueGumpResponse(_world.Player, Graphic);

                return true;
            }

            return base.OnMouseDoubleClick(x, y, button);
        }
    }

    internal class GumpPicInPic : GumpPicBase
    {
        private readonly Rectangle _picInPicBounds;

        public GumpPicInPic(
            int x,
            int y,
            ushort graphic,
            ushort sx,
            ushort sy,
            ushort width,
            ushort height
        )
        {
            X = x;
            Y = y;
            Graphic = graphic;
            Width = width;
            Height = height;
            _picInPicBounds = new Rectangle(sx, sy, Width, Height);
            IsFromServer = true;
        }

        public GumpPicInPic(List<string> parts)
            : this(
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                UInt16Converter.Parse(parts[3]),
                UInt16Converter.Parse(parts[4]),
                UInt16Converter.Parse(parts[5]),
                UInt16Converter.Parse(parts[6]),
                UInt16Converter.Parse(parts[7])
            )
        { }

        public override bool Contains(int x, int y)
        {
            return true;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            if (IsDisposed)
            {
                return false;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(Hue, IsPartialHue, Alpha, true);

            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(Graphic);

            var sourceBounds = new Rectangle(gumpInfo.UV.X + _picInPicBounds.X, gumpInfo.UV.Y + _picInPicBounds.Y, _picInPicBounds.Width, _picInPicBounds.Height);

            var texture = gumpInfo.Texture;
            if (texture != null)
            {
                renderLists.AddGumpWithAtlas(
                    batcher =>
                    {
                        // PORT DEVIATION (GUO): SuperRes 4x sub-rect (same
                        // SR-or-native rule as GumpPic). The SR source is the
                        // proportional sub-rect: scale = sr/native.
                        if (Client.Game.UO.Gumps.TryGetSr(Graphic, out Godot.Texture2D srTex, out Rectangle srUv, out int srNatW, out int srNatH))
                        {
                            if (srTex != null && srNatW > 0 && srNatH > 0)
                            {
                                float scX = (float)srUv.Width / srNatW;
                                float scY = (float)srUv.Height / srNatH;
                                var srSub = new Rectangle(
                                    srUv.X + (int)(_picInPicBounds.X * scX),
                                    srUv.Y + (int)(_picInPicBounds.Y * scY),
                                    (int)(_picInPicBounds.Width * scX),
                                    (int)(_picInPicBounds.Height * scY));
                                batcher.SetSampler(SamplerState.LinearClamp);
                                batcher.Draw(
                                    srTex,
                                    new Rectangle(x, y, Width, Height),
                                    srSub,
                                    hueVector,
                                    layerDepth
                                );
                                batcher.SetSampler(null);
                                return true;
                            }
                        }
                        batcher.Draw(
                            texture,
                            new Rectangle(x, y, Width, Height),
                            sourceBounds,
                            hueVector,
                            layerDepth
                        );
                        return true;
                    }
                );
            }

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }
}
