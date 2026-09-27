// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Input.Touch;
using GUO.Renderer;

namespace GUO.Platform.Android
{
    /// <summary>
    /// The second screen of a dual-screen handheld as a shelf for the gumps
    /// a player keeps open all session: paperdoll, backpack, status bar and
    /// journal. The main screen is then the world alone.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream is a one-window desktop client and
    /// has nothing of the kind. ADR-0009 holds the decision; the shape of
    /// it is this:
    ///
    /// The second display is treated as a virtual extension of the client
    /// window to the right. A gump on the shelf has an X of
    /// <c>MainWidth + something</c> and is otherwise an ordinary gump:
    /// UIManager lays it out, hit-tests it and draws it into its render
    /// lists exactly as it would on a wide desktop window. Only two things
    /// are added around that: the same render lists are drawn a second time
    /// into a target of the second screen's size with a <c>-MainWidth</c>
    /// translation, and that target's pixels are pushed to the display; and
    /// touches on the second display arrive as fingers at
    /// <c>mainWidth + x</c>, so every gesture the touch layer knows works
    /// unchanged. Gumps are never special-cased in how they behave, only in
    /// where they are put when they first appear.
    ///
    /// The class is inert unless <see cref="Setup"/> finds a display (on
    /// Android through <see cref="SecondDisplay"/>) or is asked to simulate
    /// one on the desktop with <c>--dual-screen WxH</c>, where a plain Godot
    /// window stands in for the panel so the feature can be looked at and
    /// tested without a device.
    ///
    /// Dual from launch (ADR-0009, amendment): with a display the
    /// presentation is shown as soon as the client is up and stays until
    /// exit. While the shelf is not in use -- before the player is in the
    /// world, or in the world with the shelf turned off -- the second screen
    /// shows the welcome panel (<see cref="DualWelcomeGump"/>): the sigil, a
    /// welcome line, and the second screen's own settings, which can be
    /// changed from there or from Options and apply live. The settings are
    /// <see cref="DualScreenSettings"/>, which reach the profile when there
    /// is one and stand in for it before.
    /// </remarks>
    internal sealed partial class DualScreen : Node
    {
        /// <summary>Every Nth frame the second screen's pixels are read back and pushed.</summary>
        private const int PresentEvery = 4;

        /// <summary>Pointer ids from the second display, kept apart from the main screen's fingers.</summary>
        private const int PointerBase = 32;

        private static DualScreen _instance;

        private SecondDisplay _display;
        private bool _simulate;
        private Window _simulator;
        private TextureRect _simulatorView;

        private int _physicalWidth, _physicalHeight;
        private int _logicalWidth, _logicalHeight;
        private RenderTarget2D _target;
        private int _frame;
        private bool _rescued;
        private bool _wasInGame;
        private int _appliedScale = -1;
        private int _appliedScalePercent = -1;
        private DualScreenSettings.Values _applied;
        private DualWelcomeGump _welcome;
        private Vector2I _mainSize;
        private readonly HashSet<Gump> _seen = new();
        private readonly HashSet<int> _fingersDown = new();

        /// <summary>What the second screen showed last, for the probe and the desktop screenshot.</summary>
        private Image _lastFrame;

        // ==========================
        // === Static face ==========
        // ==========================

        /// <summary>A display was found (or simulated); the option is worth offering.</summary>
        public static bool HasSecondaryDisplay => _instance != null && (_instance._display != null || _instance._simulate);

        /// <summary>Physical size of the second display, as rotated.</summary>
        public static int SecondWidth => _instance?._physicalWidth ?? 0;

        public static int SecondHeight => _instance?._physicalHeight ?? 0;

        /// <summary>The second screen is up: a display (or the simulator), the client running.</summary>
        public static bool Active { get; private set; }

        /// <summary>The shelf is in use: active, the player in the world, and the setting on.</summary>
        public static bool ShelfOn { get; private set; }

        /// <summary>
        /// Set by the probe to measure the main screen without the second one:
        /// nothing is drawn or pushed, everything else stays as it is.
        /// </summary>
        public static bool Suspended { get; set; }

        /// <summary>Logical width of the main window, where the second screen begins.</summary>
        public static int MainWidth => Client.Game?.ClientBounds.Width ?? 0;

        /// <summary>
        /// Logical width the client window is extended by while the shelf is
        /// in use; zero otherwise. What the gump clamps add.
        /// </summary>
        public static int ExtraWidth => ShelfOn ? _instance._logicalWidth : 0;

        /// <summary>
        /// Hand a gump back to automatic shelving (GumpFlick's "shelf's own
        /// slot"): no longer placed by hand, not yet seen, and off the shelf,
        /// so the next Shelve pass puts it in its slot if the shelf wants it.
        /// </summary>
        /// <summary>Whether the shelf takes this gump by itself: its home screen is the second one.</summary>
        public static bool WantsShelf(Gump g)
        {
            if (!ShelfOn || g == null || Client.Game?.UO?.World?.Player == null)
            {
                return false;
            }

            uint player = Client.Game.UO.World.Player.Serial;
            uint backpack = Client.Game.UO.World.Player.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0;
            Slot slot = SlotFor(g, player, backpack);

            return slot != Slot.None && Wants(DualScreenSettings.Current, slot);
        }

        public static void Reshelve(Gump g)
        {
            if (_instance == null || g == null || g.IsDisposed)
            {
                return;
            }

            g.PresentationPlaced = false;
            _instance._seen.Remove(g);

            if (g.X >= MainWidth)
            {
                g.X = Math.Max(0, MainWidth - g.Width);
            }
        }

        public static int LogicalWidth => _instance?._logicalWidth ?? 0;

        public static int LogicalHeight => _instance?._logicalHeight ?? 0;

        /// <summary>Frames pushed to the display since activation.</summary>
        public static int FramesPresented { get; private set; }

        /// <summary>Touches taken from the display since activation.</summary>
        public static int TouchesTaken { get; private set; }

        /// <summary>Milliseconds the last readback and push took, for the cost measurement.</summary>
        public static double LastPresentMs { get; private set; }

        /// <summary>Whether the run was told to leave the second screen alone.</summary>
        public static bool ForcedOff { get; private set; }

        /// <summary>How many gumps sit on the shelf right now.</summary>
        public static int ShelfCount
        {
            get
            {
                if (!ShelfOn)
                {
                    return 0;
                }

                int n = 0;

                foreach (Gump g in UIManager.Gumps)
                {
                    if (!g.IsDisposed && g.X >= MainWidth)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>
        /// Look for a second display and, if there is one, bring it up as
        /// soon as the client is running. Safe to call anywhere: with no
        /// display and no simulation it adds nothing to the tree.
        /// </summary>
        /// <param name="simulate">"WxH" to stand a desktop window in for the display, or null.</param>
        /// <param name="off">Leave the second screen alone this run.</param>
        public static void Setup(Node host, string simulate, bool off)
        {
            ForcedOff = off;

            if (_instance != null)
            {
                return;
            }

            var instance = new DualScreen { Name = "DualScreen" };

            if (!string.IsNullOrWhiteSpace(simulate))
            {
                if (!TryParseSize(simulate, out instance._physicalWidth, out instance._physicalHeight))
                {
                    GD.PrintErr($"[GUO] dual screen: --dual-screen wants WxH, got \"{simulate}\"");
                    instance.Free();

                    return;
                }

                instance._simulate = true;

                GD.Print($"[GUO] dual screen: simulating a {instance._physicalWidth}x{instance._physicalHeight} display in a window");
            }
            else
            {
                instance._display = SecondDisplay.Find();

                if (instance._display == null)
                {
                    GD.Print("[GUO] dual screen: no second display; nothing changes");
                    instance.Free();

                    return;
                }

                instance._physicalWidth = instance._display.Width;
                instance._physicalHeight = instance._display.Height;
            }

            _instance = instance;
            host.AddChild(instance);
        }

        /// <summary>
        /// Draw the UI render lists a second time, into the second screen's
        /// target, shifted so the extension is what lands on it. Called by
        /// GameController.DrawFrame right after UIManager.Draw, while the
        /// lists still hold this frame's gumps.
        /// </summary>
        /// <param name="restore">The target to point the batcher back at.</param>
        public static void Draw(UltimaBatcher2D batcher, RenderTarget2D restore)
        {
            if (!Active || Suspended || _instance?._target == null)
            {
                return;
            }

            RenderTarget2D target = _instance._target;

            // SetRenderTarget clears whatever it binds to opaque black, as
            // FNA does a DiscardContents target. Binding the UI target back
            // at the end would do that to it after GameController has
            // cleared it transparent, and the UI layer would then cover the
            // world in black (the black world on the Thor, 2026-09-27). Its
            // clear colour is put back as it was.
            Godot.Color restoreClear = restore?.ClearColor ?? Colors.Transparent;

            // The clear rect has to be put back every frame: a canvas item is
            // emptied by its own redraw, which the tree schedules at least
            // once after the item is added, and the batcher only touches the
            // colour when it changes.
            target.ClearColor = Colors.Transparent;
            target.ClearColor = Colors.Black;

            batcher.SetRenderTarget(target);
            batcher.Begin(new Transform2D(0f, new Vector2(-MainWidth, 0f)));

            // The world viewport gump hangs its border five pixels past the
            // window edge, as upstream does with the game window full size;
            // on a desktop that is off screen, here it would be a strip down
            // the left of the second screen. Clip it off.
            int strip = 0;
            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null)
            {
                strip = Math.Max(0, viewport.X + viewport.Width - MainWidth);
            }

            bool clipped = batcher.ClipBegin(MainWidth + strip, 0, _instance._logicalWidth - strip, _instance._logicalHeight);

            // Only what sits on the second screen: a gump whose middle is
            // past the main window's edge. A gump left on the main screen
            // and wider than it (the top bar, 1114 on a 1011 window) would
            // otherwise show its overflow here.
            int mainWidth = MainWidth;
            UIManager.DrawGumpsWhere(batcher, g => g.X + (g.Width >> 1) >= mainWidth);

            // The held item. GameCursor draws it at the pointer into the main
            // window's target, so a pick-up from a shelved gump put it past
            // the main window's edge, where nothing shows it (bug 2 of the
            // Thor pass). Draw it here instead, and a badge on the screen
            // the pointer is not on, so the player always sees what they carry.
            if (PointerOnShelf)
            {
                DrawHeldItem(batcher, Mouse.Position.X, Mouse.Position.Y, 1f, true);
            }
            else
            {
                DrawHeldBadge(batcher, mainWidth + BadgeMargin, _instance._logicalHeight - BadgeMargin);
            }

            if (clipped)
            {
                batcher.ClipEnd();
            }

            // The idle screen saver covers the second screen too.
            GUO.Game.Managers.ScreenSaver.Draw(batcher, new Rectangle(MainWidth, 0, _instance._logicalWidth, _instance._logicalHeight));

            batcher.End();
            batcher.SetRenderTarget(restore);

            if (restore != null)
            {
                restore.ClearColor = restoreClear;
            }
        }

        /// <summary>Pixels between the held-item badge and the screen's edge.</summary>
        private const int BadgeMargin = 8;

        /// <summary>The largest side of the held-item badge, in client pixels.</summary>
        private const int BadgeSize = 44;

        /// <summary>Whether the pointer (the last touch) is on the second screen.</summary>
        private static bool PointerOnShelf => Active && !Suspended && Mouse.Position.X >= MainWidth;

        /// <summary>
        /// The main window's half of the held-item marker: while the pointer
        /// is on the second screen, a badge of the held item in the main
        /// window's bottom-right corner, above the touch bar. Called by
        /// GameController.DrawFrame inside the cursor's batch.
        /// </summary>
        public static void DrawMainBadge(UltimaBatcher2D batcher)
        {
            if (!PointerOnShelf || Client.Game == null)
            {
                return;
            }

            Rectangle bounds = Client.Game.ClientBounds;
            float bar = TouchInput.Bar?.ReservedFraction ?? 0f;
            int bottom = bounds.Height - (int)Math.Ceiling(bar * bounds.Height);

            DrawHeldBadge(batcher, bounds.Width - BadgeMargin - BadgeSize, bottom - BadgeMargin);
        }

        /// <summary>
        /// A held-item badge: the item, at most <see cref="BadgeSize"/> on its
        /// longer side, on a dark square, with its bottom-left corner at
        /// (<paramref name="left"/>, <paramref name="bottom"/>).
        /// </summary>
        private static void DrawHeldBadge(UltimaBatcher2D batcher, int left, int bottom)
        {
            GameCursor cursor = Client.Game?.UO?.GameCursor;

            if (cursor == null || !cursor.ItemHold.Enabled || cursor.ItemHold.Dropped)
            {
                return;
            }

            var box = new Rectangle(left, bottom - BadgeSize, BadgeSize, BadgeSize);
            batcher.Draw(
                SolidColorTextureCache.GetTexture(GUO.Compat.Color.Black),
                box,
                ShaderHueTranslator.GetHueVector(0, false, 0.55f),
                0f
            );

            DrawHeldItem(batcher, box.X + (BadgeSize >> 1), box.Y + (BadgeSize >> 1), 0.9f, false);
        }

        /// <summary>
        /// Draw the held item as GameCursor does, from its own helpers. At the
        /// pointer (<paramref name="atPointer"/>) it keeps the grab offset and
        /// the stack's second copy; as a badge it is centred on (x, y) and
        /// shrunk to fit.
        /// </summary>
        private static void DrawHeldItem(UltimaBatcher2D batcher, int x, int y, float alpha, bool atPointer)
        {
            GameCursor cursor = Client.Game?.UO?.GameCursor;

            if (cursor == null || !cursor.ItemHold.Enabled || cursor.ItemHold.Dropped)
            {
                return;
            }

            ItemHold hold = cursor.ItemHold;
            ushort graphic = cursor.GetDraggingItemGraphic();

            if (graphic == 0xFFFF)
            {
                return;
            }

            ref readonly var artInfo = ref (hold.IsGumpTexture ? ref Client.Game.UO.Gumps.GetGump(graphic) : ref Client.Game.UO.Arts.GetArt(graphic));

            if (artInfo.Texture == null || artInfo.UV.Width <= 0 || artInfo.UV.Height <= 0)
            {
                return;
            }

            float scale = 1f;

            if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.ScaleItemsInsideContainers)
            {
                scale = UIManager.ContainerScale;
            }

            Rectangle rect;

            if (atPointer)
            {
                if (hold.IsFixedPosition)
                {
                    x = hold.FixedX;
                    y = hold.FixedY;
                }

                Point offset = cursor.GetDraggingItemOffset();
                rect = new Rectangle(x - offset.X, y - offset.Y, (int)(artInfo.UV.Width * scale), (int)(artInfo.UV.Height * scale));
            }
            else
            {
                // The art's visible pixels, not its frame, which for an
                // item can be mostly transparent (as CounterBarGump does).
                Rectangle source = artInfo.UV;

                if (!hold.IsGumpTexture)
                {
                    Rectangle real = Client.Game.UO.Arts.GetRealArtBounds(graphic);

                    if (real.Width > 0 && real.Height > 0)
                    {
                        source = new Rectangle(artInfo.UV.X + real.X, artInfo.UV.Y + real.Y, real.Width, real.Height);
                    }
                }

                // Whole-number steps only, so the art keeps square pixels:
                // large art shrinks to fit, small art (a book, a reagent)
                // grows until it would not.
                int longest = Math.Max(source.Width, source.Height);
                int room = BadgeSize - 4;
                int divisor = Math.Max(1, (longest + room - 1) / room);
                int factor = divisor == 1 ? Math.Max(1, room / longest) : 1;
                int w = source.Width * factor / divisor;
                int h = source.Height * factor / divisor;
                rect = new Rectangle(x - (w >> 1), y - (h >> 1), w, h);

                Vector3 badgeHue = ShaderHueTranslator.GetHueVector(hold.Hue, hold.IsPartialHue, hold.HasAlpha ? .5f * alpha : alpha);
                batcher.Draw(artInfo.Texture, rect, source, badgeHue, 0f);

                return;
            }

            Vector3 hue = ShaderHueTranslator.GetHueVector(hold.Hue, hold.IsPartialHue, hold.HasAlpha ? .5f * alpha : alpha);
            batcher.Draw(artInfo.Texture, rect, artInfo.UV, hue, 0f);

            if (atPointer && hold.Amount > 1 && hold.DisplayedGraphic == hold.Graphic && hold.IsStackable)
            {
                rect.X += 5;
                rect.Y += 5;
                batcher.Draw(artInfo.Texture, rect, artInfo.UV, hue, 0f);
            }
        }

        /// <summary>Save what the second screen last showed. False when there is nothing yet.</summary>
        public static bool SaveFrame(string path)
        {
            Image frame = _instance?._lastFrame;

            if (frame == null)
            {
                return false;
            }

            return frame.SavePng(path) == Error.Ok;
        }

        /// <summary>The bridge's last complaint, for the probe.</summary>
        public static string LastError => _instance?._display?.LastError ?? "";

        // ==========================
        // === Per frame ============
        // ==========================

        public override void _Process(double delta)
        {
            bool wanted = WantedNow();

            if (wanted && !Active)
            {
                Activate();
            }
            else if (!wanted && Active)
            {
                Deactivate();
            }

            if (!Active)
            {
                RescueOnce();

                return;
            }

            DualScreenSettings.Values settings = DualScreenSettings.Current;

            if (settings.Scale != _appliedScale || settings.ScalePercent != _appliedScalePercent)
            {
                // A new pixel scale is a new target and a new bitmap: the
                // display is reopened at the size, nothing else changes.
                Deactivate();
                Activate();
            }

            bool inGame = Client.Game.UO?.World?.InGame ?? false;
            bool shelfOn = inGame && settings.Enabled;

            if (shelfOn != ShelfOn)
            {
                ShelfOn = shelfOn;

                if (shelfOn)
                {
                    _seen.Clear();
                    GD.Print("[GUO] dual screen: shelf on");
                }
                else
                {
                    BringBack();
                    GD.Print("[GUO] dual screen: shelf off");
                }
            }
            else if (inGame && !_wasInGame && !shelfOn)
            {
                // Into the world with the shelf off: a gump saved beyond the
                // window by a dual-screen session would be out of reach.
                BringBack();
            }

            _wasInGame = inGame;

            if (inGame)
            {
                // The client resizes its window once the profile is read (the
                // saved bounds, a maximise); the world follows the window.
                Rectangle main = Client.Game.Window.ClientBounds;

                if (main.Width != _mainSize.X || main.Height != _mainSize.Y)
                {
                    _mainSize = new Vector2I(main.Width, main.Height);
                    FillMainWithWorld();
                }
            }

            if (shelfOn)
            {
                DisposeWelcome();
                ApplyLive(settings);
                Shelve(settings);
                ClampShelf();
            }
            else
            {
                EnsureWelcome();
            }

            _applied = settings;
            TakeTouches();

            if (Suspended)
            {
                return;
            }

            _frame++;

            if (_frame % PresentEvery == 0)
            {
                Present();
            }
        }

        /// <summary>The second screen is wanted whenever the client is up and the run did not say no.</summary>
        private static bool WantedNow()
        {
            return !ForcedOff && Client.Game != null && Client.Game.UO?.World != null;
        }

        private void Activate()
        {
            float dpi = Client.Game.DpiScale;
            int scale = DualScreenSettings.Current.Scale;
            int percent = DualScreenSettings.Current.ScalePercent;

            // The shelf's own pixel scale, or the main screen's. A fractional
            // scale (1.25, 1.5) is an option for the owner to compare: its
            // pixels are nearest-sampled, never filtered, so some art pixels
            // come out one screen pixel wider than others.
            float divisor = percent > 0 ? percent / 100f : scale > 0 ? scale : dpi;

            _appliedScale = scale;
            _appliedScalePercent = percent;
            _logicalWidth = Math.Max(1, (int)Math.Round(_physicalWidth / divisor));
            _logicalHeight = Math.Max(1, (int)Math.Round(_physicalHeight / divisor));

            _target = new RenderTarget2D(this, _logicalWidth, _logicalHeight)
            {
                ClearColor = Colors.Black,
            };

            if (_display != null)
            {
                _display.Open(_logicalWidth, _logicalHeight);
            }
            else
            {
                OpenSimulator();
            }

            _seen.Clear();
            _fingersDown.Clear();
            _frame = 0;
            FramesPresented = 0;
            TouchesTaken = 0;
            Active = true;
            _mainSize = Vector2I.Zero;

            GD.Print(
                $"[GUO] dual screen: active; second screen {_physicalWidth}x{_physicalHeight} "
                + $"is {_logicalWidth}x{_logicalHeight} at scale {divisor:F2} ({(percent > 0 ? "fine shelf setting" : scale > 0 ? "shelf setting" : "main screen")}), main window {MainWidth} wide"
            );
        }

        private void Deactivate()
        {
            Active = false;
            ShelfOn = false;

            _display?.Close();
            CloseSimulator();

            _target?.Dispose();
            _target = null;
            _lastFrame = null;

            DisposeWelcome();
            BringBack();

            GD.Print("[GUO] dual screen: inactive");
        }

        /// <summary>
        /// Whatever is on the shelf comes back on screen, by the same rule
        /// upstream uses for a gump that ended up off the window.
        /// </summary>
        private static void BringBack()
        {
            foreach (Gump g in UIManager.Gumps)
            {
                if (!g.IsDisposed && g is not DualWelcomeGump)
                {
                    g.SetInScreen();
                }
            }
        }

        // ==========================
        // === The welcome panel ====
        // ==========================

        /// <summary>
        /// The welcome panel on the second screen whenever the shelf is not
        /// in use. Made again when a scene change disposed it, kept at the
        /// second screen's origin when the main window changes width.
        /// </summary>
        private void EnsureWelcome()
        {
            if (_welcome == null || _welcome.IsDisposed)
            {
                _welcome = new DualWelcomeGump(Client.Game.UO.World, _logicalWidth, _logicalHeight)
                {
                    X = MainWidth,
                    Y = 0,
                };

                UIManager.Add(_welcome);
                GD.Print($"[GUO] dual screen: welcome panel {_logicalWidth}x{_logicalHeight} at x={MainWidth}");
            }
            else if (_welcome.X != MainWidth)
            {
                _welcome.X = MainWidth;
            }
        }

        private void DisposeWelcome()
        {
            if (_welcome != null && !_welcome.IsDisposed)
            {
                _welcome.Dispose();
            }

            _welcome = null;
        }

        /// <summary>
        /// A shelf setting changed while the shelf is in use: a kind turned
        /// off comes back to the main screen, a kind turned on is placed
        /// again the next frame.
        /// </summary>
        private void ApplyLive(DualScreenSettings.Values now)
        {
            if (_applied == null)
            {
                return;
            }

            uint player = Client.Game.UO.World.Player?.Serial ?? 0;
            uint backpack = Client.Game.UO.World.Player?.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0;

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed)
                {
                    continue;
                }

                Slot slot = SlotFor(g, player, backpack);
                bool was = Wants(_applied, slot);
                bool wants = Wants(now, slot);

                if (was && !wants && g.X >= MainWidth)
                {
                    g.SetInScreen();
                }
                else if (!was && wants)
                {
                    _seen.Remove(g);
                }
            }
        }

        /// <summary>
        /// Once per session with the feature off: a gump saved beyond the
        /// main window by an earlier dual-screen session would otherwise be
        /// out of reach.
        /// </summary>
        private void RescueOnce()
        {
            if (_rescued || !(Client.Game?.UO?.World?.InGame ?? false))
            {
                return;
            }

            _rescued = true;

            foreach (Gump g in UIManager.Gumps)
            {
                if (!g.IsDisposed)
                {
                    g.SetInScreen();
                }
            }
        }

        /// <summary>
        /// The world takes the whole main screen, the way upstream's "game
        /// window full size" does when the window is resized. The profile
        /// setting itself is left alone.
        /// </summary>
        private static void FillMainWithWorld()
        {
            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport == null)
            {
                return;
            }

            Rectangle window = Client.Game.Window.ClientBounds;

            viewport.ResizeGameWindow(new Point(window.Width, window.Height));
            viewport.X = -5;
            viewport.Y = -5;
        }

        // ==========================
        // === The shelf ============
        // ==========================

        /// <summary>
        /// Put a shelf gump on the second screen the first time it is seen,
        /// if it is on the main screen. After that it is the player's: they
        /// drag it where they like, including back.
        /// </summary>
        private void Shelve(DualScreenSettings.Values settings)
        {
            _seen.RemoveWhere(g => g.IsDisposed);

            uint player = Client.Game.UO.World.Player?.Serial ?? 0;
            uint backpack = Client.Game.UO.World.Player?.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0;

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || g.PresentationPlaced || _seen.Contains(g))
                {
                    continue;
                }

                Slot slot = SlotFor(g, player, backpack);

                if (slot == Slot.None || !Wants(settings, slot) || g.Width <= 0 || g.Height <= 0)
                {
                    // Not a shelf gump, or one that has not been laid out
                    // yet; a size of zero would put it in the wrong corner.
                    continue;
                }

                _seen.Add(g);

                bool onShelf = g.X >= MainWidth
                    && g.X < MainWidth + _logicalWidth
                    && g.Y > -g.Height
                    && g.Y < _logicalHeight;

                if (onShelf)
                {
                    // Already over there, from a saved position.
                    continue;
                }

                Place(g, slot);
            }
        }

        /// <summary>The kinds of gump the shelf takes, each with a fixed slot.</summary>
        private enum Slot
        {
            None,
            Paperdoll,
            Backpack,
            Status,
            Journal,
            Other,
        }

        private static Slot SlotFor(Gump g, uint player, uint backpack)
        {
            switch (g)
            {
                case PaperDollGump p when p.LocalSerial == player:
                    return Slot.Paperdoll;

                case ContainerGump c when backpack != 0 && c.LocalSerial == backpack:
                    return Slot.Backpack;

                // The mobile profile (PlatformDefaults v3, GridContainers) opens
                // the backpack as a grid; it is the same shelf gump.
                case GridContainerGump gc when backpack != 0 && gc.LocalSerial == backpack:
                    return Slot.Backpack;

                case StatusGumpBase:
                    return Slot.Status;

                case JournalGump:
                case ResizableJournal:
                    return Slot.Journal;

                // "Others": the gumps a player opens and keeps for a while,
                // never the world, the top bar, chat, or anything the server
                // laid out for the main screen.
                case ContainerGump:
                case GridContainerGump:
                case SkillGumpAdvanced:
                case StandardSkillsGump:
                case SpellbookGump:
                    return Slot.Other;
            }

            return Slot.None;
        }

        private static bool Wants(DualScreenSettings.Values s, Slot slot)
        {
            switch (slot)
            {
                case Slot.Paperdoll: return s.Paperdoll;
                case Slot.Backpack: return s.Backpack;
                case Slot.Status: return s.Status;
                case Slot.Journal: return s.Journal;
                case Slot.Other: return s.Others;
            }

            return false;
        }

        /// <summary>The gump in a slot right now, if one is on the shelf; for packing around it.</summary>
        private Gump OnShelf(Slot slot)
        {
            uint player = Client.Game.UO.World.Player?.Serial ?? 0;
            uint backpack = Client.Game.UO.World.Player?.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0;

            foreach (Gump g in UIManager.Gumps)
            {
                if (!g.IsDisposed && g.X >= MainWidth && SlotFor(g, player, backpack) == slot)
                {
                    return g;
                }
            }

            return null;
        }

        /// <summary>
        /// The packing rule, for a 620x540 shelf (the Thor at 2x) and any
        /// other size: the paperdoll top left, the status bar along the
        /// bottom (it is nearly the shelf's width), the backpack top right,
        /// the journal under the backpack in the column right of the
        /// paperdoll and above the status bar, other gumps in the middle.
        /// Every slot is then clamped to the shelf, so nothing runs off an
        /// edge. Upstream's paperdoll (262x324), modern status (577x216),
        /// journal (345x298) and backpack (230x204) do not all fit 620x540
        /// without overlap; the journal takes the overlap, over the lower
        /// part of the backpack, and a tap brings either to the front.
        /// </summary>
        private void Place(Gump g, Slot slot)
        {
            int x = 0, y = 0;

            switch (slot)
            {
                case Slot.Backpack:
                    x = _logicalWidth - g.Width;

                    break;

                case Slot.Status:
                    y = _logicalHeight - g.Height;

                    break;

                case Slot.Journal:
                {
                    Gump backpack = OnShelf(Slot.Backpack);
                    Gump status = OnShelf(Slot.Status);
                    int below = backpack?.Height ?? 0;
                    int above = _logicalHeight - (status?.Height ?? 0) - g.Height;

                    x = _logicalWidth - g.Width;
                    y = Math.Min(below, Math.Max(0, above));

                    break;
                }

                case Slot.Other:
                    x = (_logicalWidth - g.Width) / 2;
                    y = (_logicalHeight - g.Height) / 2;

                    break;
            }

            g.X = MainWidth + Math.Clamp(x, 0, Math.Max(0, _logicalWidth - g.Width));
            g.Y = Math.Clamp(y, 0, Math.Max(0, _logicalHeight - g.Height));
        }

        /// <summary>
        /// A gump on the shelf stays on the shelf: whatever put it past an
        /// edge (a saved position from a wider shelf, a placement made
        /// before its size was final), it is clamped back each frame, except
        /// while the player is dragging it.
        /// </summary>
        private void ClampShelf()
        {
            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || g.X < MainWidth || g.Width <= 0 || g.Height <= 0)
                {
                    continue;
                }

                if (UIManager.IsDragging && UIManager.DraggingControl?.RootParent == g)
                {
                    continue;
                }

                int x = Math.Clamp(g.X - MainWidth, 0, Math.Max(0, _logicalWidth - Input.Touch.GumpPresentation.Width(g)));
                int y = Math.Clamp(g.Y, 0, Math.Max(0, _logicalHeight - Input.Touch.GumpPresentation.Height(g)));

                if (g.X != MainWidth + x || g.Y != y)
                {
                    g.X = MainWidth + x;
                    g.Y = y;
                }
            }
        }

        // ==========================
        // === Frames out ===========
        // ==========================

        private void Present()
        {
            if (_target == null)
            {
                return;
            }

            ulong started = Godot.Time.GetTicksUsec();

            Image image = _target.Texture.GetImage();

            if (image == null)
            {
                return;
            }

            if (image.GetFormat() != Image.Format.Rgba8)
            {
                image.Convert(Image.Format.Rgba8);
            }

            _lastFrame = image;

            if (_display != null)
            {
                if (_display.Present(image.GetData()))
                {
                    FramesPresented++;
                }
            }
            else if (_simulatorView != null)
            {
                ((ImageTexture)_simulatorView.Texture).Update(image);
                FramesPresented++;
            }

            LastPresentMs = (Godot.Time.GetTicksUsec() - started) / 1000.0;
        }

        // ==========================
        // === Touches in ===========
        // ==========================

        /// <summary>
        /// A finger on the second display becomes a finger on the virtual
        /// extension: <c>mainWidth + x</c> in window pixels, so the touch
        /// layer and the client's own hit testing see an ordinary touch on
        /// a wide window.
        /// </summary>
        private void TakeTouches()
        {
            if (_display == null)
            {
                return;
            }

            while (_display.TryDequeueTouch(out SecondDisplay.TouchEvent e))
            {
                int index = PointerBase + e.PointerId;
                Vector2 at = ToWindow(e.X, e.Y);

                switch (e.Action)
                {
                    case 0: // ACTION_DOWN
                    case 5: // ACTION_POINTER_DOWN
                        Finger(index, at, down: true);

                        break;

                    case 1: // ACTION_UP
                    case 6: // ACTION_POINTER_UP
                        Finger(index, at, down: false);

                        break;

                    case 3: // ACTION_CANCEL cancels the stream, not a successful tap/drop.
                        Input.Touch.TouchInput.CancelGesture();
                        _fingersDown.Clear();
                        break;

                    case 2: // ACTION_MOVE
                        if (_fingersDown.Contains(index))
                        {
                            Deliver(new InputEventScreenDrag { Index = index, Position = at });
                        }

                        break;
                }
            }
        }

        private void Finger(int index, Vector2 at, bool down)
        {
            if (down)
            {
                _fingersDown.Add(index);
            }
            else
            {
                _fingersDown.Remove(index);
            }

            TouchesTaken++;

            if (TouchesTaken == 1)
            {
                // Once, so a device run can prove the path from the panel to
                // the client without a debugger on it.
                GD.Print($"[GUO] dual screen: first touch, finger {index} {(down ? "down" : "up")} at window {at.X:F0},{at.Y:F0} (client {at.X / Client.Game.DpiScale:F0},{at.Y / Client.Game.DpiScale:F0})");
            }

            Deliver(new InputEventScreenTouch { Index = index, Position = at, Pressed = down });
        }

        private static void Deliver(InputEvent e)
        {
            if (TouchInput.Enabled)
            {
                TouchInput.Handle(e);
            }
            else
            {
                // No touch layer (a desktop run without --touch): the finger
                // is a left button, as Godot's own emulation would make it.
                switch (e)
                {
                    case InputEventScreenTouch t:
                        GodotInput.Handle(new InputEventMouseMotion { Position = t.Position });
                        GodotInput.Handle(new InputEventMouseButton
                        {
                            ButtonIndex = MouseButton.Left,
                            Position = t.Position,
                            Pressed = t.Pressed,
                        });

                        break;

                    case InputEventScreenDrag d:
                        GodotInput.Handle(new InputEventMouseMotion { Position = d.Position });

                        break;
                }
            }
        }

        /// <summary>Second-display pixels to main-window pixels on the virtual extension.</summary>
        private Vector2 ToWindow(float x, float y)
        {
            float dpi = Client.Game.DpiScale;
            float mainPhysical = Client.Game.Window.ClientBounds.Width;

            // The target is scaled to the panel; undo that, then put it in
            // window pixels, which the input layer divides by the dpi scale.
            float lx = x * _logicalWidth / _physicalWidth;
            float ly = y * _logicalHeight / _physicalHeight;

            return new Vector2(mainPhysical + lx * dpi, ly * dpi);
        }

        // ==========================
        // === Desktop simulator ====
        // ==========================

        private void OpenSimulator()
        {
            // A window of its own, not one drawn inside the main viewport:
            // that is what the panel is, and it keeps the main screenshot
            // honest.
            GetWindow().GuiEmbedSubwindows = false;

            _simulator = new Window
            {
                Title = "GUO second screen",
                Size = new Vector2I(_physicalWidth, _physicalHeight),
                Unresizable = true,
                // A second OS window would activate itself as the first one
                // did; a scripted run keeps this one out of the keyboard's
                // way too (see Main._EnterTree).
                Unfocusable = GUO.Host.Main.NoFocus,
            };

            var texture = ImageTexture.CreateFromImage(Image.CreateEmpty(_logicalWidth, _logicalHeight, false, Image.Format.Rgba8));

            _simulatorView = new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                // Rule 7: the integer scale up must not be filtered.
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };

            _simulatorView.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _simulator.AddChild(_simulatorView);
            _simulator.WindowInput += OnSimulatorInput;
            _simulator.CloseRequested += () => ForcedOff = true;

            AddChild(_simulator);

            // Beside the main window, so a screenshot of the desktop shows both.
            Window main = GetWindow();
            _simulator.Position = main.Position + new Vector2I(main.Size.X + 8, 0);
        }

        private void CloseSimulator()
        {
            if (_simulator == null)
            {
                return;
            }

            _simulator.WindowInput -= OnSimulatorInput;
            _simulator.QueueFree();
            _simulator = null;
            _simulatorView = null;
        }

        /// <summary>The desktop mouse in the simulator window as one finger on the display.</summary>
        private void OnSimulatorInput(InputEvent e)
        {
            const int index = PointerBase;

            switch (e)
            {
                case InputEventMouseButton b when b.ButtonIndex == MouseButton.Left:
                    Finger(index, ToWindow(b.Position.X, b.Position.Y), b.Pressed);

                    break;

                case InputEventMouseMotion m when _fingersDown.Contains(index):
                    Deliver(new InputEventScreenDrag { Index = index, Position = ToWindow(m.Position.X, m.Position.Y) });

                    break;

                case InputEventMouseMotion m:
                    // Hovering matters to the client (tooltips, the highlight)
                    // even without a button; a finger cannot do this, a mouse can.
                    if (!TouchInput.Enabled)
                    {
                        GodotInput.Handle(new InputEventMouseMotion { Position = ToWindow(m.Position.X, m.Position.Y) });
                    }

                    break;
            }
        }

        private static bool TryParseSize(string text, out int width, out int height)
        {
            width = height = 0;

            string[] parts = text.ToLowerInvariant().Split('x');

            return parts.Length == 2
                && int.TryParse(parts[0], out width)
                && int.TryParse(parts[1], out height)
                && width > 0
                && height > 0;
        }

        public override void _ExitTree()
        {
            if (Active)
            {
                Active = false;
                ShelfOn = false;
                _display?.Close();
                CloseSimulator();
                _target?.Dispose();
                _target = null;
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
