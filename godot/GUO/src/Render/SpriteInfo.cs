// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Compat;

namespace GUO.Renderer
{
    /// <summary>
    /// One sprite's place in an atlas: the page it lives on, its rectangle
    /// within that page, and the offset that positions it in the world.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream's <c>Texture2D</c> is FNA's; this one is
    /// Godot's. The field keeps its name and its meaning -- the atlas page the
    /// sprite was packed into -- so every user reads unchanged. Nothing else
    /// about the struct moves.
    ///
    /// Deliberately still a bare texture plus a UV rectangle rather than a
    /// Godot <c>AtlasTexture</c>. The atlas is built by the port's own packer
    /// and a great many sprites share one page; allocating a Resource per
    /// sprite to say the same thing would cost more than it explains.
    /// </remarks>
    public struct SpriteInfo
    {
        public Texture2D Texture;
        public Rectangle UV;
        public Point Center;

        public static readonly SpriteInfo Empty = new SpriteInfo { Texture = null };
    }
}
