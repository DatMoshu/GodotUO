// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Scenes;
using GUO.Renderer;
using GUO.Utility;
using GUO.Compat;

namespace GUO.Game.UI.Controls
{
    /// <summary>
    /// A gump cell that draws an arbitrary PNG (authoring previews): the
    /// texture is fitted inside the control, aspect kept, centred. Null or
    /// undecodable bytes draw nothing.
    /// </summary>
    internal class PreviewPic : Control
    {
        private Godot.Texture2D _texture;

        /// <summary>Main thread: Godot textures cannot be made on workers.</summary>
        public void SetPng(byte[] png)
        {
            _texture = null;
            if (png == null || png.Length == 0)
            {
                return;
            }

            using var image = new Godot.Image();
            if (image.LoadPngFromBuffer(png) != Godot.Error.Ok)
            {
                return;
            }

            if (image.GetFormat() != Godot.Image.Format.Rgba8)
            {
                image.Convert(Godot.Image.Format.Rgba8);
            }

            _texture = Godot.ImageTexture.CreateFromImage(image);
        }

        public bool HasImage => _texture != null;

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            Godot.Texture2D texture = _texture;
            if (texture != null)
            {
                int tw = texture.GetWidth(), th = texture.GetHeight();
                float s = System.Math.Min(Width / (float)tw, Height / (float)th);
                int dw = System.Math.Max(1, (int)(tw * s));
                int dh = System.Math.Max(1, (int)(th * s));
                var dest = new Rectangle(x + (Width - dw) / 2, y + (Height - dh) / 2, dw, dh);
                var source = new Rectangle(0, 0, tw, th);
                Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, 1);
                renderLists.AddGumpWithAtlas
                (
                    (batcher) =>
                    {
                        batcher.Draw(texture, dest, source, hueVector, layerDepth);
                        return true;
                    }
                );
            }

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }
}
