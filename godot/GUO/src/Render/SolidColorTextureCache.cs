// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;

// Both namespaces define Color and they are not interchangeable: Compat's is
// the byte-packed XNA one the ported signature uses, Godot's is four floats.
// Spell out which one this file means rather than importing the ambiguity.
using Color = GUO.Compat.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// One-pixel textures in flat colours, kept so the same colour is only
    /// ever built once. The UI draws rules, borders and fills by stretching
    /// these.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream takes a <c>GraphicsDevice</c> in
    /// <c>Initialize</c> and passes it to every texture it creates. Godot
    /// allocates textures through the rendering server with no device handle
    /// to thread, so <c>Initialize</c> is gone and the single call site in
    /// GameController drops with it. Nothing else about the type changes:
    /// <c>GetTexture(Color)</c> keeps its name, signature and caching
    /// behaviour, which is all any caller uses.
    /// </remarks>
    public static class SolidColorTextureCache
    {
        private static readonly Dictionary<Color, Texture2D> _textures =
            new Dictionary<Color, Texture2D>();

        public static Texture2D GetTexture(Color color)
        {
            if (_textures.TryGetValue(color, out Texture2D texture))
            {
                return texture;
            }

            // Compat.Color packs as 0xAABBGGRR, so the bytes in memory are
            // already R,G,B,A -- the order Rgba8 wants. Same reasoning as the
            // art decoder in UoDataProbe.
            byte[] rgba = { color.R, color.G, color.B, color.A };

            Image image = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, rgba);
            texture = ImageTexture.CreateFromImage(image);

            _textures[color] = texture;

            return texture;
        }

        /// <summary>
        /// Drops every cached texture. Godot reference-counts them, so letting
        /// go of the last reference is what frees them.
        /// </summary>
        public static void Clear()
        {
            _textures.Clear();
        }
    }
}
