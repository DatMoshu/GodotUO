using GUO.Compat;
using GUO.Assets;
using GUO.Utility;
using GUO.Utility.Logging;
using static GUO.Platform.Sdl.SDL;
using Godot;

// Both namespaces define Color. The hue path below wants Compat's, the
// byte-packed XNA one with a PackedValue; Godot's is four floats.
using Color = GUO.Compat.Color;
using System;
using System.Buffers;

namespace GUO.Renderer.Arts
{
    public sealed class Art
    {
        private readonly SpriteInfo[] _spriteInfos;
        private readonly TextureAtlas _atlas;
        // PORT DEVIATION (GUO): Epic B, land-array step 2. With
        // --merged-land=array, land art gets pages of its own: every page land
        // is drawn from is a layer of LandPages' Texture2DArray, and a static
        // packed onto a shared page made that layer stale and cost a copy of
        // the whole page (the bump counter: 32 of 34 sprites packed onto land
        // layers on a desktop walk were statics). Upstream shares one atlas.
        private TextureAtlas _landAtlas;
        // END PORT DEVIATION (GUO)
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly Rectangle[] _realArtBounds;
        private readonly ArtLoader _artLoader;
        private readonly HuesLoader _huesLoader;

        public Art(ArtLoader artLoader, HuesLoader huesLoader)
        {
            _artLoader = artLoader;
            _huesLoader = huesLoader;
            _atlas = new TextureAtlas(4096, 4096);
            _spriteInfos = new SpriteInfo[_artLoader.File.Entries.Length];
            _realArtBounds = new Rectangle[_spriteInfos.Length];
        }

        // PORT DEVIATION (GUO): Epic B, the land-array bump counter. Sprites
        // packed onto a page LandPages already holds as a layer, split by
        // what they are: each makes that layer stale, so the next frame
        // copies the whole page again.
        public static int LandOntoLandLayer, StaticOntoLandLayer;
        // END PORT DEVIATION (GUO)

        public ref readonly SpriteInfo GetLand(uint idx)
            => ref Get((uint)(idx & ~0x4000));

        public ref readonly SpriteInfo GetArt(uint idx)
            => ref Get(idx + 0x4000);

        /// <summary>
        /// Forgets one cached sprite so the next draw re-reads it from the
        /// loader (the repaint gump's override files land between draws).
        /// The old atlas rect leaks; testing-grade repaints don't care.
        /// </summary>
        public void Refresh(uint idx)
        {
            if (idx < _spriteInfos.Length)
            {
                _spriteInfos[idx] = default;
            }
        }

        /// <summary>Forgets one static's sprite (graphic without the 0x4000 base).</summary>
        public void RefreshStatic(ushort graphic) => Refresh(0x4000u + graphic);

        /// <summary>Forgets one land tile's sprite.</summary>
        public void RefreshLand(ushort graphic) => Refresh(graphic);

        private ref readonly SpriteInfo Get(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var artInfo = _artLoader.GetArt(idx);

                if (artInfo.Pixels.IsEmpty && idx > 0)
                {
                    // Trying to load a texture that does not exist in the client MULs
                    // Degrading gracefully and only crash if not even the fallback ItemID exists
                    Log.Error(
                        $"Texture not found for sprite: idx: {idx}; itemid: {(idx > 0x4000 ? idx - 0x4000 : '-')}"
                    );
                    return ref Get(0); // ItemID of "UNUSED" placeholder
                }

                // PORT DEVIATION (GUO): land onto its own pages under --merged-land=array (above).
                TextureAtlas atlas = idx < 0x4000 && MergedLand.Array
                    ? _landAtlas ??= new TextureAtlas(4096, 4096)
                    : _atlas;

                spriteInfo.Texture = atlas.AddSprite(
                    artInfo.Pixels,
                    artInfo.Width,
                    artInfo.Height,
                    out spriteInfo.UV
                );

                // PORT DEVIATION (GUO): the bump counter above.
                if (LandPages.Holds(spriteInfo.Texture))
                {
                    if (idx < 0x4000) LandOntoLandLayer++; else StaticOntoLandLayer++;
                }

                if (idx > 0x4000)
                {
                    idx -= 0x4000;
                    _picker.Set(idx, artInfo.Width, artInfo.Height, artInfo.Pixels);

                    var pos1 = 0;
                    int minX = artInfo.Width,
                        minY = artInfo.Height,
                        maxX = 0,
                        maxY = 0;

                    for (int y = 0; y < artInfo.Height; ++y)
                    {
                        for (int x = 0; x < artInfo.Width; ++x)
                        {
                            if (artInfo.Pixels[pos1++] != 0)
                            {
                                minX = Math.Min(minX, x);
                                maxX = Math.Max(maxX, x);
                                minY = Math.Min(minY, y);
                                maxY = Math.Max(maxY, y);
                            }
                        }
                    }

                    _realArtBounds[idx] = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                }
                
            }

            return ref spriteInfo;
        }

        /// <summary>
        /// Builds the mouse cursor's image out of art tile
        /// <paramref name="index"/>, and reports where in it the click point
        /// sits.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream is <c>CreateCursorSurfacePtr</c> and
        /// hands back a raw <c>SDL_Surface*</c>, because FNA's window wants
        /// one. Godot's <c>Input.SetCustomMouseCursor</c> wants a texture, so
        /// that is what this returns, and the name says so. Everything above
        /// the return -- finding the hotspot from the marker pixels, clearing
        /// the border, applying a custom hue -- is upstream's, untouched: the
        /// SDL surface was only ever the container.
        /// </remarks>
        public unsafe Texture2D CreateCursorTexture(
            int index,
            ushort customHue,
            out int hotX,
            out int hotY,
            float dpiScale
        )
        {
            hotX = hotY = 0;

            var artInfo = _artLoader.GetArt((uint)(index + 0x4000));

            if (artInfo.Pixels.IsEmpty)
            {
                return null;
            }

            int srcWidth = artInfo.Width;
            int srcHeight = artInfo.Height;

            // Make a copy of pixels to avoid modifying the original
            var rentedBuffer = ArrayPool<uint>.Shared.Rent(artInfo.Pixels.Length);
            try
            {
                var pixelsCopy = rentedBuffer.AsSpan(0, artInfo.Pixels.Length);
                artInfo.Pixels.CopyTo(pixelsCopy);

                // Process the copy: find hotX/Y and clear marker pixels
                for (int y = 0; y < srcHeight; y++)
                {
                    for (int x = 0; x < srcWidth; x++)
                    {
                        int idx = y * srcWidth + x;
                        uint pixel = pixelsCopy[idx];

                        if (pixel == 0)
                            continue;

                        // Clear black marker pixels
                        if (pixel == 0xFF_00_00_00)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Check for green hotspot marker in first row/column
                        if (pixel == 0xFF_00_FF_00)
                        {
                            if (x == 0)
                                hotY = y;
                            if (y == 0)
                                hotX = x;
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Clear edge pixels (first/last row and column)
                        if (x == 0 || y == 0 || x == srcWidth - 1 || y == srcHeight - 1)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Apply custom hue if needed
                        if (customHue > 0)
                        {
                            Color c = default;
                            c.PackedValue = pixel;
                            pixelsCopy[idx] = HuesHelper.Color16To32(
                                _huesLoader.GetColor16(
                                    HuesHelper.ColorToHue(c),
                                    customHue
                                )
                            ) | 0xFF_00_00_00;
                        }
                    }
                }

                // Scale hotX/Y by dpiScale
                hotX = (int)(hotX * dpiScale);
                hotY = (int)(hotY * dpiScale);

                // Now build the image from the cleaned pixels. Upstream asks
                // SDL for ABGR8888; a uint here is 0xAABBGGRR, whose bytes are
                // R,G,B,A, which is what Rgba8 means by the same layout.
                var rgba = new byte[srcWidth * srcHeight * 4];
                System.Runtime.InteropServices.MemoryMarshal
                    .AsBytes((ReadOnlySpan<uint>)pixelsCopy)
                    .Slice(0, rgba.Length)
                    .CopyTo(rgba);

                Image image = Image.CreateFromData(
                    srcWidth, srcHeight, false, Image.Format.Rgba8, rgba);

                if (dpiScale != 1f)
                {
                    // Nearest, as upstream asks SDL for, and as project rule 7
                    // requires of anything that touches pixel art.
                    image.Resize(
                        (int)(srcWidth * dpiScale),
                        (int)(srcHeight * dpiScale),
                        Image.Interpolation.Nearest);
                }

                return ImageTexture.CreateFromImage(image);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rentedBuffer);
            } 
        }


        public Rectangle GetRealArtBounds(uint idx) =>
            idx < 0 || idx >= _realArtBounds.Length
                ? Rectangle.Empty
                : _realArtBounds[idx];

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);
    }
}
