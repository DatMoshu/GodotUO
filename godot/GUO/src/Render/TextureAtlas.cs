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
        private int _packingPageIndex = -1;

        public TextureAtlas(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Atlas dimensions must be positive.");

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


        /// <summary>
        /// Copies a region of whichever atlas page <paramref name="texture"/>
        /// is, into <paramref name="dest"/>.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream reads and writes the texture itself,
        /// through XNA's Texture2D.GetData and SetDataPointerEXT. A Godot
        /// texture is write-only from the CPU side and reading one back means
        /// a GPU round trip -- but the atlas already keeps every page as an
        /// Image, so the data never has to leave the CPU. The one caller is
        /// MiniMapGump, which paints the map into its own gump's atlas region
        /// and needs the untouched gump back to start from.
        ///
        /// Returns false when the texture is not an atlas page, so a caller
        /// that is handed a standalone texture finds out rather than silently
        /// reading zeroes.
        /// </remarks>
        public static bool TryReadRegion(Texture2D texture, Rectangle region, Span<uint> dest)
        {
            if (!TryFindPage(texture, out TextureAtlas atlas, out int index))
            {
                return false;
            }

            Image part = atlas._pages[index].GetRegion(
                new Rect2I(region.X, region.Y, region.Width, region.Height)
            );

            ReadOnlySpan<uint> pixels = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, uint>(part.GetData());

            int count = Math.Min(dest.Length, pixels.Length);

            pixels.Slice(0, count).CopyTo(dest);

            return true;
        }

        /// <summary>
        /// Paints <paramref name="src"/> back into a region of whichever atlas
        /// page <paramref name="texture"/> is. The upload happens in the next
        /// <see cref="FlushAll"/>, as it does for a decoded sprite.
        /// </summary>
        /// <inheritdoc cref="TryReadRegion" path="/remarks"/>
        public static bool TryWriteRegion(
            Texture2D texture, Rectangle region, ReadOnlySpan<uint> src
        )
        {
            if (!TryFindPage(texture, out TextureAtlas atlas, out int index))
            {
                return false;
            }

            var rgba = new byte[region.Width * region.Height * 4];

            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(src.Slice(0, region.Width * region.Height))
                .CopyTo(rgba);

            Image part = Image.CreateFromData(
                region.Width, region.Height, false, Image.Format.Rgba8, rgba
            );

            atlas._pages[index].BlitRect(
                part,
                new Rect2I(0, 0, region.Width, region.Height),
                new Vector2I(region.X, region.Y)
            );

            atlas._dirty[index] = true;

            return true;
        }

        private static bool TryFindPage(Texture2D texture, out TextureAtlas atlas, out int index)
        {
            for (int i = 0; i < _live.Count; i++)
            {
                int page = _live[i]._textures.IndexOf(texture as ImageTexture);

                if (page >= 0)
                {
                    atlas = _live[i];
                    index = page;

                    return true;
                }
            }

            atlas = null;
            index = -1;

            return false;
        }

        public Texture2D AddSprite(
            ReadOnlySpan<uint> pixels,
            int width,
            int height,
            out Rectangle pr
        )
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Sprite dimensions must be positive.");

            int pixelCount = checked(width * height);
            int byteCount = checked(pixelCount * 4);
            if (pixels.Length < pixelCount)
                throw new ArgumentException("Not enough pixels for the sprite dimensions.", nameof(pixels));

            // PackRect adds two pixels of padding. An oversized sprite cannot
            // fit even on a fresh page; retain it as a dedicated texture instead.
            if (width > _width - 2 || height > _height - 2)
            {
                var data = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Slice(0, pixelCount)).ToArray();
                Image page = Image.CreateFromData(width, height, false, Image.Format.Rgba8, data);
                var texture = ImageTexture.CreateFromImage(page);
                _pages.Add(page);
                _textures.Add(texture);
                _dirty.Add(false);
                pr = new Rectangle(0, 0, width, height);
                return texture;
            }

            int index = _packingPageIndex;

            if (index < 0)
            {
                CreateNewPage();
                index = _packingPageIndex;
            }

            while (!_packer.PackRect(width, height, out pr))
            {
                CreateNewPage();
                index = _packingPageIndex;
            }

            // A uint here is 0xAABBGGRR, so its bytes in memory are already
            // R,G,B,A -- the order Rgba8 wants. Same reasoning as the art
            // decoder in UoDataProbe and SolidColorTextureCache.
            var rgba = new byte[byteCount];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Slice(0, pixelCount))
                .CopyTo(rgba);

            using Image sprite = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);

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
            _packingPageIndex = _textures.Count - 1;

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

        /// <summary>
        /// Drops every live atlas.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream has no equivalent, because in FNA
        /// the graphics device owns every texture and takes them all down with
        /// itself. Here each loader builds an atlas and most of them never let
        /// go of it, so at exit Godot reports the pages as leaked RIDs -- and
        /// those errors sit in the log next to real ones. Called from the
        /// controller's teardown, which is where the device going away used to
        /// be.
        /// </remarks>
        public static void DisposeAll()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                _live[i].Dispose();
            }

            _live.Clear();
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
            _packingPageIndex = -1;
        }
    }
}
