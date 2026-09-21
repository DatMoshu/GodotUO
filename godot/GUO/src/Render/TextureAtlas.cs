// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using StbRectPackSharp;

// Compat's Rectangle is the one the ported signature returns. Godot's Rect2I
// is the one the blit takes. Both are in scope; say which is meant.
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Renderer
{
    /// <summary>
    /// Packs many small UO sprites into a few large textures, so the renderer
    /// switches texture rarely. The packing is upstream's, unchanged; what
    /// changes is how a packed sprite reaches the GPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO) — uploads are deferred to one per page per frame.
    /// Upstream uploads just the packed rectangle through FNA's
    /// <c>SetDataPointerEXT</c>. Godot has no partial texture upload at all:
    /// <c>ImageTexture.Update</c> and <c>RenderingServer.Texture2DUpdate</c>
    /// both replace the whole image. Uploading per sprite would therefore mean
    /// rewriting the entire page every time one 44x44 tile is decoded.
    /// </para>
    /// <para>
    /// Measured by <c>launchers\dev\atlas_probe.bat</c>, cost of adding one
    /// 44x44 sprite:
    /// </para>
    /// <code>
    ///    512 x  512    1 MB    blit 0.9us    upload    359us
    ///   1024 x 1024    4 MB    blit 0.7us    upload  1,676us
    ///   2048 x 2048   16 MB    blit 1.2us    upload  1,909us
    ///   4096 x 4096   64 MB    blit 3.2us    upload 11,377us
    /// </code>
    /// <para>
    /// Upstream's page size is 4096, which on Godot would be eleven
    /// milliseconds — a dropped frame — for every sprite decoded, and a walk
    /// across a map decodes many per second. Two things follow from the
    /// numbers. The blit into the CPU-side page is free, so sprites are
    /// blitted as they arrive and the page is uploaded at most once a frame,
    /// by <see cref="FlushAll"/>. And the page is capped at
    /// <see cref="MaxPageSize"/>: 2048 measured no dearer than 1024 while
    /// needing a quarter as many pages, and 4096 is six times dearer again.
    /// </para>
    /// <para>
    /// PORT DEVIATION (GUO) — the constructor drops upstream's
    /// <c>GraphicsDevice</c> and <c>SurfaceFormat</c>. Godot has no device,
    /// and every caller asks for <c>SurfaceFormat.Color</c>, which is
    /// <c>Rgba8</c>.
    /// </para>
    /// </remarks>
    public sealed class TextureAtlas : IDisposable
    {
        /// <summary>
        /// The largest page this will allocate, whatever a caller asks for.
        /// See the cost table above.
        /// </summary>
        public const int MaxPageSize = 2048;

        // Every live atlas, so one call a frame can flush all of them without
        // each owner having to remember to. An atlas whose page is stale draws
        // sprites that are not there yet, which looks like a decode bug and is
        // not one.
        private static readonly List<TextureAtlas> _live = new List<TextureAtlas>();

        private readonly int _width;
        private readonly int _height;

        private readonly List<Image> _pages = new List<Image>();
        private readonly List<ImageTexture> _textures = new List<ImageTexture>();
        private readonly List<bool> _dirty = new List<bool>();

        private Packer _packer;

        public TextureAtlas(int width, int height)
        {
            _width = Math.Min(width, MaxPageSize);
            _height = Math.Min(height, MaxPageSize);

            _live.Add(this);
        }

        public int TexturesCount => _textures.Count;

        /// <summary>
        /// Uploads every page that has changed since the last call. Cheap when
        /// nothing has: the whole point is that it collapses a frame's worth of
        /// decoded sprites into one upload per page.
        /// </summary>
        public static void FlushAll()
        {
            for (int i = 0; i < _live.Count; i++)
            {
                _live[i].Flush();
            }
        }

        public void Flush()
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                if (_dirty[i])
                {
                    _textures[i].Update(_pages[i]);
                    _dirty[i] = false;
                }
            }
        }

        public Texture2D AddSprite(
            ReadOnlySpan<uint> pixels,
            int width,
            int height,
            out Rectangle pr
        )
        {
            int index = _textures.Count - 1;

            if (index < 0)
            {
                index = 0;
                CreateNewPage();
            }

            while (!_packer.PackRect(width, height, out pr))
            {
                CreateNewPage();
                index = _textures.Count - 1;
            }

            // A uint here is 0xAABBGGRR, so its bytes in memory are already
            // R,G,B,A -- the order Rgba8 wants. Same reasoning as the art
            // decoder in UoDataProbe and SolidColorTextureCache.
            var rgba = new byte[width * height * 4];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Slice(0, width * height))
                .CopyTo(rgba);

            Image sprite = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);

            _pages[index].BlitRect(
                sprite,
                new Rect2I(0, 0, width, height),
                new Vector2I(pr.X, pr.Y));

            // Not uploaded here. See the remarks: the upload is the expensive
            // half and it happens once a frame, in Flush.
            _dirty[index] = true;

            return _textures[index];
        }

        private void CreateNewPage()
        {
            GUO.Utility.Logging.Log.Trace($"creating atlas page: {_width}x{_height}");

            Image page = Image.CreateEmpty(_width, _height, false, Image.Format.Rgba8);

            _pages.Add(page);
            _textures.Add(ImageTexture.CreateFromImage(page));
            _dirty.Add(false);

            _packer?.Dispose();
            _packer = new Packer(_width, _height);
        }

        public void SaveImages(string name)
        {
            DirAccess.MakeDirRecursiveAbsolute("user://atlas");

            for (int i = 0, count = _pages.Count; i < count; ++i)
            {
                _pages[i].SavePng($"user://atlas/{name}_atlas_{i}.png");
            }
        }

        public void Dispose()
        {
            _live.Remove(this);

            // Godot reference-counts both, so letting go of the last reference
            // is what frees them.
            _pages.Clear();
            _textures.Clear();
            _dirty.Clear();

            _packer?.Dispose();
            _packer = null;
        }
    }
}
