using GUO.Compat;
using GUO.Assets;
using GUO.Utility.Logging;
using System;

namespace GUO.Renderer.Gumps
{
    public sealed class Gump
    {
        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _spriteInfos;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly GumpsLoader _gumpsLoader;

        // PORT DEVIATION (GUO): SuperRes 4x upscale detail. Standalone
        // mipmapped textures from the overrides/SR4x disk cache (never the
        // atlas, never picking). LRU-capped at 768; the toggle gates the
        // resolver so off stays byte-identical native. The background
        // worker that fills the disk cache lands next; on a disk miss
        // TryGetSr returns false so draws stay native.
        private sealed class SrEntry
        {
            public Godot.Texture2D Texture;
            public int SrW;
            public int SrH;
            public int NativeW;
            public int NativeH;
            public int Tick;
        }

        private readonly System.Collections.Generic.Dictionary<uint, SrEntry> _srCache =
            new System.Collections.Generic.Dictionary<uint, SrEntry>();
        private int _srTick;
        private const int SrCacheCap = 768;

        private static readonly System.Collections.Generic.HashSet<Godot.Texture2D> _srTextures =
            new System.Collections.Generic.HashSet<Godot.Texture2D>();

        /// <summary>Whether a live texture is a SuperRes 4x gump one.</summary>
        public static bool IsSrTexture(Godot.Texture2D texture)
        {
            return texture != null && _srTextures.Contains(texture);
        }

        // PORT DEVIATION (GUO): no GraphicsDevice; the atlas is created without one.
        public Gump(GumpsLoader gumpsLoader)
        {
            _gumpsLoader = gumpsLoader;
            _atlas = new TextureAtlas(4096, 4096);
            _spriteInfos = new SpriteInfo[gumpsLoader.File.Entries.Length];
        }

        public ref readonly SpriteInfo GetGump(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var gumpInfo = _gumpsLoader.GetGump(idx);
                if (!gumpInfo.Pixels.IsEmpty)
                {
                    spriteInfo.Texture = _atlas.AddSprite(
                        gumpInfo.Pixels,
                        gumpInfo.Width,
                        gumpInfo.Height,
                        out spriteInfo.UV
                    );

                    _picker.Set(idx, gumpInfo.Width, gumpInfo.Height, gumpInfo.Pixels);
                }
            }

            return ref spriteInfo;
        }

        public bool PixelCheck(uint idx, int x, int y) => _picker.Get(idx, x, y);

        /// <summary>
        /// Forgets every SuperRes 4x gump texture (toggle flips). Native
        /// atlas entries are untouched. Textures are forgotten, not
        /// disposed: a live gump may still reference one until rebuilt.
        /// </summary>
        public void RefreshAllSR()
        {
            foreach (var kv in _srCache)
            {
                try
                {
                    _srTextures.Remove(kv.Value.Texture);
                }
                catch
                {
                }
            }

            _srCache.Clear();

            // PORT DEVIATION (GUO): SuperRes 4x toggle-off drains the lazy
            // queues so no background upscale survives the flip.
            try { SuperRes.SrRequestQueue.Clear(); } catch { }
        }

        internal void DropSr(uint graphic)
        {
            if (_srCache.TryGetValue(graphic, out SrEntry entry))
            {
                try
                {
                    _srTextures.Remove(entry.Texture);
                }
                catch
                {
                }

                _srCache.Remove(graphic);
            }
        }

        private bool TryGetSrPath(uint graphic, out string path)
        {
            path = null;
            try
            {
                string over = System.IO.Path.Combine(
                    ArtLoader.OverrideDir(), "SR4x", "Gumps", graphic + ".png");
                if (System.IO.File.Exists(over))
                {
                    path = over;
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                string basePath = _gumpsLoader?.FileManager?.BasePath;
                if (!string.IsNullOrEmpty(basePath))
                {
                    string under = System.IO.Path.Combine(
                        basePath, "SR4x", "Gumps", graphic + ".png");
                    if (System.IO.File.Exists(under))
                    {
                        path = under;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// SuperRes 4x gump resolver. Returns the SR texture plus its
        /// full-rect UV and the native footprint to draw it into. False
        /// means draw native. Corrupt or mismatched PNGs fall through with
        /// a warning. On a disk miss returns false: the background worker
        /// that fills the disk cache lands next.
        /// </summary>
        internal bool TryGetSr(uint graphic, out Godot.Texture2D texture, out Rectangle srUv, out int nativeW, out int nativeH)
        {
            texture = null;
            srUv = default;
            nativeW = 0;
            nativeH = 0;

            try
            {
                if (Configuration.ProfileManager.CurrentProfile?.SuperRes4x != true)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            if (_srCache.TryGetValue(graphic, out SrEntry cached))
            {
                cached.Tick = ++_srTick;
                texture = cached.Texture;
                srUv = new Rectangle(0, 0, cached.SrW, cached.SrH);
                nativeW = cached.NativeW;
                nativeH = cached.NativeH;
                return texture != null;
            }

            if (!TryGetSrPath(graphic, out string path) || string.IsNullOrEmpty(path))
            {
                // PORT DEVIATION (GUO): SuperRes 4x lazy worker (same copy-
                // on-main-thread rule as Art.TryGetSr: the gump reader is not
                // thread-safe, so the worker never touches the loader). The
                // copy is the loader-direct unhued hue-0 base, never hued
                // pixels: one base per graphic, hue in shader (SrGray).
                try
                {
                    GumpInfo nativeCopy = _gumpsLoader.GetGump(graphic);
                    if (nativeCopy.Width > 0 && nativeCopy.Height > 0 && !nativeCopy.Pixels.IsEmpty)
                    {
                        uint[] copy = nativeCopy.Pixels.ToArray();
                        SuperRes.SrRequestQueue.Enqueue(
                            SuperRes.SrKind.Gump, (int)graphic,
                            nativeCopy.Width, nativeCopy.Height, copy);
                        SuperRes.SrWorker.EnsureStarted();
                    }
                }
                catch
                {
                }

                return false;
            }

            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Log.Warn($"SR4x {path} unreadable: {e.Message}");
                return false;
            }

            Godot.Image image = new Godot.Image();
            try
            {
                if (image.LoadPngFromBuffer(bytes) != Godot.Error.Ok)
                {
                    Log.Warn($"SR4x {path} is not a PNG; using native.");
                    image.Dispose();
                    return false;
                }

                if (image.GetFormat() != Godot.Image.Format.Rgba8)
                {
                    image.Convert(Godot.Image.Format.Rgba8);
                }

                int srW = image.GetWidth();
                int srH = image.GetHeight();
                if (srW <= 0 || srH <= 0)
                {
                    Log.Warn($"SR4x {path} is empty; using native.");
                    image.Dispose();
                    return false;
                }

                GumpInfo native;
                try
                {
                    native = _gumpsLoader.GetGump(graphic);
                }
                catch (Exception e)
                {
                    Log.Warn($"SR4x {path}: native unreadable ({e.Message}); using native.");
                    image.Dispose();
                    return false;
                }

                int natW = native.Width;
                int natH = native.Height;
                if (natW <= 0 || natH <= 0)
                {
                    image.Dispose();
                    return false;
                }

                if (srW < natW || srH < natH)
                {
                    Log.Warn($"SR4x {path} is {srW}x{srH}, smaller than native {natW}x{natH}; using native.");
                    image.Dispose();
                    return false;
                }

                // PORT DEVIATION (GUO): SuperRes 4x hue-safety (SrGray, same
                // rule as Art.TryGetSr). The gump cache holds one unhued
                // hue-0 base per graphic and every GumpPic hues it in-shader;
                // a PNG with chroma where native is exact gray would lose its
                // hue (PARTIAL_HUED needs r==g==b). Reject it: delete,
                // requeue the hue-0 base copy, draw native this frame.
                try
                {
                    byte[] srData = image.GetData();
                    uint[] natPx = native.Pixels.ToArray();
                    if (!SuperRes.SrGray.IsSrGraySafe(srData, srW, srH, natPx, natW, natH))
                    {
                        Log.Warn($"SR4x {path} breaks the hue gray-base (chroma where native is gray); regenerating, using native.");
                        image.Dispose();
                        try { System.IO.File.Delete(path); } catch { }
                        try
                        {
                            GumpInfo regen = _gumpsLoader.GetGump(graphic);
                            if (regen.Width > 0 && regen.Height > 0 && !regen.Pixels.IsEmpty)
                            {
                                uint[] copy = regen.Pixels.ToArray();
                                SuperRes.SrRequestQueue.Enqueue(
                                    SuperRes.SrKind.Gump, (int)graphic,
                                    regen.Width, regen.Height, copy);
                                SuperRes.SrWorker.EnsureStarted();
                            }
                        }
                        catch
                        {
                        }

                        return false;
                    }
                }
                catch
                {
                    // Validation failure is not fatal: fall through and draw
                    // the SR texture (old behavior) rather than break the frame.
                }

                if (srW > 1 && srH > 1)
                {
                    image.GenerateMipmaps();
                }

                Godot.Texture2D tex = Godot.ImageTexture.CreateFromImage(image);
                image.Dispose();
                if (tex == null)
                {
                    return false;
                }

                if (_srCache.Count >= SrCacheCap)
                {
                    uint oldest = 0;
                    int oldestTick = int.MaxValue;
                    bool found = false;
                    foreach (var kv in _srCache)
                    {
                        if (kv.Value.Tick < oldestTick)
                        {
                            oldestTick = kv.Value.Tick;
                            oldest = kv.Key;
                            found = true;
                        }
                    }

                    if (found && _srCache.TryGetValue(oldest, out SrEntry evict))
                    {
                        try
                        {
                            _srTextures.Remove(evict.Texture);
                        }
                        catch
                        {
                        }

                        _srCache.Remove(oldest);
                    }
                }

                var entry = new SrEntry
                {
                    Texture = tex,
                    SrW = srW,
                    SrH = srH,
                    NativeW = natW,
                    NativeH = natH,
                    Tick = ++_srTick,
                };
                _srCache[graphic] = entry;
                _srTextures.Add(tex);

                texture = tex;
                srUv = new Rectangle(0, 0, srW, srH);
                nativeW = natW;
                nativeH = natH;
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"SR4x {path} failed ({e.Message}); using native.");
                try { image?.Dispose(); } catch { }
                return false;
            }
        }
    }
}
