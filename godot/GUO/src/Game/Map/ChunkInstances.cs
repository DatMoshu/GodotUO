// SPDX-License-Identifier: BSD-2-Clause

using System;
using Godot;
using GUO.Renderer;

namespace GUO.Game.Map
{
    /// <summary>
    /// A chunk's instanced statics for the world depth pass (ADR-0007, GPU-01):
    /// one instance per static that qualifies, in a MultiMesh per atlas page,
    /// written when the chunk is built and uploaded once per change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GUO addition, not in ClassicUO, standing where upstream's ChunkMesh
    /// vertex buffer stands. The chunk mesh assigns each static its index
    /// (MeshSpriteIndex), contiguous per page; this holds the instance floats
    /// behind those indices and what the tile walk may change per frame: the
    /// hue (highlight, out of range, dead) and whether the static draws from
    /// here at all. A static the walk routes to the queue (fading, under the
    /// circle of transparency, hidden) is given alpha 0 here, which the shader
    /// discards, and is drawn by the sprite path with its own key instead.
    /// </para>
    /// <para>
    /// Nothing here is per frame unless something changed: a page re-uploads
    /// its whole buffer, at the pass's End, when any of its instances did.
    /// </para>
    /// </remarks>
    internal sealed class ChunkInstances
    {
        private const int Stride = WorldDepthPass.Stride;

        private WorldDepthPass.ChunkSet _set;
        private int _generation = -1;

        private WorldDepthPass.InstanceBuffer[] _pages = Array.Empty<WorldDepthPass.InstanceBuffer>();
        private int _pageCount;

        // Per record: which page, where in it, the tile slot, and the drawn state.
        private int[] _recordPage = Array.Empty<int>();
        private int[] _recordAt = Array.Empty<int>();
        private byte[] _recordSlot = Array.Empty<byte>();
        private bool[] _recordDrawn = Array.Empty<bool>();
        private int _count;

        /// <summary>True while the pass these instances were written for still exists.</summary>
        public bool Valid => _set != null && !_set.Disposed && _generation == GpuWorld.Generation && GpuWorld.Pass != null;

        public int Count => _count;

        /// <summary>
        /// Starts a build: one page per texture in <paramref name="pages"/>,
        /// with room for <paramref name="counts"/> instances each. Indices
        /// follow the chunk mesh's prefix sums, so record i lands in the page
        /// whose range holds i.
        /// </summary>
        public void Begin(WorldDepthPass pass, ReadOnlySpan<Texture2D> pages, ReadOnlySpan<int> counts, int total)
        {
            if (_set == null || _set.Disposed || _generation != GpuWorld.Generation)
            {
                _set?.Dispose();
                _set = pass.CreateChunkSet();
                _generation = GpuWorld.Generation;
            }

            if (_pages.Length < pages.Length)
            {
                Array.Resize(ref _pages, pages.Length);
            }

            _pageCount = pages.Length;
            _set.Trim(_pageCount);
            if (_recordPage.Length < total)
            {
                Array.Resize(ref _recordPage, total);
                Array.Resize(ref _recordAt, total);
                Array.Resize(ref _recordSlot, total);
                Array.Resize(ref _recordDrawn, total);
            }

            _count = total;
            int at = 0;
            for (int p = 0; p < pages.Length; p++)
            {
                WorldDepthPass.InstanceBuffer buffer = _set.Page(p, pages[p]);
                buffer.Count = 0;
                buffer.Reserve(counts[p]).Clear();
                buffer.Dirty = true;
                _pages[p] = buffer;
                for (int i = 0; i < counts[p]; i++, at++)
                {
                    _recordPage[at] = p;
                    _recordAt[at] = i;
                    _recordDrawn[at] = true;
                }
            }
        }

        /// <summary>
        /// Writes one static's instance: the quad's absolute corners (world
        /// pixels before the draw offset, integers), its source rect in
        /// texels, the hue as GetHueVector built it, and its key parts.
        /// </summary>
        public void Write(int index, int posX, int posY, int width, int height, in Compat.Rectangle source,
            Vector3 hue, int x, int y, int z, int slot, uint depthAbs)
        {
            int page = _recordPage[index];
            Span<float> f = _pages[page].Instances.Slice(_recordAt[index] * Stride, Stride);
            var p0 = new Vector2(posX, posY);
            var p1 = new Vector2(posX + width, posY);
            var p2 = new Vector2(posX, posY + height);
            var p3 = new Vector2(posX + width, posY + height);
            WorldDepthPass.Pack(f, p0, p1, p2, p3, depthAbs, WorldDepthKey.TieAbs(x, y, slot), x + y, z, x - y,
                WorldDepthPass.StaticFlags, hue, 0f,
                new Vector4(source.X, source.Y, source.Width, source.Height));
            _recordSlot[index] = (byte)slot;
            _recordDrawn[index] = true;
        }

        public int Slot(int index) => _recordSlot[index];

        /// <summary>The hue the tile walk gives the static this frame; a change re-uploads its page.</summary>
        public void SetHue(int index, float hueX, float hueY)
        {
            int page = _recordPage[index];
            WorldDepthPass.InstanceBuffer buffer = _pages[page];
            Span<float> f = buffer.Instances.Slice(_recordAt[index] * Stride, Stride);
            if (f[12] == hueX && f[13] == hueY)
            {
                return;
            }

            f[12] = hueX;
            f[13] = hueY;
            buffer.Dirty = true;
        }

        /// <summary>
        /// Whether the static draws from its instance this frame. Off gives
        /// the instance alpha 0, which the shader discards; the sprite path
        /// draws the object instead, with the same key.
        /// </summary>
        public void SetDrawn(int index, bool drawn)
        {
            if (_recordDrawn[index] == drawn)
            {
                return;
            }

            _recordDrawn[index] = drawn;
            int page = _recordPage[index];
            WorldDepthPass.InstanceBuffer buffer = _pages[page];
            Span<float> f = buffer.Instances.Slice(_recordAt[index] * Stride, Stride);
            f[14] = drawn ? 1f : 0f;
            buffer.Dirty = true;
        }

        public bool IsDrawn(int index) => _recordDrawn[index];

        /// <summary>The chunk is in the walk's range this frame; the pass uploads what changed at its End.</summary>
        public void Show()
        {
            if (Valid)
            {
                _set.Pass.Show(_set);
            }
        }

        public void Clear()
        {
            _set?.Dispose();
            _set = null;
            _count = 0;
            _pageCount = 0;
        }
    }
}
