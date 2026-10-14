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
        // mipmapped textures from overrides/HD/Art PNGs (never the atlas,
        // never picking/cursor/containers). LRU-capped; the toggle gates
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
        private const int HdCacheCap = 256;

        // PORT DEVIATION (GUO): SuperRes 4x upscale detail. Standalone
        // mipmapped textures from the overrides/SR4x disk cache (never the
        // atlas, never picking/cursor/containers). LRU-capped at 768; the
        // toggle gates the resolver so off stays byte-identical native.
        // The background worker that fills the disk cache lands next; on a
        // disk miss TryGetSr returns false so draws stay native.
        private sealed class SrEntry
        {
            public Texture2D Texture;
            public int SrW;
            public int SrH;
            public int NativeW;
            public int NativeH;
            public int Tick;
        }

        private readonly System.Collections.Generic.Dictionary<(bool IsLand, ushort Graphic), SrEntry> _srCache =
            new System.Collections.Generic.Dictionary<(bool, ushort), SrEntry>();
        private int _srTick;
        private const int SrCacheCap = 768;

        // PORT DEVIATION (GUO): which live textures are HD (for the
        // batcher's mesh runs, which share canvas items across textures).
        private static readonly System.Collections.Generic.HashSet<Texture2D> _hdTextures =
            new System.Collections.Generic.HashSet<Texture2D>();

        /// <summary>Whether a live texture is an Enhanced HD one.</summary>
        public static bool IsHdTexture(Texture2D texture)
        {
            return texture != null && _hdTextures.Contains(texture);
        }

        // PORT DEVIATION (GUO): which live textures are SuperRes 4x (for
        // the batcher's mesh runs, which share canvas items across
        // textures).
        private static readonly System.Collections.Generic.HashSet<Texture2D> _srTextures =
            new System.Collections.Generic.HashSet<Texture2D>();

        /// <summary>Whether a live texture is a SuperRes 4x one.</summary>
        public static bool IsSrTexture(Texture2D texture)
        {
            return texture != null && _srTextures.Contains(texture);
        }

        // PORT DEVIATION (GUO): DLSS bake detail. Standalone mipmapped
        // textures from overrides/DLSS PNGs (never the atlas, never
        // picking/cursor/containers). LRU-capped at 256; the DlssBake
        // toggle gates the resolver so off stays byte-identical native.
        // A baked detail filter, so it chains last: HD wins, then SR.
        private sealed class DlssEntry
        {
            public Texture2D Texture;
            public int DlssW;
            public int DlssH;
            public int NativeW;
            public int NativeH;
            public int Tick;
        }

        private readonly System.Collections.Generic.Dictionary<(bool IsLand, ushort Graphic), DlssEntry> _dlssCache =
            new System.Collections.Generic.Dictionary<(bool, ushort), DlssEntry>();
        private int _dlssTick;
        private const int DlssCacheCap = 256;

        // PORT DEVIATION (GUO): which live textures are DLSS bakes (for
        // the batcher's mesh runs, which share canvas items across
        // textures).
        private static readonly System.Collections.Generic.HashSet<Texture2D> _dlssTextures =
            new System.Collections.Generic.HashSet<Texture2D>();

        /// <summary>Whether a live texture is a DLSS bake one.</summary>
        public static bool IsDlssTexture(Texture2D texture)
        {
            return texture != null && _dlssTextures.Contains(texture);
        }

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
        public void RefreshStatic(ushort graphic)
        {
            Refresh(0x4000u + graphic);
            DropHd(false, graphic);
            DropSr(false, graphic);
            DropDlss(false, graphic);
        }

        /// <summary>Forgets one land tile's sprite.</summary>
        public void RefreshLand(ushort graphic)
        {
            Refresh(graphic);
            DropHd(true, graphic);
            DropSr(true, graphic);
            DropDlss(true, graphic);
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
                }
                catch
                {
                }
            }

            _hdCache.Clear();
        }

        /// <summary>
        /// Forgets every SuperRes 4x texture (toggle flips). Native atlas
        /// entries are untouched; chunks are dirtied by the caller.
        /// Textures are forgotten, not disposed: a baked chunk mesh may
        /// still reference one until its rebuild (like the native atlas
        /// rect leak above). HD entries are untouched: clearing SR only.
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

        /// <summary>
        /// Forgets every DLSS bake texture (toggle flips). Native atlas
        /// entries are untouched; chunks are dirtied by the caller.
        /// Textures are forgotten, not disposed: a baked chunk mesh may
        /// still reference one until its rebuild (like the native atlas
        /// rect leak above).
        /// </summary>
        public void RefreshAllDlss()
        {
            foreach (var kv in _dlssCache)
            {
                try
                {
                    _dlssTextures.Remove(kv.Value.Texture);
                }
                catch
                {
                }
            }

            _dlssCache.Clear();
        }

        internal void DropDlss(bool isLand, ushort graphic)
        {
            var key = (isLand, graphic);
            if (_dlssCache.TryGetValue(key, out DlssEntry entry))
            {
                try
                {
                    _dlssTextures.Remove(entry.Texture);
                }
                catch
                {
                }

                _dlssCache.Remove(key);
            }
        }

        internal void DropSr(bool isLand, ushort graphic)
        {
            var key = (isLand, graphic);
            if (_srCache.TryGetValue(key, out SrEntry entry))
            {
                try
                {
                    _srTextures.Remove(entry.Texture);
                }
                catch
                {
                }

                _srCache.Remove(key);
            }
        }

        private bool TryGetSrPath(bool isLand, ushort graphic, out string path)
        {
            path = null;
            try
            {
                string over = System.IO.Path.Combine(
                    ArtLoader.OverrideDir(), "SR4x", "Art",
                    isLand ? "Land" : "Statics", graphic + ".png");
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
                string basePath = _artLoader?.FileManager?.BasePath;
                if (!string.IsNullOrEmpty(basePath))
                {
                    string under = System.IO.Path.Combine(
                        basePath, "SR4x", "Art",
                        isLand ? "Land" : "Statics", graphic + ".png");
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
        /// SuperRes 4x resolver for world-draw callers ONLY (Option B, same
        /// seats as TryGetHd): View.DrawStatic*, LandView's flat path and
        /// ChunkMesh's land bake. Cursor, containers, thumbnails and picking
        /// stay native via GetArt/GetLand. Returns the SR texture plus its
        /// full-rect UV and the native footprint to draw it into. False
        /// means draw native. Corrupt or mismatched PNGs fall through with
        /// a warning. On a disk miss returns false: the background worker
        /// that fills the disk cache lands next.
        /// </summary>
        internal bool TryGetSr(bool isLand, ushort graphic, out Texture2D texture, out Rectangle srUv, out int nativeW, out int nativeH)
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

            var key = (isLand, graphic);
            if (_srCache.TryGetValue(key, out SrEntry cached))
            {
                cached.Tick = ++_srTick;
                texture = cached.Texture;
                srUv = new Rectangle(0, 0, cached.SrW, cached.SrH);
                nativeW = cached.NativeW;
                nativeH = cached.NativeH;
                return texture != null;
            }

            if (!TryGetSrPath(isLand, graphic, out string path) || string.IsNullOrEmpty(path))
            {
                // PORT DEVIATION (GUO): SuperRes 4x lazy worker. On a disk
                // miss the draw stays native and one upscale is queued (the
                // pixel copy is taken here on the main thread: the .mul/.uop
                // readers seek a shared stream and are not thread-safe, so
                // the worker itself never touches the loaders). The copy is
                // the loader-direct unhued hue-0 base, never hued pixels:
                // the cache holds one base per graphic and hue applies in
                // shader at draw time (SrGray).
                try
                {
                    uint idx = isLand ? graphic : 0x4000u + graphic;
                    ArtInfo nativeCopy = _artLoader.GetArt(idx);
                    if (nativeCopy.Width > 0 && nativeCopy.Height > 0 && !nativeCopy.Pixels.IsEmpty)
                    {
                        uint[] copy = nativeCopy.Pixels.ToArray();
                        SuperRes.SrRequestQueue.Enqueue(
                            isLand ? SuperRes.SrKind.Land : SuperRes.SrKind.Static,
                            graphic, nativeCopy.Width, nativeCopy.Height, copy);
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

                // Native footprint for validation and the draw size.
                uint idx = isLand ? graphic : 0x4000u + graphic;
                ArtInfo native;
                try
                {
                    native = _artLoader.GetArt(idx);
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

                if (isLand && srW != srH)
                {
                    Log.Warn($"SR4x {path} is {srW}x{srH}, land must be square; using native.");
                    image.Dispose();
                    return false;
                }

                // PORT DEVIATION (GUO): SuperRes 4x hue-safety (SrGray).
                // The disk cache holds one unhued hue-0 base per graphic and
                // every draw hues it in-shader (same seat as HD), so a PNG
                // that carries chroma where native is exact gray would lose
                // its hue (PARTIAL_HUED needs r==g==b: gray-white with
                // speckles). Reject it before mipmaps: delete, requeue the
                // hue-0 base copy for a gray-restored upscale, draw native.
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
                            uint idx2 = isLand ? graphic : 0x4000u + graphic;
                            ArtInfo regen = _artLoader.GetArt(idx2);
                            if (regen.Width > 0 && regen.Height > 0 && !regen.Pixels.IsEmpty)
                            {
                                uint[] copy = regen.Pixels.ToArray();
                                SuperRes.SrRequestQueue.Enqueue(
                                    isLand ? SuperRes.SrKind.Land : SuperRes.SrKind.Static,
                                    graphic, regen.Width, regen.Height, copy);
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

                Texture2D tex = ImageTexture.CreateFromImage(image);
                image.Dispose();
                if (tex == null)
                {
                    return false;
                }

                if (_srCache.Count >= SrCacheCap)
                {
                    // Evict the least recently used.
                    (bool, ushort) oldest = default;
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
                        // Forget, don't dispose: a baked mesh may still
                        // reference the evicted texture until rebuilt.
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
                _srCache[key] = entry;
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

        /// <summary>
        /// DLSS bake resolver for world-draw callers ONLY (Option B, same
        /// seats as TryGetHd/TryGetSr): View.DrawStatic*, LandView's flat
        /// path and ChunkMesh's land bake. Cursor, containers, thumbnails
        /// and picking stay native via GetArt/GetLand. Returns the baked
        /// texture plus its full-rect UV and the native footprint to draw
        /// it into. False means draw native. Corrupt or mismatched PNGs
        /// fall through with a warning; a PNG without a manifest row still
        /// loads. Chains last (HD wins, then SR): a baked detail filter,
        /// never an upscale, so crisper sources keep their seat.
        /// </summary>
        internal bool TryGetDlss(bool isLand, ushort graphic, out Texture2D texture, out Rectangle dlssUv, out int nativeW, out int nativeH)
        {
            texture = null;
            dlssUv = default;
            nativeW = 0;
            nativeH = 0;

            try
            {
                if (Configuration.ProfileManager.CurrentProfile?.DlssBake != true)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var key = (isLand, graphic);
            if (_dlssCache.TryGetValue(key, out DlssEntry cached))
            {
                cached.Tick = ++_dlssTick;
                texture = cached.Texture;
                dlssUv = new Rectangle(0, 0, cached.DlssW, cached.DlssH);
                nativeW = cached.NativeW;
                nativeH = cached.NativeH;
                return texture != null;
            }

            if (!_artLoader.TryGetDlssPath(isLand, graphic, out string path) || string.IsNullOrEmpty(path))
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Log.Warn($"DLSS {path} unreadable: {e.Message}");
                return false;
            }

            Godot.Image image = new Godot.Image();
            try
            {
                if (image.LoadPngFromBuffer(bytes) != Godot.Error.Ok)
                {
                    Log.Warn($"DLSS {path} is not a PNG; using native.");
                    image.Dispose();
                    return false;
                }

                if (image.GetFormat() != Godot.Image.Format.Rgba8)
                {
                    image.Convert(Godot.Image.Format.Rgba8);
                }

                int dlssW = image.GetWidth();
                int dlssH = image.GetHeight();
                if (dlssW <= 0 || dlssH <= 0)
                {
                    Log.Warn($"DLSS {path} is empty; using native.");
                    image.Dispose();
                    return false;
                }

                // Native footprint for validation and the draw size.
                uint idx = isLand ? graphic : 0x4000u + graphic;
                ArtInfo native;
                try
                {
                    native = _artLoader.GetArt(idx);
                }
                catch (Exception e)
                {
                    Log.Warn($"DLSS {path}: native unreadable ({e.Message}); using native.");
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

                if (dlssW < natW || dlssH < natH)
                {
                    Log.Warn($"DLSS {path} is {dlssW}x{dlssH}, smaller than native {natW}x{natH}; using native.");
                    image.Dispose();
                    return false;
                }

                if (isLand && dlssW != dlssH)
                {
                    Log.Warn($"DLSS {path} is {dlssW}x{dlssH}, land must be square; using native.");
                    image.Dispose();
                    return false;
                }

                // Manifest row, when present, must agree with both sides;
                // a PNG without a row still loads.
                if (ArtLoader.TryGetDlssManifestRow(isLand, graphic, out ArtLoader.DlssManifestRow row) && row != null)
                {
                    if ((row.NativeW != natW || row.NativeH != natH)
                        || (row.DlssW != dlssW || row.DlssH != dlssH))
                    {
                        Log.Warn($"DLSS {path} mismatches its manifest (native {row.NativeW}x{row.NativeH} vs {natW}x{natH}, dlss {row.DlssW}x{row.DlssH} vs {dlssW}x{dlssH}); using native.");
                        image.Dispose();
                        return false;
                    }
                }

                if (dlssW > 1 && dlssH > 1)
                {
                    image.GenerateMipmaps();
                }

                Texture2D tex = ImageTexture.CreateFromImage(image);
                image.Dispose();
                if (tex == null)
                {
                    return false;
                }

                if (_dlssCache.Count >= DlssCacheCap)
                {
                    // Evict the least recently used.
                    (bool, ushort) oldest = default;
                    int oldestTick = int.MaxValue;
                    bool found = false;
                    foreach (var kv in _dlssCache)
                    {
                        if (kv.Value.Tick < oldestTick)
                        {
                            oldestTick = kv.Value.Tick;
                            oldest = kv.Key;
                            found = true;
                        }
                    }

                    if (found && _dlssCache.TryGetValue(oldest, out DlssEntry evict))
                    {
                        // Forget, don't dispose: a baked mesh may still
                        // reference the evicted texture until rebuilt.
                        try
                        {
                            _dlssTextures.Remove(evict.Texture);
                        }
                        catch
                        {
                        }

                        _dlssCache.Remove(oldest);
                    }
                }

                var entry = new DlssEntry
                {
                    Texture = tex,
                    DlssW = dlssW,
                    DlssH = dlssH,
                    NativeW = natW,
                    NativeH = natH,
                    Tick = ++_dlssTick,
                };
                _dlssCache[key] = entry;
                _dlssTextures.Add(tex);

                texture = tex;
                dlssUv = new Rectangle(0, 0, dlssW, dlssH);
                nativeW = natW;
                nativeH = natH;
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"DLSS {path} failed ({e.Message}); using native.");
                try { image?.Dispose(); } catch { }
                return false;
            }
        }

        private void DropHd(bool isLand, ushort graphic)
        {
            var key = (isLand, graphic);
            if (_hdCache.TryGetValue(key, out HdEntry entry))
            {
                try
                {
                    _hdTextures.Remove(entry.Texture);
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
        /// Corrupt or mismatched PNGs fall through with a warning; a PNG
        /// without a manifest row still loads.
        /// </summary>
        internal bool TryGetHd(bool isLand, ushort graphic, out Texture2D texture, out Rectangle hdUv, out int nativeW, out int nativeH)
        {
            texture = null;
            hdUv = default;
            nativeW = 0;
            nativeH = 0;

            try
            {
                if (Configuration.ProfileManager.CurrentProfile?.EnhancedHD != true)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var key = (isLand, graphic);
            if (_hdCache.TryGetValue(key, out HdEntry cached))
            {
                cached.Tick = ++_hdTick;
                texture = cached.Texture;
                hdUv = new Rectangle(0, 0, cached.HdW, cached.HdH);
                nativeW = cached.NativeW;
                nativeH = cached.NativeH;
                return texture != null;
            }

            if (!_artLoader.TryGetHdPath(isLand, graphic, out string path) || string.IsNullOrEmpty(path))
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Log.Warn($"HD {path} unreadable: {e.Message}");
                return false;
            }

            Godot.Image image = new Godot.Image();
            try
            {
                if (image.LoadPngFromBuffer(bytes) != Godot.Error.Ok)
                {
                    Log.Warn($"HD {path} is not a PNG; using native.");
                    image.Dispose();
                    return false;
                }

                if (image.GetFormat() != Godot.Image.Format.Rgba8)
                {
                    image.Convert(Godot.Image.Format.Rgba8);
                }

                int hdW = image.GetWidth();
                int hdH = image.GetHeight();
                if (hdW <= 0 || hdH <= 0)
                {
                    Log.Warn($"HD {path} is empty; using native.");
                    image.Dispose();
                    return false;
                }

                // Native footprint for validation and the draw size.
                uint idx = isLand ? graphic : 0x4000u + graphic;
                ArtInfo native;
                try
                {
                    native = _artLoader.GetArt(idx);
                }
                catch (Exception e)
                {
                    Log.Warn($"HD {path}: native unreadable ({e.Message}); using native.");
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

                if (hdW < natW || hdH < natH)
                {
                    Log.Warn($"HD {path} is {hdW}x{hdH}, smaller than native {natW}x{natH}; using native.");
                    image.Dispose();
                    return false;
                }

                if (isLand && hdW != hdH)
                {
                    Log.Warn($"HD {path} is {hdW}x{hdH}, land must be square; using native.");
                    image.Dispose();
                    return false;
                }

                // Manifest row, when present, must agree with both sides;
                // a PNG without a row still loads.
                if (ArtLoader.TryGetHdManifestRow(isLand, graphic, out ArtLoader.HdManifestRow row) && row != null)
                {
                    if ((row.NativeW != natW || row.NativeH != natH)
                        || (row.HdW != hdW || row.HdH != hdH))
                    {
                        Log.Warn($"HD {path} mismatches its manifest (native {row.NativeW}x{row.NativeH} vs {natW}x{natH}, hd {row.HdW}x{row.HdH} vs {hdW}x{hdH}); using native.");
                        image.Dispose();
                        return false;
                    }
                }

                if (hdW > 1 && hdH > 1)
                {
                    image.GenerateMipmaps();
                }

                Texture2D tex = ImageTexture.CreateFromImage(image);
                image.Dispose();
                if (tex == null)
                {
                    return false;
                }

                if (_hdCache.Count >= HdCacheCap)
                {
                    // Evict the least recently used.
                    (bool, ushort) oldest = default;
                    int oldestTick = int.MaxValue;
                    bool found = false;
                    foreach (var kv in _hdCache)
                    {
                        if (kv.Value.Tick < oldestTick)
                        {
                            oldestTick = kv.Value.Tick;
                            oldest = kv.Key;
                            found = true;
                        }
                    }

                    if (found && _hdCache.TryGetValue(oldest, out HdEntry evict))
                    {
                        // Forget, don't dispose: a baked mesh may still
                        // reference the evicted texture until rebuilt.
                        try
                        {
                            _hdTextures.Remove(evict.Texture);
                        }
                        catch
                        {
                        }

                        _hdCache.Remove(oldest);
                    }
                }

                var entry = new HdEntry
                {
                    Texture = tex,
                    HdW = hdW,
                    HdH = hdH,
                    NativeW = natW,
                    NativeH = natH,
                    Tick = ++_hdTick,
                };
                _hdCache[key] = entry;
                _hdTextures.Add(tex);

                texture = tex;
                hdUv = new Rectangle(0, 0, hdW, hdH);
                nativeW = natW;
                nativeH = natH;
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"HD {path} failed ({e.Message}); using native.");
                try { image?.Dispose(); } catch { }
                return false;
            }
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
