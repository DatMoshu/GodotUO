// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// Epic B, B4 fix 1b (--merged-land=array): the atlas pages land is drawn
    /// from, as the layers of one <see cref="Texture2DArray"/>, so all visible
    /// land can be one mesh in its original draw order.
    /// </summary>
    /// <remarks>
    /// GUO addition, not in ClassicUO (ADR-0007's step 1, scoped to land). A
    /// canvas mesh binds one texture, so land from several pages was one draw
    /// call per page run; with the pages as layers the texture never changes.
    /// Every layer is <see cref="TextureAtlas.MaxPageSize"/> square: a smaller
    /// page is copied into the corner and its UVs scaled by
    /// <see cref="ScaleOf"/>, which therefore never changes once a mesh is
    /// built. A layer is re-copied when its atlas page was uploaded again
    /// (<see cref="TextureAtlas.TryGetPage"/>'s version). Sampled nearest
    /// (project rule 7) by the land-array shader.
    /// </remarks>
    internal static class LandPages
    {
        private const int Size = TextureAtlas.MaxPageSize;

        private static readonly List<Texture2D> _layers = new();
        private static readonly List<int> _versions = new();
        private static readonly Dictionary<Texture2D, int> _index = new();
        private static bool _rebuild;

        /// <summary>
        /// Layers the array was created with: a power of two, at least
        /// <see cref="MinCapacity"/>, the spares blank. A Texture2DArray's layer
        /// count is fixed at creation, so a page added within it is one layer
        /// copy, not a copy of every layer; only outgrowing it rebuilds.
        /// </summary>
        private static int _capacity;

        private const int MinCapacity = 4;

        public static Texture2DArray Array { get; private set; }

        /// <summary>A page was added since the last <see cref="Sync"/>.</summary>
        public static bool Dirty => _rebuild;

        /// <summary>Layer copies made, for the perf probe and PerfDump.</summary>
        public static int Uploads;

        /// <summary>
        /// Of <see cref="Uploads"/>: copies of every layer because a page was
        /// added (Rebuilds), and copies of one layer because its page was
        /// uploaded again (Bumps) -- something new was packed onto it.
        /// </summary>
        public static int Rebuilds, Bumps;

        /// <summary>Whether <paramref name="texture"/> is a layer already.</summary>
        public static bool Holds(Texture2D texture) => texture != null && _index.ContainsKey(texture);

        /// <summary>Layers the array holds, spares included: what it takes on the GPU is this times <see cref="LayerBytes"/>.</summary>
        public static int Capacity => _capacity;

        /// <summary>Layers in the array (pages land has been drawn from).</summary>
        public static int Layers => _layers.Count;

        /// <summary>What one layer copy moves: a full RGBA8 page.</summary>
        public const long LayerBytes = (long)Size * Size * 4;

        /// <summary>The layer of an atlas page, adding it if new; -1 when it is not an atlas page that fits.</summary>
        public static int LayerOf(Texture2D texture)
        {
            if (_index.TryGetValue(texture, out int layer))
            {
                return layer;
            }

            if (!TextureAtlas.TryGetPage(texture, out Image page, out _) || page.GetWidth() > Size || page.GetHeight() > Size)
            {
                return -1;
            }

            layer = _layers.Count;
            _layers.Add(texture);
            _versions.Add(-1);
            _index[texture] = layer;
            _rebuild = true;
            return layer;
        }

        public static Vector2 ScaleOf(Texture2D texture) =>
            new((float)texture.GetWidth() / Size, (float)texture.GetHeight() / Size);

        /// <summary>Brings the array up to date with its pages; once a frame, before land draws.</summary>
        public static void Sync()
        {
            if (_layers.Count == 0)
            {
                return;
            }

            if (Array == null || _layers.Count > _capacity)
            {
                _capacity = MinCapacity;
                while (_capacity < _layers.Count)
                {
                    _capacity *= 2;
                }

                var images = new Godot.Collections.Array<Image>();
                for (int i = 0; i < _capacity; i++)
                {
                    // A page gone from the atlas (never while the atlases live:
                    // they are only disposed whole, which resets this) is a
                    // blank layer, as is every spare.
                    if (i < _layers.Count && TextureAtlas.TryGetPage(_layers[i], out Image page, out int version))
                    {
                        images.Add(Padded(page));
                        _versions[i] = version;
                    }
                    else
                    {
                        images.Add(Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8));
                    }
                }

                Array ??= new Texture2DArray();
                Array.CreateFromImages(images);
                Uploads += _capacity;
                Rebuilds += _capacity;
                _rebuild = false;
                return;
            }

            // A page added within the capacity has version -1 here, so the
            // loop below copies it into its blank layer.
            _rebuild = false;

            for (int i = 0; i < _layers.Count; i++)
            {
                if (TextureAtlas.TryGetPage(_layers[i], out Image page, out int version) && version != _versions[i])
                {
                    Array.UpdateLayer(Padded(page), i);
                    _versions[i] = version;
                    Uploads++;
                    Bumps++;
                }
            }
        }

        /// <summary>
        /// Forget every page and free the array: the atlases were disposed
        /// (<see cref="TextureAtlas.DisposeAll"/>, an embedded unload), so the
        /// textures held here are dead and the next load starts again.
        /// </summary>
        public static void Reset()
        {
            _layers.Clear();
            _versions.Clear();
            _index.Clear();
            _rebuild = false;
            _capacity = 0;
            Array?.Dispose();
            Array = null;
        }

        private static Image Padded(Image page)
        {
            if (page.GetWidth() == Size && page.GetHeight() == Size)
            {
                return page;
            }

            Image full = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
            full.BlitRect(page, new Rect2I(0, 0, page.GetWidth(), page.GetHeight()), Vector2I.Zero);
            return full;
        }
    }
}
