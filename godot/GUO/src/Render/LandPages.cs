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

        public static Texture2DArray Array { get; private set; }

        /// <summary>A page was added since the last <see cref="Sync"/>.</summary>
        public static bool Dirty => _rebuild;

        /// <summary>Layer copies made, for the perf probe and PerfDump.</summary>
        public static int Uploads;

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

            if (_rebuild || Array == null)
            {
                var images = new Godot.Collections.Array<Image>();
                for (int i = 0; i < _layers.Count; i++)
                {
                    // A page gone from the atlas (never while the atlases live:
                    // they are only disposed whole, which resets this) is a blank layer.
                    images.Add(TextureAtlas.TryGetPage(_layers[i], out Image page, out int version)
                        ? Padded(page)
                        : Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8));
                    _versions[i] = version;
                }

                Array ??= new Texture2DArray();
                Array.CreateFromImages(images);
                Uploads += _layers.Count;
                _rebuild = false;
                return;
            }

            for (int i = 0; i < _layers.Count; i++)
            {
                if (TextureAtlas.TryGetPage(_layers[i], out Image page, out int version) && version != _versions[i])
                {
                    Array.UpdateLayer(Padded(page), i);
                    _versions[i] = version;
                    Uploads++;
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
