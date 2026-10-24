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

        // PORT DEVIATION (GUO): Enhanced HD repaint detail. Standalone
        // mipmapped textures from verified hd-art pack components (never the atlas,
        // never picking/cursor/containers). Pack-memory-bounded; the toggle gates
        // the resolver so off stays byte-identical native.
        private sealed class HdEntry
        {
            public Texture2D Texture;
            public int HdW;
            public int HdH;
            public int NativeW;
            public int NativeH;
            public int Tick;
        }

        private readonly System.Collections.Generic.Dictionary<(bool IsLand, ushort Graphic), HdEntry> _hdCache =
            new System.Collections.Generic.Dictionary<(bool, ushort), HdEntry>();
        private int _hdTick;

        // PORT DEVIATION (GUO): which live textures are HD (for the
        // batcher's mesh runs, which share canvas items across textures).
        private static readonly System.Collections.Generic.HashSet<Texture2D> _hdTextures =
            new System.Collections.Generic.HashSet<Texture2D>();

        /// <summary>Whether a live texture is an Enhanced HD one.</summary>
        public static bool IsHdTexture(Texture2D texture)
        {
            return texture != null && _hdTextures.Contains(texture);
        }

        public Art(ArtLoader artLoader, HuesLoader huesLoader)
        {
            _artLoader = artLoader;
            _huesLoader = huesLoader;
            // PORT DEVIATION (GUO): dynamicPages, for GUO_ATLAS_PAGE_1024 (TextureAtlas.SmallDynamicPages).
            _atlas = new TextureAtlas(4096, 4096, dynamicPages: true);
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
            // PORT DEVIATION (GUO): wall-plane classification follows native
            // art edits along with the sprite cache (ADR-0039).
            if (idx >= 0x4000 && idx - 0x4000 <= ushort.MaxValue)
                GUO.Renderer.Lights.WallFaces.Invalidate((ushort)(idx - 0x4000));
            if (idx < _spriteInfos.Length)
            {
                _spriteInfos[idx] = default;
            }
        }

        /// <summary>Forgets one static's sprite (graphic without the 0x4000 base).</summary>
        public void RefreshStatic(ushort graphic)
        {
            Refresh(0x4000u + graphic);
            DropHd(false, graphic);
        }

        /// <summary>Forgets one land tile's sprite.</summary>
        public void RefreshLand(ushort graphic)
        {
            Refresh(graphic);
            DropHd(true, graphic);
        }

        /// <summary>
        /// Forgets every Enhanced HD texture (toggle flips). Native atlas
        /// entries are untouched; chunks are dirtied by the caller.
        /// Textures are forgotten, not disposed: a baked chunk mesh may
        /// still reference one until its rebuild (like the native atlas
        /// rect leak above).
        /// </summary>
        public void RefreshAllHD()
        {
            foreach (var kv in _hdCache)
            {
                try
                {
                    _hdTextures.Remove(kv.Value.Texture);
                    _hdScales.Remove(kv.Value.Texture);
                }
                catch
                {
                }
            }

            _hdCache.Clear();
            _hdBad.Clear();
        }

        private void DropHd(bool isLand, ushort graphic)
        {
            var key = (isLand, graphic);
            if (_hdCache.TryGetValue(key, out HdEntry entry))
            {
                try
                {
                    _hdTextures.Remove(entry.Texture);
                    _hdScales.Remove(entry.Texture);
                }
                catch
                {
                }

                _hdCache.Remove(key);
            }
        }

        /// <summary>
        /// Enhanced HD resolver for world-draw callers ONLY (Option B):
        /// View.DrawStatic*, LandView's flat path and ChunkMesh's land bake.
        /// Cursor, containers, thumbnails and picking stay native via
        /// GetArt/GetLand. Returns the HD texture plus its full-rect UV and
        /// the native footprint to draw it into. False means draw native.
        /// Only verified pack payloads are accepted; a different native canvas falls back.
        /// </summary>
        internal bool TryGetHd(bool isLand, ushort graphic, out Texture2D texture, out Rectangle hdUv, out int nativeW, out int nativeH)
        {
            texture = null; hdUv = default; nativeW = nativeH = 0;
            if (Configuration.ProfileManager.CurrentProfile?.EnhancedHD != true) return false;
            var key = (isLand, graphic);
            if (_hdBad.Contains(key)) return false;
            if (_hdCache.TryGetValue(key, out var cached))
            {
                texture = cached.Texture; hdUv = new Rectangle(0, 0, cached.HdW, cached.HdH);
                nativeW = cached.NativeW; nativeH = cached.NativeH;
                return true;
            }
            var content = Client.Game?.UO?.FileManager?.Content;
            if (content == null || !content.TryHd(isLand, graphic, out var asset)) return false;
            var native = _artLoader.GetArt(isLand ? graphic : 0x4000u + graphic);
            if (native.Width != asset.NativeWidth || native.Height != asset.NativeHeight)
            {
                _hdBad.Add(key);
                Log.Warn($"HD pack graphic {graphic} has a different native canvas; using native.");
                return false;
            }
            texture = asset.Texture();
            _hdCache[key] = new HdEntry { Texture = texture, HdW = asset.Width, HdH = asset.Height,
                NativeW = asset.NativeWidth, NativeH = asset.NativeHeight, Tick = ++_hdTick };
            _hdTextures.Add(texture);
            _hdScales[texture] = asset.Scale;
            hdUv = new Rectangle(0, 0, asset.Width, asset.Height);
            nativeW = asset.NativeWidth; nativeH = asset.NativeHeight;
            return true;
        }

        private static readonly System.Collections.Generic.Dictionary<Texture2D, double> _hdScales = new();
        private readonly System.Collections.Generic.HashSet<(bool, ushort)> _hdBad = new();
        internal static SamplerState HdSampler(Texture2D texture)
        {
            if (!_hdScales.TryGetValue(texture, out double scale)) return SamplerState.PointClamp;
            double zoom = Client.Game?.GetScene<GUO.Game.Scenes.GameScene>()?.Camera.Zoom ?? 1f;
            return HdSampling.Select(scale, zoom, Configuration.ProfileManager.CurrentProfile?.HdFilter ?? Configuration.HdFilterMode.Auto);
        }

        internal static void ReleaseHd(Texture2D texture)
        {
            _hdTextures.Remove(texture);
            _hdScales.Remove(texture);
        }

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
