// GUO-owned (ADR-0023): the id a world object has in the post-processing id buffer.

using GUO.Assets;
using GUO.Game.GameObjects;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// A 24-bit, never-zero id per world object, written as a colour into the
    /// id buffer so an outline pass can tell one object's pixels from another's.
    /// An entity keeps its serial's low 24 bits; a static (which has no serial)
    /// is hashed from its graphic and position, so two statics share an id only
    /// by a rare collision, which at worst loses one outline between them.
    /// A flat walkable tile (a floor, a paved road) is ground, as land is: it
    /// returns -1, background, so floors are not outlined tile by tile.
    /// </summary>
    internal static class PostFxIds
    {
        public static int Of(GameObject o)
        {
            if (o is Static st && Flat(ref st.ItemData) || o is Multi mu && Flat(ref mu.ItemData))
            {
                return -1;
            }

            uint h = o is Entity e
                ? e.Serial
                : (uint)o.Graphic * 73856093u ^ (uint)o.X * 19349663u ^ (uint)o.Y * 83492791u ^ (uint)(o.Z + 128) * 2654435761u;
            int id = (int)(h & 0xFFFFFF);
            return id == 0 ? 1 : id;
        }

        private static bool Flat(ref StaticTiles data) => data.IsSurface && data.Height == 0;
    }
}
