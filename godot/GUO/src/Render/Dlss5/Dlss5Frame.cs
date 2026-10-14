// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): DLSS 5 full-frame enhance V3 frame hook (full-viewport
// tiled composite).
//
// V3 keeps V2's plumbing (hook after RenderTargets.Draw, 2ms readback
// budget, skip frames with backoff, drop on toggle-off, temporal reset on
// scene cuts, CPU/mobile/web/headless pass-through) and widens the visible
// composite from the single center 320 tile to the whole viewport: the
// background worker tiles the captured frame with DlssBakeTiles (TileSize
// 320, Overlap 32, Stride 288, linear-ramp BlendWeight via
// ComputePlacements/ExtractTile/Accumulate/Normalize), runs each tile through
// EnhanceTemporal, and the frame thread draws the stitched full-res RGB back
// over the full window rect via ImageTexture.CreateFromImage. The generation
// guard is checked every tile (scene cut, resize, toggle flip mid-enhance
// aborts the frame) plus at composite commit and at draw, so a stale frame
// never draws. Off stays byte-identical (no readback, no draw); missing-model
// and CPU stay pass-through/idle (no composite).
//
// Motion: every tile uses the ZeroMotion320 stub (static-frame history warm).
// Real per-pixel motion RGF (current->previous) is deferred: the temporal
// path takes the motion image already, but nothing feeds it velocity yet, so
// fast scrolls fall back on history rather than warping. Teleports, facet
// changes, resizes and toggle flips call NotifySceneCut/Reset (plus the
// auto-detect below) to restart history instead of smearing it.
//
// Cursor: the cursor (plus held-item badge, tooltip, aura) is drawn into the
// UI render target (GameController draws GameCursor while UiRenderTarget is
// bound), so the viewport readback below contains the cursor and temporal
// history would smear a moving cursor across frames. Cheap exclusion: the
// frame thread captures a ~64px cursor box at Mouse.Position (empty when the
// pointer is hidden or the UI is hidden for a clean shot) and the worker
// skips the network for tiles intersecting it (passthrough of the original
// pixels), so the cursor stays native-sharp. A full inpaint-plus-composite-
// hole (enhance under the cursor, cut it out of the draw) is deferred to V4.
//
// Filter decision: the project sets default_texture_filter=0 (Nearest) to
// preserve pixel art (rule 7). The DLSS composite is neural pixels magnified
// to the window (e.g. 320 tiles stitched 1:1, Linear at non-integer DPI); the
// composite draws LinearClamp explicitly and resets to null (PointClamp)
// immediately after, leaving every other draw Nearest byte-identical.

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GUO.Compat;
using GUO.Configuration;
using GUO.Dlss5;
using GUO.Utility.Logging;

namespace GUO.Renderer.Dlss5
{
    internal static class Dlss5Frame
    {
        /// <summary>Network tile: 320x320 RGB8 (DlssBakeTiles size).</summary>
        private const int Tile = DlssBakeTiles.TileSize;

        /// <summary>Frame-thread budget for the readback, in microseconds.</summary>
        private const ulong BudgetUsec = 2000;

        /// <summary>
        /// Frames to skip between readbacks once warm (~1.5s at 60fps; one
        /// full-viewport enhance is many tiles, so the worker is usually busy
        /// anyway).
        /// </summary>
        private const int BaseSkipFrames = 90;

        private const int MaxSkipFrames = 600;

        /// <summary>Cursor exclusion box, in window pixels before DPI scale.</summary>
        private const int CursorBox = 64;

        /// <summary>Player-tile jump treated as a teleport (walking is 1).</summary>
        private const int TeleportTiles = 8;

        /// <summary>Camera-pixel jump treated as a teleport (follow drifts).</summary>
        private const int TeleportPixels = 256;

        private static readonly object Sync = new object();
        private static Task _worker;
        private static volatile bool _shutdown;
        private static Image _pending;
        private static int _pendingW;
        private static int _pendingH;
        private static Rectangle _pendingDest;
        private static Rectangle _pendingCursor;
        private static long _pendingGen = -1;
        private static long _generation;
        private static byte[] _compositeRgb;
        private static int _compositeW;
        private static int _compositeH;
        private static Rectangle _compositeDest;
        private static long _compositeGen = -1;
        private static ImageTexture _compositeTex;
        private static long _compositeTexGen = -1;
        private static bool _loggedComposite;
        private static int _busy;
        private static int _drop;
        private static volatile bool _cpuPassthrough;
        private static long _framesSeen;
        private static long _nextFrame;
        private static int _skipFrames = BaseSkipFrames;
        private static object _lastScene;
        private static Vector2I _lastSize = Vector2I.Zero;
        private static int _lastImgW;
        private static int _lastImgH;
        private static int _lastCamX;
        private static int _lastCamY;
        private static int _lastCamW;
        private static int _lastCamH;
        private static int _lastPlayerX;
        private static int _lastPlayerY;
        private static int _lastPlayerZ;
        private static int _lastMap = int.MinValue;
        private static bool _hasTrack;
        private static bool _loggedMobile;
        private static bool _loggedHeadless;
        private static bool _loggedCpu;
        private static bool _loggedProvider;
        private static bool _loggedNoModel;
        private static long _enhanced;
        private static long _dropped;
        private static double _lastMs;
        private static string _provider = "";

        /// <summary>Ms of the last background enhance (0 until the first one finishes).</summary>
        internal static double LastMs => _lastMs;

        /// <summary>Native provider serving temporal ("cuda"/"cpu"/"" until loaded).</summary>
        internal static string Provider => _provider;

        internal static long Enhanced => Interlocked.Read(ref _enhanced);
        internal static long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>
        /// Clears frame state on toggle flip (ModernOptions + OptionsGump).
        /// Drops in-flight work, invalidates the composite generation so a
        /// stale frame never draws, and resets native temporal history; never throws.
        /// Frame thread drops the cached texture (main-thread owned); the
        /// worker only ever touches the byte copy under lock.
        /// </summary>
        public static void Reset()
        {
            try
            {
                if (_shutdown)
                {
                    return;
                }
                // Drain: drop the queued frame, invalidate the composite so a
                // stale frame never draws, bump the generation so in-flight
                // worker tiles abort at the next guard. The worker's current
                // native call finishes under Dlss5Service's lock; ResetTemporal
                // then clears history. Non-blocking: never joins the worker.
                Interlocked.Exchange(ref _drop, 1);
                lock (Sync)
                {
                    try { _pending?.Dispose(); } catch { }
                    _pending = null;
                    _pendingW = 0;
                    _pendingH = 0;
                    _pendingGen = -1;
                    _compositeRgb = null;
                    _compositeGen = -1;
                }
                Interlocked.Increment(ref _generation);
                _compositeTex = null;
                _compositeTexGen = -1;
                _loggedComposite = false;
                _lastScene = null;
                _lastSize = Vector2I.Zero;
                _lastImgW = 0;
                _lastImgH = 0;
                _hasTrack = false;
                _lastMap = int.MinValue;
                _skipFrames = BaseSkipFrames;
                Interlocked.Exchange(ref _nextFrame, 0);
                _cpuPassthrough = false;
                _loggedProvider = false;
                _loggedNoModel = false;
                _loggedCpu = false;
                Dlss5Service.Shared.ResetTemporal();
            }
            catch { }
        }

        /// <summary>
        /// Joins the background worker before the native DLSS5/ORT session is
        /// freed. Idempotent, main-thread only, never throws. After this the
        /// loop has exited, _busy is 0, the pending/composite queues are
        /// drained, and the caller may free the native instance. Process()
        /// and Reset() become no-ops once shutdown is set.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _shutdown = true;
                try { Interlocked.Exchange(ref _drop, 1); } catch { }
                try { Interlocked.Increment(ref _generation); } catch { }
                try
                {
                    lock (Sync)
                    {
                        try { _pending?.Dispose(); } catch { }
                        _pending = null;
                        _pendingGen = -1;
                        _compositeRgb = null;
                        _compositeGen = -1;
                    }
                }
                catch { }
                // Join: wait for the in-flight tile to leave the native call
                // (EnhanceTemporal holds Dlss5Service's lock) then for the
                // task itself. Bounded so exit never hangs.
                try
                {
                    for (int i = 0; i < 50 && Interlocked.CompareExchange(ref _busy, 0, 0) == 1; i++)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch { }
                Task w = null;
                try
                {
                    lock (Sync) { w = _worker; }
                }
                catch { }
                try
                {
                    w?.Wait(5000);
                }
                catch { }
                try
                {
                    lock (Sync) { _worker = null; }
                }
                catch { }
                try
                {
                    _compositeTex = null;
                    _compositeTexGen = -1;
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Marks a scene cut (map change, teleport): history restarts and the
        /// composite generation invalidates so the previous scene's frame never
        /// draws over the new one.
        /// </summary>
        public static void NotifySceneCut()
        {
            try
            {
                if (_shutdown)
                {
                    return;
                }
                lock (Sync)
                {
                    try { _pending?.Dispose(); } catch { }
                    _pending = null;
                    _pendingGen = -1;
                    _compositeRgb = null;
                    _compositeGen = -1;
                }
                Interlocked.Increment(ref _generation);
                Dlss5Service.Shared.ResetTemporal();
            }
            catch { }
        }

        /// <summary>
        /// V3 frame hook: call after _renderTargets.Draw, before
        /// Plugin.ProcessDrawCmdList. Off is one bool check with no readback
        /// and no draw (byte-identical). On readbacks the full viewport on a
        /// skip budget, and draws the last stitched full-viewport enhance back
        /// over the window when its generation is current.
        /// Never throws and never blocks the frame past the 2ms budget:
        /// tiling + inference + model load run on the background worker; the
        /// composite texture is built on this thread (ImageTexture is
        /// main-thread owned).
        /// </summary>
        internal static void Process(GameController game, UltimaBatcher2D batcher)
        {
            if (_shutdown)
            {
                return;
            }
            Profile profile = null;
            try
            {
                profile = ProfileManager.CurrentProfile;
            }
            catch
            {
                return;
            }
            if (profile == null || !profile.Dlss5)
            {
                if (_pending != null || _compositeRgb != null || Interlocked.CompareExchange(ref _busy, 0, 0) == 1)
                {
                    Interlocked.Exchange(ref _drop, 1);
                    lock (Sync)
                    {
                        try { _pending?.Dispose(); } catch { }
                        _pending = null;
                        _pendingGen = -1;
                        _compositeRgb = null;
                        _compositeGen = -1;
                    }
                    Interlocked.Increment(ref _generation);
                    _compositeTex = null;
                    _compositeTexGen = -1;
                }
                return;
            }

            try
            {
                if (game == null)
                {
                    return;
                }

                if (OS.HasFeature("mobile") || OS.HasFeature("web"))
                {
                    if (!_loggedMobile)
                    {
                        _loggedMobile = true;
                        try
                        {
                            Log.Warn("[DLSS5] frame hook pass-through on mobile/web.");
                        }
                        catch { }
                    }
                    return;
                }

                if (DisplayServer.GetName() == "headless")
                {
                    if (!_loggedHeadless)
                    {
                        _loggedHeadless = true;
                        try
                        {
                            Log.Warn("[DLSS5] frame hook pass-through headless.");
                        }
                        catch { }
                    }
                    return;
                }

                if (_cpuPassthrough)
                {
                    return;
                }

                // Scene cut: a new scene (login -> game), a viewport resize,
                // a camera teleport (large player/camera jump, facet change),
                // or a toggle flip invalidates history and the composite
                // generation; reset and drop the frame. Ordinary walking
                // drifts the camera a few pixels and never cuts.
                object scene = null;
                try
                {
                    scene = game.Scene;
                }
                catch
                {
                    return;
                }
                Vector2I size;
                try
                {
                    Vector2 s = game.GetViewportRect().Size;
                    size = new Vector2I((int)s.X, (int)s.Y);
                }
                catch
                {
                    return;
                }
                int camX = 0, camY = 0, camW = 0, camH = 0;
                bool hasCam = false;
                try
                {
                    var sc = scene as GUO.Game.Scenes.Scene;
                    if (sc != null && sc.Camera.Bounds.Width > 0 && sc.Camera.Bounds.Height > 0)
                    {
                        camX = sc.Camera.Bounds.X;
                        camY = sc.Camera.Bounds.Y;
                        camW = sc.Camera.Bounds.Width;
                        camH = sc.Camera.Bounds.Height;
                        hasCam = true;
                    }
                }
                catch { }
                int px = 0, py = 0, pz = 0, map = int.MinValue;
                bool hasPlayer = false;
                try
                {
                    var world = game.UO?.World;
                    var player = world?.Player;
                    if (player != null)
                    {
                        px = player.X;
                        py = player.Y;
                        pz = player.Z;
                        map = world.MapIndex;
                        hasPlayer = true;
                    }
                }
                catch { }
                bool cut = false;
                if (!ReferenceEquals(scene, _lastScene) || size != _lastSize)
                {
                    cut = true;
                }
                else if (hasCam && _hasTrack && (camW != _lastCamW || camH != _lastCamH))
                {
                    cut = true;
                }
                else if (hasCam && _hasTrack && hasPlayer &&
                    (Math.Abs(camX - _lastCamX) + Math.Abs(camY - _lastCamY) > TeleportPixels))
                {
                    cut = true;
                }
                else if (hasPlayer && _hasTrack && _lastMap != int.MinValue &&
                    (map != _lastMap ||
                     Math.Abs(px - _lastPlayerX) + Math.Abs(py - _lastPlayerY) > TeleportTiles ||
                     Math.Abs(pz - _lastPlayerZ) > 20))
                {
                    cut = true;
                }
                if (cut)
                {
                    _lastScene = scene;
                    _lastSize = size;
                    _lastImgW = 0;
                    _lastImgH = 0;
                    _hasTrack = true;
                    _lastCamX = camX;
                    _lastCamY = camY;
                    _lastCamW = camW;
                    _lastCamH = camH;
                    if (hasPlayer)
                    {
                        _lastPlayerX = px;
                        _lastPlayerY = py;
                        _lastPlayerZ = pz;
                        _lastMap = map;
                    }
                    try
                    {
                        Dlss5Service.Shared.ResetTemporal();
                    }
                    catch { }
                    lock (Sync)
                    {
                        try { _pending?.Dispose(); } catch { }
                        _pending = null;
                        _pendingGen = -1;
                        _compositeRgb = null;
                        _compositeGen = -1;
                    }
                    Interlocked.Increment(ref _generation);
                    _compositeTex = null;
                    _compositeTexGen = -1;
                }
                else
                {
                    _lastScene = scene;
                    _lastSize = size;
                    if (hasCam)
                    {
                        _hasTrack = true;
                        _lastCamX = camX;
                        _lastCamY = camY;
                        _lastCamW = camW;
                        _lastCamH = camH;
                    }
                    if (hasPlayer)
                    {
                        _hasTrack = true;
                        _lastPlayerX = px;
                        _lastPlayerY = py;
                        _lastPlayerZ = pz;
                        _lastMap = map;
                    }
                }

                long frame = Interlocked.Increment(ref _framesSeen);
                if (frame < Interlocked.Read(ref _nextFrame))
                {
                    DrawComposite(batcher);
                    return;
                }
                if (Interlocked.CompareExchange(ref _busy, 0, 0) == 1)
                {
                    Interlocked.Exchange(ref _nextFrame, frame + _skipFrames);
                    DrawComposite(batcher);
                    return;
                }
                bool hasPending;
                lock (Sync)
                {
                    hasPending = _pending != null;
                }
                if (hasPending)
                {
                    DrawComposite(batcher);
                    return;
                }

                // Full-viewport readback: the network runs at 320, so the
                // worker tiles the frame at Stride 288 with 32px overlap and
                // blends with the linear ramp. Only the GetImage copy is timed
                // against the 2ms budget here; conversion + tiling + inference
                // run on the worker. Overruns back the skip off instead of
                // stalling. The dest rect is the full window, so the stitched
                // enhance draws back exactly where it was read from.
                ulong t0 = Godot.Time.GetTicksUsec();
                Image full = null;
                int fw = 0, fh = 0;
                try
                {
                    Viewport viewport = game.GetViewport();
                    Texture2D tex = viewport?.GetTexture();
                    full = tex?.GetImage();
                    if (full != null)
                    {
                        fw = full.GetWidth();
                        fh = full.GetHeight();
                    }
                }
                catch
                {
                    try { full?.Dispose(); } catch { }
                    full = null;
                }
                ulong dt = Godot.Time.GetTicksUsec() - t0;
                if (dt > BudgetUsec || full == null || fw <= 0 || fh <= 0 || size.X <= 0 || size.Y <= 0)
                {
                    try { full?.Dispose(); } catch { }
                    _skipFrames = Math.Min(_skipFrames * 2 + 10, MaxSkipFrames);
                    Interlocked.Exchange(ref _nextFrame, frame + _skipFrames);
                    DrawComposite(batcher);
                    return;
                }
                if (fw > 4096 || fh > 4096)
                {
                    // 4K+ viewports would stitch ~100MB float accumulators;
                    // stay pass-through rather than OOM the worker.
                    try { full.Dispose(); } catch { }
                    _skipFrames = Math.Min(_skipFrames * 2 + 10, MaxSkipFrames);
                    Interlocked.Exchange(ref _nextFrame, frame + _skipFrames);
                    DrawComposite(batcher);
                    return;
                }
                if (fw != _lastImgW || fh != _lastImgH)
                {
                    if (_lastImgW != 0 && _lastImgH != 0)
                    {
                        // Backing-store resize without a window resize (DPI
                        // flip): same cut as above, this frame is already stale.
                        try { full.Dispose(); } catch { }
                        _lastImgW = fw;
                        _lastImgH = fh;
                        try { Dlss5Service.Shared.ResetTemporal(); } catch { }
                        lock (Sync)
                        {
                            try { _pending?.Dispose(); } catch { }
                            _pending = null;
                            _pendingGen = -1;
                            _compositeRgb = null;
                            _compositeGen = -1;
                        }
                        Interlocked.Increment(ref _generation);
                        _compositeTex = null;
                        _compositeTexGen = -1;
                        Interlocked.Exchange(ref _nextFrame, frame + _skipFrames);
                        DrawComposite(batcher);
                        return;
                    }
                    _lastImgW = fw;
                    _lastImgH = fh;
                }
                if (_skipFrames > BaseSkipFrames)
                {
                    _skipFrames = Math.Max(BaseSkipFrames, _skipFrames / 2);
                }

                Rectangle dest = new Rectangle(0, 0, size.X, size.Y);
                Rectangle cursor = GetCursorRect(fw, fh, size);
                long gen = Interlocked.Read(ref _generation);
                bool pendBusy;
                lock (Sync)
                {
                    pendBusy = _pending != null;
                    if (!pendBusy)
                    {
                        _pending = full;
                        _pendingW = fw;
                        _pendingH = fh;
                        _pendingDest = dest;
                        _pendingCursor = cursor;
                        _pendingGen = gen;
                        full = null;
                    }
                }
                if (pendBusy)
                {
                    try { full?.Dispose(); } catch { }
                    DrawComposite(batcher);
                    return;
                }
                try { full?.Dispose(); } catch { }
                Interlocked.Exchange(ref _drop, 0);
                Interlocked.Exchange(ref _nextFrame, frame + _skipFrames);
                EnsureWorker();
                DrawComposite(batcher);
            }
            catch { }
        }

        /// <summary>
        /// Cursor box in frame pixels: ~64px at Mouse.Position, scaled from
        /// window to backing-store size. Empty when no cursor is drawn (pad
        /// pointer-hidden, clean-shot UI hidden), so every tile enhances.
        /// Never throws.
        /// </summary>
        private static Rectangle GetCursorRect(int imgW, int imgH, Vector2I winSize)
        {
            try
            {
                if (GUO.Input.InputMode.PointerHidden)
                {
                    return new Rectangle(0, 0, 0, 0);
                }
                if (GUO.Renderer.CleanShots.Hidden)
                {
                    return new Rectangle(0, 0, 0, 0);
                }
                if (winSize.X <= 0 || winSize.Y <= 0 || imgW <= 0 || imgH <= 0)
                {
                    return new Rectangle(0, 0, 0, 0);
                }
                var mp = GUO.Input.Mouse.Position;
                float sx = (float)imgW / (float)winSize.X;
                float sy = (float)imgH / (float)winSize.Y;
                int bx = mp.X - 8;
                int by = mp.Y - 8;
                int ix = (int)(bx * sx);
                int iy = (int)(by * sy);
                int iw = Math.Max(8, (int)(CursorBox * sx));
                int ih = Math.Max(8, (int)(CursorBox * sy));
                if (ix < 0) { iw += ix; ix = 0; }
                if (iy < 0) { ih += iy; iy = 0; }
                if (ix + iw > imgW) { iw = imgW - ix; }
                if (iy + ih > imgH) { ih = imgH - iy; }
                if (iw <= 0 || ih <= 0)
                {
                    return new Rectangle(0, 0, 0, 0);
                }
                return new Rectangle(ix, iy, iw, ih);
            }
            catch
            {
                return new Rectangle(0, 0, 0, 0);
            }
        }

        private static bool TileOverlapsCursor(DlssBakeTiles.Placement p, Rectangle cursor)
        {
            if (cursor.Width <= 0 || cursor.Height <= 0)
            {
                return false;
            }
            int tx1 = p.X0 + DlssBakeTiles.TileSize;
            int ty1 = p.Y0 + DlssBakeTiles.TileSize;
            return p.X0 < cursor.X + cursor.Width && cursor.X < tx1 &&
                   p.Y0 < cursor.Y + cursor.Height && cursor.Y < ty1;
        }

        /// <summary>
        /// Frame-thread composite: draws the last stitched full-viewport
        /// enhance over the window when its generation is still current.
        /// Stale frames (scene cut, resize, teleport, toggle flip mid-enhance)
        /// never draw. Off / CPU / missing-model never reach here with a
        /// composite set. LinearClamp is explicit: the project default is
        /// Nearest (rule 7, pixel art), but the neural frame is magnified at
        /// non-integer DPI and Nearest would block it up; the sampler resets
        /// to null (PointClamp) immediately after so all other draws stay
        /// identical. Never throws.
        /// </summary>
        private static void DrawComposite(UltimaBatcher2D batcher)
        {
            if (batcher == null)
            {
                return;
            }
            try
            {
                byte[] rgb;
                int w, h;
                Rectangle dest;
                long gen;
                lock (Sync)
                {
                    if (_compositeRgb == null)
                    {
                        return;
                    }
                    gen = _compositeGen;
                    if (gen != Interlocked.Read(ref _generation))
                    {
                        return;
                    }
                    rgb = _compositeRgb;
                    w = _compositeW;
                    h = _compositeH;
                    dest = _compositeDest;
                }
                if (rgb == null || w <= 0 || h <= 0 || rgb.Length < w * h * 3 ||
                    dest.Width <= 0 || dest.Height <= 0)
                {
                    return;
                }
                if (_compositeTex == null || !GodotObject.IsInstanceValid(_compositeTex) || _compositeTexGen != gen)
                {
                    Image img = null;
                    try
                    {
                        img = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
                        if (img == null)
                        {
                            return;
                        }
                        ImageTexture tex = ImageTexture.CreateFromImage(img);
                        if (tex == null)
                        {
                            return;
                        }
                        _compositeTex = tex;
                        _compositeTexGen = gen;
                    }
                    catch
                    {
                        return;
                    }
                    finally
                    {
                        try { img?.Dispose(); } catch { }
                    }
                }
                try
                {
                    batcher.Begin();
                    batcher.SetSampler(SamplerState.LinearClamp);
                    batcher.Draw(_compositeTex, dest, new Vector3(0, 0, 1), 0f);
                    batcher.SetSampler(null);
                    batcher.End();
                }
                catch
                {
                    try { batcher.SetSampler(null); } catch { }
                    try { batcher.End(); } catch { }
                    return;
                }
                if (!_loggedComposite)
                {
                    _loggedComposite = true;
                    try
                    {
                        Log.Info($"[DLSS5] composite active: provider={_provider} last_ms={_lastMs:F0} frame={w}x{h} dest={dest.Width}x{dest.Height}.");
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void EnsureWorker()
        {
            if (_shutdown)
            {
                return;
            }
            lock (Sync)
            {
                if (_shutdown || _worker != null)
                {
                    return;
                }
                _worker = Task.Factory.StartNew(
                    Loop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }

        private static void Loop()
        {
            Dlss5Service svc = null;
            try
            {
                svc = Dlss5Service.Shared;
            }
            catch { }
            if (svc == null)
            {
                try { lock (Sync) { _worker = null; } } catch { }
                return;
            }
            Image motion = null;

            try
            {
                while (!_shutdown)
                {
                    if (svc.Disposed || _shutdown)
                    {
                        break;
                    }
                    try
                    {
                        Profile profile = null;
                        try
                        {
                            profile = ProfileManager.CurrentProfile;
                        }
                        catch { }
                        if (_shutdown || svc.Disposed)
                        {
                            break;
                        }
                        if (profile == null || !profile.Dlss5)
                        {
                            lock (Sync)
                            {
                                try { _pending?.Dispose(); } catch { }
                                _pending = null;
                                _pendingGen = -1;
                            }
                            Interlocked.Exchange(ref _drop, 0);
                            Interlocked.Exchange(ref _busy, 0);
                            Thread.Sleep(100);
                            continue;
                        }

                    Image work = null;
                    int workW = 0, workH = 0;
                    Rectangle workDest = default;
                    Rectangle workCursor = default;
                    long workGen = -1;
                    lock (Sync)
                    {
                        work = _pending;
                        _pending = null;
                        workW = _pendingW;
                        workH = _pendingH;
                        workDest = _pendingDest;
                        workCursor = _pendingCursor;
                        workGen = _pendingGen;
                        _pendingGen = -1;
                    }
                    if (work == null)
                    {
                        Thread.Sleep(25);
                        continue;
                    }
                    if (Interlocked.Exchange(ref _drop, 0) == 1)
                    {
                        try { work.Dispose(); } catch { }
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }
                    if (workGen != Interlocked.Read(ref _generation))
                    {
                        try { work.Dispose(); } catch { }
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }

                    Interlocked.Exchange(ref _busy, 1);
                    try
                    {
                        if (motion == null)
                        {
                            try
                            {
                                motion = Dlss5Service.ZeroMotion320();
                            }
                            catch
                            {
                                motion = null;
                            }
                        }

                        // Frame bytes on the worker (off the 2ms frame
                        // budget): the viewport image converts to RGB8 here,
                        // then tiles at Stride 288 with the linear-ramp blend.
                        int w = workW > 0 ? workW : work.GetWidth();
                        int h = workH > 0 ? workH : work.GetHeight();
                        byte[] baseRgb = null;
                        try
                        {
                            if (work.GetFormat() != Image.Format.Rgb8)
                            {
                                work.Convert(Image.Format.Rgb8);
                            }
                            byte[] data = work.GetData();
                            if (data != null && data.Length >= w * h * 3)
                            {
                                baseRgb = new byte[w * h * 3];
                                Buffer.BlockCopy(data, 0, baseRgb, 0, baseRgb.Length);
                            }
                        }
                        catch { }
                        try { work.Dispose(); } catch { }
                        work = null;
                        if (baseRgb == null)
                        {
                            Interlocked.Exchange(ref _nextFrame, Interlocked.Read(ref _framesSeen) + MaxSkipFrames);
                            continue;
                        }
                        if (workGen != Interlocked.Read(ref _generation))
                        {
                            Interlocked.Increment(ref _dropped);
                            continue;
                        }

                        DlssBakeTiles.Placement[] placements;
                        try
                        {
                            placements = DlssBakeTiles.ComputePlacements(w, h);
                        }
                        catch
                        {
                            continue;
                        }
                        if (placements == null || placements.Length == 0)
                        {
                            continue;
                        }

                        float[] accum = null;
                        float[] weights = null;
                        try
                        {
                            accum = new float[w * h * 3];
                            weights = new float[w * h];
                        }
                        catch
                        {
                            // Viewport too large for float accumulators:
                            // back off and stay pass-through this round.
                            Interlocked.Exchange(ref _nextFrame, Interlocked.Read(ref _framesSeen) + _skipFrames);
                            continue;
                        }

                        bool stale = false;
                        bool cpu = false;
                        bool anyFailed = false;
                        int doneTiles = 0;
                        double totalMs = 0;
                        foreach (DlssBakeTiles.Placement p in placements)
                        {
                            // Generation-guard every tile: a scene cut,
                            // resize, teleport or toggle flip mid-enhance
                            // abandons the frame so stale pixels never draw.
                            // Shutdown/dispose aborts the same way so exit
                            // never runs the native session while it is freed.
                            if (_shutdown || svc.Disposed ||
                                workGen != Interlocked.Read(ref _generation) ||
                                Interlocked.CompareExchange(ref _drop, 0, 0) == 1)
                            {
                                stale = true;
                                break;
                            }
                            byte[] tileIn = null;
                            try
                            {
                                tileIn = DlssBakeTiles.ExtractTile(baseRgb, w, h, p);
                            }
                            catch
                            {
                                anyFailed = true;
                                continue;
                            }
                            if (TileOverlapsCursor(p, workCursor))
                            {
                                // Cursor exclusion (cheap): tiles under the
                                // cursor never run the network, so the moving
                                // cursor cannot smear temporal history; they
                                // blend back as native-sharp passthrough.
                                try
                                {
                                    DlssBakeTiles.Accumulate(accum, weights, w, h, tileIn, p);
                                    doneTiles++;
                                }
                                catch
                                {
                                    anyFailed = true;
                                }
                                continue;
                            }
                            if (_shutdown || svc.Disposed || workGen != Interlocked.Read(ref _generation))
                            {
                                stale = true;
                                break;
                            }
                            byte[] tileOut = EnhanceOneTemporal(svc, tileIn, motion);
                            _lastMs = svc.LastMs;
                            _provider = svc.Provider ?? "";
                            if (_provider == "cpu")
                            {
                                cpu = true;
                                break;
                            }
                            if (tileOut == null)
                            {
                                anyFailed = true;
                                if (doneTiles == 0)
                                {
                                    // First tile failed: model missing or
                                    // native error. Cool down and stay idle
                                    // rather than passthrough-looping every
                                    // tile each round.
                                    break;
                                }
                                try
                                {
                                    DlssBakeTiles.Accumulate(accum, weights, w, h, tileIn, p);
                                    doneTiles++;
                                }
                                catch { }
                                continue;
                            }
                            totalMs += svc.LastMs;
                            try
                            {
                                DlssBakeTiles.Accumulate(accum, weights, w, h, tileOut, p);
                                doneTiles++;
                            }
                            catch
                            {
                                anyFailed = true;
                            }
                        }

                        _lastMs = totalMs;
                        _provider = svc.Provider ?? "";
                        if (_provider.Length > 0 && !_loggedProvider)
                        {
                            _loggedProvider = true;
                            try
                            {
                                Log.Info($"[DLSS5] temporal warm: provider={_provider} last_ms={_lastMs:F0} tiles={placements.Length} frame={w}x{h} (full-viewport composite).");
                            }
                            catch { }
                        }

                        if (cpu)
                        {
                            // CPU inference (~seconds/tile) can never hold a
                            // frame rate: pass through and stop readbacks.
                            // No composite is retained.
                            _cpuPassthrough = true;
                            Interlocked.Increment(ref _enhanced);
                            if (!_loggedCpu)
                            {
                                _loggedCpu = true;
                                try
                                {
                                    Log.Warn($"[DLSS5] frame hook pass-through on cpu ({svc.LastMs:F0} ms/tile).");
                                }
                                catch { }
                            }
                            continue;
                        }
                        if (stale || workGen != Interlocked.Read(ref _generation))
                        {
                            Interlocked.Increment(ref _dropped);
                            continue;
                        }
                        if (doneTiles == 0)
                        {
                            // Missing dll/model or enhance error: cool down,
                            // log once, keep the toggle harmless.
                            // No composite is retained (pass-through).
                            Interlocked.Exchange(ref _nextFrame, Interlocked.Read(ref _framesSeen) + MaxSkipFrames);
                            if (!_loggedNoModel)
                            {
                                _loggedNoModel = true;
                                string why = svc.LastError ?? "";
                                if (why.Length > 200)
                                {
                                    why = why.Substring(0, 200);
                                }
                                try
                                {
                                    Log.Warn("[DLSS5] temporal unavailable, hook idle: " + why);
                                }
                                catch { }
                            }
                            continue;
                        }

                        byte[] outRgb = null;
                        try
                        {
                            outRgb = DlssBakeTiles.Normalize(accum, weights, w, h, baseRgb);
                        }
                        catch { }
                        if (outRgb == null || outRgb.Length < w * h * 3)
                        {
                            continue;
                        }
                        if (workGen != Interlocked.Read(ref _generation))
                        {
                            Interlocked.Increment(ref _dropped);
                            continue;
                        }
                        lock (Sync)
                        {
                            if (workGen == Interlocked.Read(ref _generation))
                            {
                                _compositeRgb = outRgb;
                                _compositeW = w;
                                _compositeH = h;
                                _compositeDest = workDest;
                                _compositeGen = workGen;
                            }
                            else
                            {
                                Interlocked.Increment(ref _dropped);
                                continue;
                            }
                        }
                        Interlocked.Increment(ref _enhanced);
                        if (anyFailed)
                        {
                            try { Log.Warn("[DLSS5] full-viewport enhance partial-fallback (some tiles passthrough)."); } catch { }
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                }
                catch
                {
                    try
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                    catch { }
                    if (_shutdown || svc.Disposed)
                    {
                        break;
                    }
                    try
                    {
                        Thread.Sleep(100);
                    }
                    catch { }
                }
                }
            }
            catch { }
            finally
            {
                try { Interlocked.Exchange(ref _busy, 0); } catch { }
                try
                {
                    lock (Sync)
                    {
                        try { _pending?.Dispose(); } catch { }
                        _pending = null;
                        _pendingGen = -1;
                        _worker = null;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// One 320 tile through the temporal path with the shared zero-motion
        /// stub (static frame). Returns the enhanced RGB8 bytes, or null on
        /// any failure (caller falls back to passthrough). Never throws.
        /// Real motion RGF feeds here once the renderer produces velocity.
        /// </summary>
        private static byte[] EnhanceOneTemporal(Dlss5Service svc, byte[] tileRgb, Image motion)
        {
            if (_shutdown || svc == null || svc.Disposed || tileRgb == null || tileRgb.Length < Tile * Tile * 3 || motion == null)
            {
                return null;
            }
            try
            {
                if (!GodotObject.IsInstanceValid(motion))
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
            Image img = null;
            Image enhanced = null;
            try
            {
                img = Image.CreateFromData(Tile, Tile, false, Image.Format.Rgb8, tileRgb);
                if (img == null)
                {
                    return null;
                }
                try
                {
                    enhanced = svc.EnhanceTemporal(img, motion, 1.0f, 0);
                }
                catch
                {
                    return null;
                }
                if (enhanced == null)
                {
                    return null;
                }
                if (enhanced.GetWidth() != Tile || enhanced.GetHeight() != Tile)
                {
                    return null;
                }
                if (enhanced.GetFormat() != Image.Format.Rgb8)
                {
                    try { enhanced.Convert(Image.Format.Rgb8); } catch { return null; }
                }
                byte[] data = null;
                try { data = enhanced.GetData(); } catch { return null; }
                if (data == null || data.Length < Tile * Tile * 3)
                {
                    return null;
                }
                var copy = new byte[Tile * Tile * 3];
                Buffer.BlockCopy(data, 0, copy, 0, copy.Length);
                return copy;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { img?.Dispose(); } catch { }
                try { enhanced?.Dispose(); } catch { }
            }
        }
    }
}
