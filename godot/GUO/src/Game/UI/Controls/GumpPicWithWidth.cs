// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;
using GUO.Renderer;
using GUO.Compat;
namespace GUO.Game.UI.Controls
{
    internal class GumpPicWithWidth : GumpPic
    {
        public GumpPicWithWidth(int x, int y, ushort graphic, ushort hue, int perc)
            : base(x, y, graphic, hue)
        {
            Percent = perc;
            CanMove = true;
            //AcceptMouseInput = false;
        }

        public int Percent { get; set; }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(Hue);

            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(Graphic);

            var texture = gumpInfo.Texture;
            if (gumpInfo.Texture != null)
            {
                var sourceRectangle = gumpInfo.UV;
                renderLists.AddGumpWithAtlas
                (
                    (batcher) =>
                    {
                        // PORT DEVIATION (GUO): SuperRes 4x bar fill
                        // (health/mana/stamina). SR-or-native tiled into the
                        // Percent-wide dest; LinearClamp on SR.
                        if (Client.Game.UO.Gumps.TryGetSr(Graphic, out Godot.Texture2D srTex, out Rectangle srUv, out int srNatW, out int srNatH))
                        {
                            if (srTex != null && srNatW > 0 && srNatH > 0)
                            {
                                batcher.SetSampler(SamplerState.LinearClamp);
                                for (int oy = 0; oy < Height; oy += srNatH)
                                {
                                    int dh = System.Math.Min(srNatH, Height - oy);
                                    int sh = (int)(srUv.Height * (float)dh / srNatH);
                                    for (int ox = 0; ox < Percent; ox += srNatW)
                                    {
                                        int dw = System.Math.Min(srNatW, Percent - ox);
                                        int sw = (int)(srUv.Width * (float)dw / srNatW);
                                        batcher.Draw(
                                            srTex,
                                            new Rectangle(x + ox, y + oy, dw, dh),
                                            new Rectangle(srUv.X, srUv.Y, sw, sh),
                                            hueVector,
                                            layerDepth);
                                    }
                                }
                                batcher.SetSampler(null);
                                return true;
                            }
                        }
                        batcher.DrawTiled(
                            texture,
                            new Rectangle(x, y, Percent, Height),
                            sourceRectangle,
                            hueVector,
                            layerDepth
                        );
                        return true;
                    }
                );

                return true;
            }

            return false;
        }
    }
}
