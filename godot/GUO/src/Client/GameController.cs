// SPDX-License-Identifier: BSD-2-Clause

using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Network;
using GUO.Network.Encryption;
using GUO.Renderer;
using GUO.Resources;
using GUO.Utility;
using GUO.Utility.Logging;
using GUO.Compat;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace GUO
{
    /// <summary>
    /// The client's root object: it owns the scene, the audio manager, the
    /// loaded UO data, and the update/draw loop.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream derives from FNA's
    /// <c>Microsoft.Xna.Framework.Game</c>, which owns the process -- it
    /// creates the window and the graphics device, and <c>Game.Run()</c> is a
    /// loop that does not return until the client exits. Godot owns all of
    /// that, so this becomes a <see cref="Node2D"/> in Godot's tree:
    ///
    ///   * <c>Initialize</c> + <c>LoadContent</c> become <c>_Ready</c>.
    ///   * <c>Update</c> and <c>Draw</c> become one <c>_Process</c>, called in
    ///     that order so the frame is identical to FNA's.
    ///   * <c>UnloadContent</c> + <c>OnExiting</c> become <c>_ExitTree</c>.
    ///   * There is no <c>GraphicsDeviceManager</c> and no
    ///     <c>GraphicsDevice</c>; the window is <see cref="DisplayServer"/>
    ///     and the drawing surface is this node's canvas item.
    ///
    /// Node2D rather than Node because <see cref="UltimaBatcher2D"/> draws
    /// into a canvas item, and only a CanvasItem has one.
    /// </remarks>
    internal sealed class GameController : Node2D
    {
        private bool _ignoreNextTextInput;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
        private UltimaBatcher2D _uoSpriteBatch;
        private readonly RenderTargets _renderTargets = new();
        private readonly RenderLists _renderLists = new();
        private bool _suppressedDraw;
        private bool _pluginsInitialized = false;
        private float _displayScale;

        // PORT DEVIATION (GUO): FNA's GameTime. Godot hands _Process a delta
        // and keeps no running total that starts when the client does.
        private double _totalGameTime;

        public GameController(IPluginHost pluginHost)
        {
            SetVSync(false);

            Window = new GameWindow();
            Window.AllowUserResizing = true;
            Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            PluginHost = pluginHost;
        }

        public Scene Scene { get; private set; }
        public AudioManager Audio { get; private set; }
        public UltimaOnline UO { get; } = new UltimaOnline();
        public IPluginHost PluginHost { get; private set; }

        /// <summary>The OS window, in the shape upstream's call sites expect.</summary>
        public GameWindow Window { get; }

        /// <summary>
        /// Whether the client has focus. Upstream reads FNA's
        /// <c>Game.IsActive</c>; the profile setting ReduceFPSWhenInactive and
        /// the audio manager both hang off it.
        /// </summary>
        public bool IsActive => DisplayServer.WindowIsFocused();

        /// <summary>
        /// PORT DEVIATION (GUO): FNA's <c>Game.IsMouseVisible</c> hides the OS
        /// cursor so the client can draw its own. Godot's equivalent is the
        /// input mouse mode.
        /// </summary>
        public bool IsMouseVisible
        {
            get => Godot.Input.MouseMode == Godot.Input.MouseModeEnum.Visible;
            set =>
                Godot.Input.MouseMode = value
                    ? Godot.Input.MouseModeEnum.Visible
                    : Godot.Input.MouseModeEnum.Hidden;
        }

        public Rectangle ClientBounds
        {
            get
            {
                var window_rectangle = Window.ClientBounds;
                return new Rectangle(
                    window_rectangle.X,
                    window_rectangle.Y,
                    (int)((float)(window_rectangle.Width) / DpiScale),
                    (int)((float)(window_rectangle.Height) / DpiScale)
                );
            }
        }

        public readonly uint[] FrameDelay = new uint[2];

        private readonly List<(uint, Action)> _queuedActions = new ();

        public void EnqueueAction(uint time, Action action)
        {
            _queuedActions.Add((Time.Ticks + time, action));
        }

        public override void _Ready()
        {
            Initialize();
            LoadContent();
        }

        private void Initialize()
        {
            SetRefreshRate(Settings.GlobalSettings.FPS);
            _uoSpriteBatch = new UltimaBatcher2D(GetCanvasItem());

            // PORT GAP (GUO): upstream installs an SDL event filter here
            // (SDL_SetEventFilter(HandleSdlEvent)) and does all of its input
            // in it -- mouse, keyboard, text input, window events, drag and
            // drop. Godot delivers input through _Input/_UnhandledInput and
            // window changes through notifications, so that filter is not
            // ported: the Godot input layer under src/Input replaces it
            // wholesale, and until it lands the client takes no input. The
            // same goes for TextInputEXT.StartTextInput, whose Godot
            // equivalent is per-control.

            _displayScale = DpiScale;
        }

        private void LoadContent()
        {
            Fonts.Initialize();
            Audio = new AudioManager();

            var bytes = Loader.GetBackgroundImage().ToArray();
            _renderTargets.InitializeBackground(TextureFromPng(bytes));

            UO.Load(this);
            Audio.Initialize();
            // TODO: temporary fix to avoid crash when laoding plugins
            Settings.GlobalSettings.Encryption = (byte) NetClient.Socket.Load(UO.FileManager.Version, (EncryptionType) Settings.GlobalSettings.Encryption);

            Log.Trace("Loading plugins...");
            PluginHost?.Initialize();

            foreach (string p in Settings.GlobalSettings.Plugins)
            {
                Plugin.Create(p);
            }
            _pluginsInitialized = true;

            Log.Trace("Done!");

            SetScene(new LoginScene(UO.World));

            SetWindowPositionBySettings();
        }

        /// <summary>
        /// PORT DEVIATION (GUO): upstream's <c>Texture2D.FromStream</c>, which
        /// is FNA decoding a PNG against the device.
        /// </summary>
        internal static Texture2D TextureFromPng(byte[] png)
        {
            var image = new Image();

            Error error = image.LoadPngFromBuffer(png);

            if (error != Error.Ok)
            {
                throw new InvalidDataException($"could not decode PNG: {error}");
            }

            return ImageTexture.CreateFromImage(image);
        }

        /// <summary>
        /// Hands the batcher the two palette textures UltimaOnline builds.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream binds them to device sampler slots
        /// 1 and 2, which every shader then sees. Godot has no such slots --
        /// see UltimaOnline.Load.
        /// </remarks>
        public void SetHueTextures(Texture2D hues, Texture2D lights)
        {
            _uoSpriteBatch.HueTexture = hues;
            _uoSpriteBatch.LightTexture = lights;
        }

        public override void _ExitTree()
        {
            UnloadContent();

            Scene?.Dispose();
        }

        private void UnloadContent()
        {
            // PORT DEVIATION (GUO): upstream subtracts the window border size
            // from the window position before saving it, because SDL reports
            // the client area's position and expects the frame's back.
            // DisplayServer.WindowGetPosition is already the position that
            // WindowSetPosition takes, so there is nothing to correct for.
            Settings.GlobalSettings.WindowPosition = new Point(
                Math.Max(0, Window.ClientBounds.X),
                Math.Max(0, Window.ClientBounds.Y)
            );

            Audio?.StopMusic();
            Settings.GlobalSettings.Save();
            Plugin.OnClosing();

            UO.Unload();
        }

        public void Exit()
        {
            GetTree().Quit();
        }

        public void SetWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
#if DEV_BUILD
                Window.Title = $"ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
#endif
            }
            else
            {
#if DEV_BUILD
                Window.Title = $"{title} - ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"{title} - ClassicUO - {CUOEnviroment.Version}";
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetScene<T>() where T : Scene
        {
            return Scene as T;
        }

        public void SetScene(Scene scene)
        {
            Scene?.Dispose();
            Scene = scene;
            Scene?.Load();
        }

        public void SetVSync(bool value)
        {
            DisplayServer.WindowSetVsyncMode(
                value ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled
            );
        }

        public void SetRefreshRate(int rate)
        {
            if (rate < Constants.MIN_FPS)
            {
                rate = Constants.MIN_FPS;
            }
            else if (rate > Constants.MAX_FPS)
            {
                rate = Constants.MAX_FPS;
            }

            float frameDelay;

            if (rate == Constants.MIN_FPS)
            {
                // The "real" UO framerate is 12.5. Treat "12" as "12.5" to match.
                frameDelay = 80;
            }
            else
            {
                frameDelay = 1000.0f / rate;
            }

            FrameDelay[0] = FrameDelay[1] = (uint)frameDelay;
            FrameDelay[1] = FrameDelay[1] >> 1;

            Settings.GlobalSettings.FPS = rate;

            _intervalFixedUpdate[0] = frameDelay;
            _intervalFixedUpdate[1] = 217; // 5 FPS

            // PORT DEVIATION (GUO): upstream runs the FNA loop uncapped and
            // paces itself by hand, sleeping 1ms whenever a frame arrives
            // early. Godot paces the loop, so the cap is set here and the
            // sleep is gone -- see Update for what is left of the throttle.
            Engine.MaxFps = rate;
        }

        private void SetWindowPosition(int x, int y)
        {
            DisplayServer.WindowSetPosition(new Vector2I(x, y));
        }

        public void SetWindowSize(int width, int height)
        {
            DisplayServer.WindowSetSize(new Vector2I(width, height));
        }

        public void SetWindowBorderless(bool borderless)
        {
            if (DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless) == borderless)
            {
                return;
            }

            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, borderless);

            int screen = DisplayServer.WindowGetCurrentScreen();
            Vector2I screenSize = DisplayServer.ScreenGetSize(screen);

            int width = screenSize.X;
            int height = screenSize.Y;

            if (borderless)
            {
                SetWindowSize(width, height);

                Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
                DisplayServer.WindowSetPosition(usable.Position);
            }
            else
            {
                // PORT DEVIATION (GUO): upstream shrinks the window by the
                // difference between the top and bottom border thicknesses,
                // which it gets from SDL_GetWindowBordersSize. Godot does not
                // report border sizes; the usable rect is the same fact
                // stated as a rectangle, so the window takes it directly.
                Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);

                SetWindowSize(usable.Size.X, usable.Size.Y);
                SetWindowPositionBySettings();
            }

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        public void MaximizeWindow()
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        }

        public bool IsWindowMaximized()
        {
            return DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized;
        }

        public void RestoreWindow()
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }

        public void SetWindowPositionBySettings()
        {
            if (!Settings.GlobalSettings.WindowPosition.HasValue)
            {
                return;
            }

            int x = Math.Max(0, Settings.GlobalSettings.WindowPosition.Value.X);
            int y = Math.Max(0, Settings.GlobalSettings.WindowPosition.Value.Y);

            // Make sure the window is actually in view and not out of bounds.
            int screen = DisplayServer.GetScreenFromRect(
                new Rect2(new Vector2(x, y), Vector2.One)
            );

            if (screen < 0)
            {
                screen = DisplayServer.WindowGetCurrentScreen();
            }

            Rect2I displayBounds = DisplayServer.ScreenGetUsableRect(screen);

            if (x < displayBounds.Position.X || x >= displayBounds.Position.X + displayBounds.Size.X)
            {
                x = displayBounds.Position.X;
            }

            if (y < displayBounds.Position.Y || y >= displayBounds.Position.Y + displayBounds.Size.Y)
            {
                y = displayBounds.Position.Y;
            }

            SetWindowPosition(x, y);
        }

        public override void _Process(double delta)
        {
            double elapsedMilliseconds = delta * 1000.0;

            _totalGameTime += delta;

            Update(elapsedMilliseconds);

            if (!_suppressedDraw)
            {
                Draw();
            }
        }

        private void Update(double elapsedMilliseconds)
        {
            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Time.Ticks = (uint)(_totalGameTime * 1000.0);
            Time.Delta = (float)(elapsedMilliseconds / 1000.0);

            Mouse.Update();

            var data = NetClient.Socket.CollectAvailableData();
            var packetsCount = PacketHandlers.Handler.ParsePackets(NetClient.Socket, UO.World, data);

            NetClient.Socket.Statistics.TotalPacketsReceived += (uint)packetsCount;
            NetClient.Socket.Flush();

            Plugin.Tick();

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Profiler.EnterContext(Profiler.ProfilerContext.UPDATE_WORLD);
                Scene.Update();
                Profiler.ExitContext(Profiler.ProfilerContext.UPDATE_WORLD);
            }

            UIManager.Update();

            _totalElapsed += elapsedMilliseconds;
            _currentFpsTime += elapsedMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

            double x = _intervalFixedUpdate[
                !IsActive
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.ReduceFPSWhenInactive
                    ? 1
                    : 0
            ];
            _suppressedDraw = false;

            if (_totalElapsed > x)
            {
                _totalElapsed %= x;
            }
            else
            {
                // PORT DEVIATION (GUO): upstream also calls SuppressDraw() and
                // sleeps a millisecond here. _Process is Godot's main thread;
                // sleeping in it stalls the engine, not just the client. The
                // flag alone still does the job it is here for -- skipping the
                // draw when the window is inactive and the profile asks for a
                // slower frame -- and Engine.MaxFps does the pacing.
                _suppressedDraw = true;
            }

            UO.GameCursor?.Update();
            Audio?.Update();


            for (var i = _queuedActions.Count - 1; i >= 0; i--)
            {
                (var time, var fn) = _queuedActions[i];

                if (Time.Ticks > time)
                {
                    fn();
                    _queuedActions.RemoveAt(i);
                    break;
                }
            }
        }

        private void Draw()
        {
            Rectangle windowBounds = Window.ClientBounds;

            _renderTargets.EnsureSizes(
                this,
                new Rectangle(0, 0, windowBounds.Width, windowBounds.Height),
                Scene.Camera.Bounds,
                DpiScale
            );

            Profiler.EndFrame();
            Profiler.BeginFrame();

            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME);

            _totalFrames++;

            // PORT DEVIATION (GUO): upstream clears the back buffer to black
            // here. There is no back buffer -- the batcher's canvas items are
            // discarded and rebuilt each frame, which is the same fact, and
            // RenderTargets.Draw covers the window with the tiled background
            // before anything else lands on it.
            _uoSpriteBatch.BeginFrame();

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.Draw(_uoSpriteBatch, _renderTargets);
            }

            _uoSpriteBatch.SetRenderTarget(_renderTargets.UiRenderTarget);
            _uoSpriteBatch.Clear(Color.Transparent);

            if ((UO.World?.InGame ?? false) && SelectedObject.Object is TextObject t)
            {
                if (t.IsTextGump)
                {
                    t.ToTopD();
                }
                else
                {
                    UO.World.WorldTextManager?.MoveToTop(t);
                }
            }

            SelectedObject.HealthbarObject = null;
            SelectedObject.SelectedContainer = null;

            _uoSpriteBatch.Begin();
            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.DrawUI(_uoSpriteBatch);
            }
            _uoSpriteBatch.End();

            UIManager.Draw(_uoSpriteBatch);

            _uoSpriteBatch.Begin();
            UO.GameCursor?.Draw(_uoSpriteBatch);
            _uoSpriteBatch.End();

            _uoSpriteBatch.SetRenderTarget(null);

            _renderTargets.Draw(_uoSpriteBatch);

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME);
            Profiler.EnterContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);

            Plugin.ProcessDrawCmdList();
        }

        private float _screenScale = Settings.GlobalSettings.ScreenScale;
        public float ScreenScale {
            get => _screenScale;
            set {
                if (value != _screenScale) {
                    _screenScale = value;
                    UO.GameCursor?.CreateGraphic(DpiScale);
                }
            }
        }

        /// <summary>
        /// PORT DEVIATION (GUO): upstream reads
        /// <c>SDL_GetWindowDisplayScale</c>, which is the scale of the display
        /// the window is currently on. Godot states the same fact per screen.
        /// </summary>
        public float DpiScale
        {
            get => DisplayServer.ScreenGetScale(DisplayServer.WindowGetCurrentScreen()) * ScreenScale;
        }

        public int ScaleWithDpi(int value, float previousDpi = 1)
        {
            return (int)Math.Round((value / previousDpi) * DpiScale);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationWMSizeChanged)
            {
                Vector2I size = DisplayServer.WindowGetSize();

                WindowOnClientSizeChanged(size.X, size.Y);
            }
        }

        private void WindowOnClientSizeChanged(int width, int height)
        {
            if (!IsWindowMaximized() && Window.AllowUserResizing)
            {
                if (ProfileManager.CurrentProfile != null)
                    ProfileManager.CurrentProfile.WindowClientBounds = new Point(width, height);
            }

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        private void TakeScreenshot()
        {
            string screenshotsFolder = FileSystemHelper.CreateFolderIfNotExists(
                CUOEnviroment.ExecutablePath,
                "Data",
                "Client",
                "Screenshots"
            );

            string path = Path.Combine(
                screenshotsFolder,
                $"screenshot_{DateTime.Now:yyyy-MM-dd_hh-mm-ss}.png"
            );

            // PORT DEVIATION (GUO): upstream reads the back buffer into a
            // Color[], wraps it in a Texture2D and calls SaveAsPng. Godot's
            // viewport hands back the finished frame as an Image, which saves
            // itself.
            Image image = GetViewport().GetTexture().GetImage();

            Error error = image.SavePng(path);

            if (error != Error.Ok)
            {
                Log.Error($"could not save screenshot to '{path}': {error}");

                return;
            }

            string message = string.Format(ResGeneral.ScreenshotStoredIn0, path);

            if (
                ProfileManager.CurrentProfile == null
                || ProfileManager.CurrentProfile.HideScreenshotStoredInMessage
            )
            {
                Log.Info(message);
            }
            else
            {
                GameActions.Print(UO.World, message, 0x44, MessageType.System);
            }
        }

        /// <summary>
        /// The OS window, in the shape upstream's call sites expect of FNA's
        /// <c>GameWindow</c>.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): FNA hands the game a GameWindow object.
        /// Godot's window is <see cref="DisplayServer"/>, a static, so this is
        /// a thin front for it rather than something that owns state. It
        /// exists so the ~15 <c>Client.Game.Window.X</c> call sites across the
        /// client port unchanged.
        /// </remarks>
        internal sealed class GameWindow
        {
            /// <summary>
            /// The native HWND (or X11/Wayland equivalent). Only the plugin
            /// host and UoAssist want it, and only to hand to the OS.
            /// </summary>
            public IntPtr Handle =>
                (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);

            public Rectangle ClientBounds
            {
                get
                {
                    Vector2I position = DisplayServer.WindowGetPosition();
                    Vector2I size = DisplayServer.WindowGetSize();

                    return new Rectangle(position.X, position.Y, size.X, size.Y);
                }
            }

            public bool AllowUserResizing
            {
                get => !DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.ResizeDisabled);
                set => DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.ResizeDisabled, !value);
            }

            public string Title
            {
                set => DisplayServer.WindowSetTitle(value);
            }
        }
    }
}
