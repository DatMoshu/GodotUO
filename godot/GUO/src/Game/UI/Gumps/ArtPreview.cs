// SPDX-License-Identifier: BSD-2-Clause

using GUO.Assets;

namespace GUO.Game.UI.Gumps
{
    /// <summary>Local native-art preview for manual staged-splat placement.</summary>
    internal static class ArtPreview
    {
        internal static byte[] ExtractPng(ushort graphic, bool isLand)
        {
            uint index = isLand ? graphic : 0x4000u + graphic;
            ArtInfo art = Client.Game.UO.FileManager.Arts.GetArt(index);
            if (art.Width <= 0 || art.Height <= 0 || art.Pixels.Length < art.Width * art.Height)
                return null;

            byte[] rgba = new byte[art.Width * art.Height * 4];
            for (int i = 0; i < art.Width * art.Height; i++)
            {
                uint pixel = art.Pixels[i];
                int at = i * 4;
                rgba[at] = (byte)pixel;
                rgba[at + 1] = (byte)(pixel >> 8);
                rgba[at + 2] = (byte)(pixel >> 16);
                rgba[at + 3] = pixel == 0 ? (byte)0 : (byte)255;
            }
            using Godot.Image image = Godot.Image.CreateFromData(
                art.Width, art.Height, false, Godot.Image.Format.Rgba8, rgba);
            return image.SavePngToBuffer();
        }
    }
}
