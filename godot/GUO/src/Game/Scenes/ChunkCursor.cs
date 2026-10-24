// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Game.Scenes
{
    /// <summary>Something that hands out the chunk at chunk coordinates, or null when it is not loaded.</summary>
    internal interface IChunkSource<out T> where T : class
    {
        T ChunkAt(int chunkX, int chunkY);
    }

    /// <summary>
    /// The chunk a walk over tiles is in, looked up again only when the walk
    /// leaves it. Eight neighbours in a row share one lookup; a chunk that
    /// is not loaded is remembered as null for as long as the walk stays in
    /// it. No engine types, so tools/render_sort_tests checks it alone.
    /// </summary>
    internal struct ChunkCursor<T> where T : class
    {
        private T _chunk;
        private int _chunkX, _chunkY;
        private bool _held;

        /// <summary>The chunk holding tile (x, y), or null for a negative coordinate or an unloaded chunk.</summary>
        public T At<TSource>(int x, int y, TSource source) where TSource : IChunkSource<T>
        {
            if (x < 0 || y < 0)
            {
                return null;
            }

            int cx = x >> 3, cy = y >> 3;

            if (!_held || cx != _chunkX || cy != _chunkY)
            {
                _chunk = source.ChunkAt(cx, cy);
                _chunkX = cx;
                _chunkY = cy;
                _held = true;
            }

            return _chunk;
        }
    }
}
