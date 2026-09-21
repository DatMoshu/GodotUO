// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;
using GUO.Renderer;
using GUO.Compat;
using System;
using System.Collections.Generic;

namespace GUO.Game.UI.Controls
{
    internal class CheckerTrans : Control
    {
        // PORT GAP (GUO): upstream builds two states here -- a stencil that
        // writes 1 everywhere it draws, and a blend that writes no colour --
        // to punch a checkerboard hole through what is behind the gump. Every
        // call site for both is already commented out upstream, so they are
        // dead code there and would not compile here: ADR-0002's
        // DepthStencilState is a closed set of the states the client actually
        // uses, and the 2D canvas has no stencil buffer to set. They are left
        // out rather than translated, and the drawing below -- which is what
        // upstream actually runs -- is unchanged.

        //public CheckerTrans(float alpha = 0.5f)
        //{
        //    _alpha = alpha;
        //    AcceptMouseInput = false;
        //}

        public CheckerTrans(List<string> parts)
        {
            X = int.Parse(parts[1]);
            Y = int.Parse(parts[2]);
            Width = int.Parse(parts[3]);
            Height = int.Parse(parts[4]);
            AcceptMouseInput = false;
            IsFromServer = true;
        }


        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            //batcher.SetBlendState(_checkerBlend.Value);
            //batcher.SetStencil(_checkerStencil.Value);

            //batcher.Draw2D(TransparentTexture, new Rectangle(position.X, position.Y, Width, Height), Vector3.Zero /*ShaderHueTranslator.GetHueVector(0, false, 0.5f, false)*/);

            //batcher.SetBlendState(null);
            //batcher.SetStencil(null);

            //return true;

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, 0.5f);

            //batcher.SetStencil(_checkerStencil.Value);

            renderLists.AddGumpNoAtlas
            (
                (batcher) =>
                {
                    batcher.Draw
                    (
                        SolidColorTextureCache.GetTexture(Color.Black),
                        new Rectangle
                        (
                            x,
                            y,
                            Width,
                            Height
                        ),
                        hueVector,
                        layerDepth
                    );

                    return true;
                }
            );

            

            //batcher.SetStencil(null);
            return true;
        }
    }
}