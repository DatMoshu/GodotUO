// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;

namespace GUO.Game.UI.Controls
{
    internal class ScissorControl : Control
    {
        public ScissorControl(bool enabled, int x, int y, int width, int height) : this(enabled)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public ScissorControl(bool enabled)
        {
            CanMove = false;
            AcceptMouseInput = false;
            AcceptKeyboardInput = false;
            Alpha = 1.0f;
            WantUpdateSize = false;
            DoScissor = enabled;
        }

        public bool DoScissor;

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            bool clipIt(Renderer.UltimaBatcher2D batcher)
            {
                if (DoScissor)
                {
                    batcher.ClipBegin(x, y, Width, Height);
                }
                else
                {
                    batcher.ClipEnd();
                }
                return true;
            }

            // PORT DEVIATION (GUO): upstream queues the clip twice, once into
            // each of its two gump queues, because each is flushed as its own
            // pass and both have to be clipped. RenderLists keeps one queue
            // here -- see the note on it -- and queueing twice would begin the
            // clip, then begin it again, or end an ended one.
            renderLists.AddGumpNoAtlas(clipIt);

            return true;
        }
    }
}