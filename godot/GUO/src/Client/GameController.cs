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
using Color = GUO.Compat.Color;
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
    internal sealed partial class GameController : Node2D
    {
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
        private UltimaBatcher2D _uoSpriteBatch;
        private readonly RenderTargets _renderTargets = new();
        private readonly RenderLists _renderLists = new();
        private bool _suppressedDraw;
        private bool _pluginsInitialized = false;

        /// <summary>
        /// PORT DEVIATION (GUO): a window position given on the command line.
        /// It wins over the saved one and is never saved, so a scripted run
        /// that tiles four clients across a screen leaves the next ordinary
        /// launch where the user last put it.
        /// </summary>
        public static Vector2I? PinnedWindowPosition { get; set; }

        /// <summary>
        /// PORT DEVIATION (GUO): a window size given on the command line. It
        /// wins over the saved size and the saved maximised state when the
        /// game scene sizes the window, and neither is saved back.
        /// </summary>
        public static Vector2I? PinnedWindowSize { get; set; }

        /// <summary>
        /// Applies PinnedWindowSize; false when there is none, so the caller
        /// sizes the window from the settings as upstream does.
        /// </summary>
        public bool SetPinnedWindowSize()
        {
            if (!PinnedWindowSize.HasValue)
            {
                return false;
            }

            RestoreWindow();
            SetWindowSize(PinnedWindowSize.Value.X, PinnedWindowSize.Value.Y);
            SetWindowPositionBySettings();

            return true;
        }
        private float _displayScale;
        private Texture2D _hueTexture, _lightTexture;

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

        // PORT DEVIATION (GUO): an embedded controller for the editor's World
        // tab (docs/architecture/ADR-0015-editor-world-view.md). Upstream has
        // one controller that owns the OS window; the editor needs the same
        // renderer drawing into a viewport of its own, with no window, audio,
        // socket, plugins or login. This block is the whole difference: the
        // constructor leaves the window alone, LoadEmbedded is LoadContent
        // without those, and DrawEmbedded is DrawFrame for the world only,
        // sized by the host rather than DisplayServer. The node is never
        // added to a tree, so _Ready, _Process and _Input never run.
        //
        // Window stays null on purpose. The window is the editor's, and the
        // one thing that would use it here is World's UoAssist, which then
        // publishes a UOAssist message window for Razor on the editor's
        // handle and registers a window class whose procedure is a delegate
        // in this assembly -- never unregistered, so after an assembly reload
        // the next World calls into freed code and the CLR dies. With no
        // Window, UoAssist does what it does on a non-Windows OS: nothing.
        internal GameController(bool embedded)
        {
        }

        internal void LoadEmbedded(CanvasItem host)
        {
            _uoSpriteBatch = new UltimaBatcher2D(host.GetCanvasItem());
            _displayScale = 1f;
            Fonts.Initialize();
            _renderTargets.InitializeBackground(TextureFromPng(Loader.GetBackgroundImage().ToArray()));
            UO.Load(this);
        }

        internal void SetEmbeddedScene(Scene scene) => Scene = scene;

        internal void DrawEmbedded(Node host, Rectangle bounds)
        {
            if (Scene == null || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            _renderTargets.EnsureSizes(host, bounds, Scene.Camera.Bounds, 1f);
            _uoSpriteBatch.BeginFrame();
            Scene.Draw(_uoSpriteBatch, _renderTargets);
            _uoSpriteBatch.SetRenderTarget(null);
            _renderTargets.Draw(_uoSpriteBatch);
        }

        internal void UnloadEmbedded()
        {
            Scene = null;
            UO.Unload();
            _uoSpriteBatch?.Dispose();
            _uoSpriteBatch = null;
            _hueTexture?.Dispose();
            _hueTexture = null;
            _lightTexture?.Dispose();
            _lightTexture = null;
            TextureAtlas.DisposeAll();
            SolidColorTextureCache.Clear();
            _renderTargets.Dispose();
        }
        // END PORT DEVIATION (GUO)

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
        /// <remarks>
        /// PORT DEVIATION (GUO): a scripted run keeps its window unfocusable
        /// (Main.NoFocus) so it cannot take the keyboard from whoever is
        /// working at the desktop. Such a window is never focused, and read
        /// literally that would put every probe on ReduceFPSWhenInactive's
        /// 5 Hz tick. The thing driving it is in-process, so it counts as
        /// active.
        /// </remarks>
        public bool IsActive => DisplayServer.WindowIsFocused() || GUO.Host.Main.NoFocus;

        /// <summary>
        /// Raised when the window takes and loses focus. Upstream gets these
        /// from FNA's <c>Game</c>; the audio manager is the only subscriber,
        /// and it uses them to duck the music.
        /// </summary>
        public event EventHandler Activated;

        /// <inheritdoc cref="Activated"/>
        public event EventHandler Deactivated;

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

        /// <summary>
        /// PORT DEVIATION (GUO): the node that draws what is behind the world
        /// and the gumps (ADR-0016); null until LoadContent.
        /// </summary>
        internal CanvasBackground CanvasBackground { get; private set; }

        private readonly List<(uint, Action)> _queuedActions = new ();

        public void EnqueueAction(uint time, Action action)
        {
            _queuedActions.Add((Time.Ticks + time, action));
        }

        public override void _Ready()
        {
            // PORT DEVIATION (GUO): upstream learns about a resize from
            // SDL_EVENT_WINDOW_RESIZED. NotificationWMSizeChanged is Godot's
            // nearest equivalent, but it reaches the Window node, not an
            // arbitrary node in the tree, so on its own the client never heard
            // about a resize it had asked for itself -- it maximised the
            // window on entering the world and went on laying the UI out for
            // the 640x480 it started at. The viewport's size_changed signal is
            // the one that actually arrives.
            GetViewport().SizeChanged += OnViewportSizeChanged;

            Initialize();
            LoadContent();
        }

        private void OnViewportSizeChanged()
        {
            Vector2I size = DisplayServer.WindowGetSize();

            WindowOnClientSizeChanged(size.X, size.Y);
        }

        private void Initialize()
        {
            SetRefreshRate(Settings.GlobalSettings.FPS);
            _uoSpriteBatch = new UltimaBatcher2D(GetCanvasItem());

            // PORT DEVIATION (GUO): upstream installs an SDL event filter
            // here (SDL_SetEventFilter(HandleSdlEvent)) and does all of its
            // input in it. There is nothing to install: Godot delivers input
            // to _Input and window changes to _Notification, and
            // src/Input/GodotInput.cs replaces the filter wholesale. ADR-0006.
            // TextInputEXT.StartTextInput goes the same way -- Godot has no
            // global text-input mode to turn on.

            _displayScale = DpiScale;
        }

        private void LoadContent()
        {
            Fonts.Initialize();
            Audio = new AudioManager();

            var bytes = Loader.GetBackgroundImage().ToArray();
            _renderTargets.InitializeBackground(TextureFromPng(bytes));

            // PORT DEVIATION (GUO): the profile's canvas background, a layer
            // under this node's canvas; it tells the render targets when it
            // has replaced upstream's tile. ADR-0016.
            CanvasBackground = new CanvasBackground();
            AddChild(CanvasBackground);
            _renderTargets.SetBackgroundReplaced(() => CanvasBackground.Active);

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

            // PORT DEVIATION (GUO): the touch layer's whole-number screen
            // scale has to be in place before the first scene lays itself
            // out against ClientBounds, or the login screen is centred for a
            // client size that changes a frame later. Off the layer: one
            // false test. ADR-0017.
            if (GUO.Input.Touch.TouchInput.Enabled)
            {
                GUO.Input.Touch.TouchInput.ApplyScreenScale(GUO.Input.Touch.TouchInput.RequestedScale);
            }

            SetScene(new LoginScene(UO.World));

            SetWindowPositionBySettings();
            PullWindowOntoAScreen();
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
            // Kept so they can be freed at exit. They are set on the shader
            // materials and nothing else holds them, so without a reference
            // here they outlive the rendering server and Godot reports them
            // as leaked.
            _hueTexture = hues;
            _lightTexture = lights;

            _uoSpriteBatch.HueTexture = hues;
            _uoSpriteBatch.LightTexture = lights;
        }

        public override void _ExitTree()
        {
            GetViewport().SizeChanged -= OnViewportSizeChanged;

            UnloadContent();

            Scene?.Dispose();

            // PORT DEVIATION (GUO): upstream has nothing here. FNA's device
            // owns every resource the batcher uses and tears them down with
            // itself; here the batcher holds RenderingServer RIDs, and Godot
            // reports each one still alive at exit as a leak. Freeing them is
            // the equivalent of the device going away.
            _uoSpriteBatch?.Dispose();
            _uoSpriteBatch = null;

            _hueTexture?.Dispose();
            _hueTexture = null;
            _lightTexture?.Dispose();
            _lightTexture = null;

            TextureAtlas.DisposeAll();
            SolidColorTextureCache.Clear();
            _renderTargets.Dispose();

            // PORT DEVIATION (GUO): upstream frees its SDL_Cursors in
            // GameCursor's own teardown. Godot's custom cursor is set on the
            // Input singleton, which keeps its own texture for it and still
            // holds it when the rendering server shuts down -- the client's
            // last leaked RID at exit. Putting the arrow back releases it.
            Godot.Input.SetCustomMouseCursor(null);
        }

        private void UnloadContent()
        {
            // PORT DEVIATION (GUO): upstream subtracts the window border size
            // from the window position before saving it, because SDL reports
            // the client area's position and expects the frame's back.
            // DisplayServer.WindowGetPosition is already the position that
            // WindowSetPosition takes, so there is nothing to correct for.
            if (!PinnedWindowPosition.HasValue)
            {
                Settings.GlobalSettings.WindowPosition = new Point(
                    Math.Max(0, Window.ClientBounds.X),
                    Math.Max(0, Window.ClientBounds.Y)
                );
            }

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

        // PORT DEVIATION (GUO): a phone has one window mode that matters.
        // Godot's Android display server turns immersive mode (no system
        // bars) on for Fullscreen and off for every other mode, so the
        // maximize the game scene asks for, and the restore the login scene
        // asks for, would each bring the status bar back over the top of the
        // client. On a mobile OS both keep the window fullscreen. ADR-0017.
        private static bool KeepFullscreen => OS.HasFeature("mobile");

        public void MaximizeWindow()
        {
            DisplayServer.WindowSetMode(KeepFullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Maximized);
        }

        public bool IsWindowMaximized()
        {
            return DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized;
        }

        public void RestoreWindow()
        {
            DisplayServer.WindowSetMode(KeepFullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        }

        public void SetWindowPositionBySettings()
        {
            if (PinnedWindowPosition.HasValue)
            {
                DisplayServer.WindowSetPosition(PinnedWindowPosition.Value);

                return;
            }

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

        /// <summary>
        /// If the window has come up somewhere no monitor covers, move it back
        /// onto the primary one.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream has no such check, and
        /// <see cref="SetWindowPositionBySettings"/> -- which it does have --
        /// only guards the saved position, only its top-left corner, and only
        /// when one was saved at all. That leaves the case this exists for: a
        /// window placed off every screen by something else, by a monitor that
        /// is no longer plugged in, or by a resize that left it hanging past
        /// the edge. There is nothing to click and no title bar to drag, and
        /// the taskbar icon does not help, because the window is not hidden --
        /// it is somewhere real that no monitor shows. It happened here, and
        /// it took a Win32 SetWindowPos from outside the client to undo.
        ///
        /// A quarter of the window has to be on a screen, not merely a pixel:
        /// a window whose only visible sliver is its right edge cannot be
        /// moved by hand either.
        /// </remarks>
        public void PullWindowOntoAScreen()
        {
            // A maximised or fullscreen window is placed by the window
            // manager and cannot be off screen.
            if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
            {
                return;
            }

            Vector2I at = DisplayServer.WindowGetPosition();
            Vector2I size = DisplayServer.WindowGetSize();

            if (size.X <= 0 || size.Y <= 0)
            {
                return;
            }

            var window = new Rect2I(at, size);

            long area = (long) size.X * size.Y;
            long seen = 0;

            for (int screen = 0; screen < DisplayServer.GetScreenCount(); screen++)
            {
                Rect2I shown = window.Intersection(DisplayServer.ScreenGetUsableRect(screen));

                seen += (long) Math.Max(0, shown.Size.X) * Math.Max(0, shown.Size.Y);
            }

            if (seen * 4 >= area)
            {
                return;
            }

            Rect2I home = DisplayServer.ScreenGetUsableRect(DisplayServer.GetPrimaryScreen());

            Log.Warn(
                $"window at {at} sized {size} is off every screen ({seen} of {area} pixels "
                    + $"visible); moving it to {home.Position}"
            );

            SetWindowPosition(home.Position.X, home.Position.Y);
        }

        /// <summary>
        /// Every input event Godot delivers, handed to the layer that
        /// replaces upstream's SDL event filter.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): _Input rather than _UnhandledInput, because
        /// the client draws its own UI into the canvas and has no Godot
        /// Controls to consume anything first. Everything the client sees is
        /// marked handled so a Godot node cannot act on it a second time.
        /// </remarks>
        public override void _Input(InputEvent @event)
        {
            // PORT DEVIATION (GUO): which input is in use, pad or keyboard and
            // mouse or touch, switched by the event itself (ADR-0025).
            GUO.Input.InputMode.Note(@event);

            // PORT DEVIATION (GUO): every input counts as activity for the
            // screen saver, and the one that wakes it goes no further.
            if (GUO.Game.Managers.ScreenSaver.NoteInput())
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): gamepads (upstream has none), in front of
            // the touch layer and GodotInput; see Input/Gamepad.
            if (GUO.Input.Gamepad.GamepadInput.Handle(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): the Store window (ADR-0019) is made of
            // Godot Controls, which only see an event nobody marked handled.
            // While it is open the event goes to them and not to the client:
            // a mouse click, or on a phone Godot's own touch-to-mouse events.
            if (StoreWindowOpen())
            {
                return;
            }

            // PORT DEVIATION (GUO): the screen effects menu (ADR-0023) is a
            // Godot card too; events over it go to its controls, not the client.
            // So does the change-folder screen (FirstRunScreen), which covers
            // the window. A finger reaches both as the mouse (FingerAsMouse).
            if (GUO.Renderer.PostFx.PostFxMenu.OwnsInput(@event) || GUO.Host.FirstRunScreen.OwnsInput(@event))
            {
                return;
            }

            // PORT DEVIATION (GUO): on one screen, the second screen's panel
            // (the dock beside the login gump, the drawer in the world) takes
            // the pointer over it and its tab; see DualScreen.Panel.
            if (GUO.Platform.Android.DualScreen.HandleMainInput(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): the pre-game card (a Godot card) takes the
            // keys while one of its fields is typed in, the pointer while it is
            // open over the login screen, and a tap on its Servers button.
            if (GUO.Input.Touch.Pregame.PregameCard.HandleMainInput(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): the pad-first pregame (docs/ui/pregame_3d.md, the
            // login painting with its own focus and keyboard) takes the keys and the
            // pointer's buttons while it is up; pointer motion goes on.
            if (GUO.Pregame3D.PregameScreen.HandleMainInput(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): a Modern gump (ADR-0024) and the command
            // bar's slot editor (Godot cards, modal) take the pointer and the
            // keys while open.
            if (GUO.Input.Touch.Modern.ModernScripts.HandleShortcut(@event) || GUO.Input.Touch.Modern.ModernGump.HandleInput(@event) || GUO.Input.Touch.BarEditor.HandleInput(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): the window menu (a Godot card) takes the
            // pointer while it is open; a press outside it closes it.
            if (GUO.Input.Touch.WindowMenu.HandleInput(@event))
            {
                GetViewport().SetInputAsHandled();

                return;
            }

            // PORT DEVIATION (GUO): on a touch screen, or under --touch, the
            // touch layer stands in front and hands GodotInput the mouse
            // events a finger amounts to. Off, it is one false test.
            if (!(GUO.Input.Touch.TouchInput.Enabled && GUO.Input.Touch.TouchInput.Handle(@event)))
            {
                GodotInput.Handle(@event);
            }

            GetViewport().SetInputAsHandled();
        }

        /// <summary>Whether a Store window is up: it is added as a child of this node.</summary>
        private bool StoreWindowOpen()
        {
            foreach (Node child in GetChildren())
            {
                if (child is GUO.Store.StoreWindow && !child.IsQueuedForDeletion())
                {
                    return true;
                }
            }

            return false;
        }

        public override void _Process(double delta)
        {
            double elapsedMilliseconds = delta * 1000.0;

            _totalGameTime += delta;

            if (GUO.Input.Touch.TouchInput.Enabled)
            {
                // A finger that does not move raises no event; the hold timer
                // has to be looked at once a frame.
                GUO.Input.Touch.TouchInput.Update();
            }

            // PORT DEVIATION (GUO): a tilted right stick moves the pointer
            // every frame, as a held finger is looked at every frame.
            GUO.Input.Gamepad.GamepadInput.Update(delta);

            Update(elapsedMilliseconds);

            if (!_suppressedDraw)
            {
                DrawFrame();
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

            Mouse.Refresh();

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

        /// <remarks>
        /// PORT DEVIATION (GUO): upstream calls this Draw. A CanvasItem
        /// already has a Draw -- the C# event for Godot's draw signal -- and
        /// a method of that name would hide it.
        /// </remarks>
        private void DrawFrame()
        {
            Rectangle windowBounds = Window.ClientBounds;

            if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
            {
                // PORT DEVIATION (GUO): upstream has no such case -- FNA does
                // not run without a device. Godot does: --headless reports a
                // zero-size window, no render target can be made for it, and
                // the first Clear then has nothing to clear and throws, once
                // per frame. The client still boots, loads and updates this
                // way, which is what the smoke test uses it for. It just does
                // not draw.
                return;
            }

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
            _uoSpriteBatch.Clear(Godot.Colors.Transparent);

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

            // PORT DEVIATION (GUO): the same gumps again for the second
            // screen of a dual-screen device; a no-op without one. ADR-0009.
            GUO.Platform.Android.DualScreen.Draw(_uoSpriteBatch, _renderTargets.UiRenderTarget);

            // PORT DEVIATION (GUO): the idle screen saver, over everything
            // but the cursor. A no-op unless the profile turns it on.
            _uoSpriteBatch.Begin();
            GUO.Game.Managers.ScreenSaver.Draw(_uoSpriteBatch, new Rectangle(0, 0, ClientBounds.Width, ClientBounds.Height));
            _uoSpriteBatch.End();

            _uoSpriteBatch.Begin();
            // PORT DEVIATION (GUO): no cursor while a pad is in use and its
            // pointer idle (ADR-0025, Input.InputMode.PointerHidden), or
            // while the UI is hidden for a clean shot (Renderer.CleanShots).
            if (!GUO.Input.InputMode.PointerHidden && !GUO.Renderer.CleanShots.Hidden)
            {
                UO.GameCursor?.Draw(_uoSpriteBatch);
            }
            // PORT DEVIATION (GUO): with the pointer on a dual-screen device's
            // second screen, a badge of the held item on this one. ADR-0009.
            GUO.Platform.Android.DualScreen.DrawMainBadge(_uoSpriteBatch);
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
            // Godot's notification constants are long; the override takes int.
            switch ((long)what)
            {
                case NotificationWMSizeChanged:
                    Vector2I size = DisplayServer.WindowGetSize();

                    WindowOnClientSizeChanged(size.X, size.Y);

                    break;

                case NotificationApplicationFocusIn:
                    Activated?.Invoke(this, EventArgs.Empty);
                    Plugin.OnFocusGained();

                    break;

                case NotificationApplicationFocusOut:
                    if (GUO.Input.Touch.TouchInput.Enabled) GUO.Input.Touch.TouchInput.CancelGesture();
                    Deactivated?.Invoke(this, EventArgs.Empty);
                    Plugin.OnFocusLost();

                    break;

                // PORT DEVIATION (GUO): a phone does not close the client, it
                // pauses it and later kills it without a word, so nothing
                // upstream saves on exit would ever be saved there. On the
                // pause the profile and the settings are written as GameScene
                // .Unload and UnloadContent write them; the desktop never
                // receives this notification.
                case NotificationApplicationPaused:
                    if (GUO.Input.Touch.TouchInput.Enabled) GUO.Input.Touch.TouchInput.CancelGesture();
                    if (GUO.Input.Touch.TouchInput.Enabled)
                    {
                        SaveOnPause();
                    }

                    break;

                // PORT DEVIATION (GUO): Android's Back button. Upstream has no
                // such key; Godot would quit on it, which project.godot turns off
                // (quit_on_go_back) so BackButton can close a gump, lower the
                // keyboard or ask the upstream quit question instead.
                case NotificationWMGoBackRequest:
                    GUO.Input.Touch.BackButton.Handle();

                    break;

                // Upstream's SDL_EVENT_WINDOW_MOUSE_ENTER / _LEAVE. Godot
                // reports these as window notifications rather than events,
                // so they are here and not in the input layer.
                case NotificationWMMouseEnter:
                    Mouse.MouseInWindow = true;

                    break;

                case NotificationWMMouseExit:
                    Mouse.MouseInWindow = false;

                    break;
            }
        }

        /// <summary>
        /// PORT DEVIATION (GUO): what the desktop saves when the client is
        /// closed, saved when a touch device puts the client in the
        /// background instead; see the notification above.
        /// </summary>
        private void SaveOnPause()
        {
            try
            {
                if (Scene is GameScene game && UO.World != null && UO.World.InGame && ProfileManager.CurrentProfile != null)
                {
                    if (ProfileManager.CurrentProfile.SaveScaleAfterClose)
                    {
                        ProfileManager.CurrentProfile.DefaultScale = game.Camera.Zoom;
                    }

                    ProfileManager.CurrentProfile.Save(UO.World, ProfileManager.ProfilePath);
                }

                Settings.GlobalSettings.Save();
                Log.Trace("Saved on pause");
            }
            catch (Exception ex)
            {
                Log.Error($"Save on pause failed: {ex.Message}");
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

        /// <summary>
        /// PORT DEVIATION (GUO): internal rather than private, because the
        /// PrintScreen key reaches it from the input layer now instead of
        /// from an event filter that lived in this class.
        /// </summary>
        internal void TakeScreenshot()
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
