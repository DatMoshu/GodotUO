// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Configuration;
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

        /// <summary>The feature is on: a display, the profile allows it, the player is in the world.</summary>
        public static bool Active { get; private set; }

        /// <summary>
        /// Set by the probe to measure the main screen without the second one:
        /// nothing is drawn or pushed, everything else stays as it is.
        /// </summary>
        public static bool Suspended { get; set; }

        /// <summary>Logical width of the main window, where the second screen begins.</summary>
        public static int MainWidth => Client.Game?.ClientBounds.Width ?? 0;

        /// <summary>
        /// Logical width the client window is extended by while the feature
        /// is active; zero otherwise. What the gump clamps add.
        /// </summary>
        public static int ExtraWidth => Active ? _instance._logicalWidth : 0;

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
                if (!Active)
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
        /// Look for a second display and, if there is one, get ready to use
        /// it once the player is in the world. Safe to call anywhere: with
        /// no display and no simulation it adds nothing to the tree.
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

            UIManager.RedrawLists(batcher);

            if (clipped)
            {
                batcher.ClipEnd();
            }

            batcher.End();
            batcher.SetRenderTarget(restore);
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

            // The client resizes its window once the profile is read (the
            // saved bounds, a maximise); the world follows the window.
            Rectangle main = Client.Game.Window.ClientBounds;

            if (main.Width != _mainSize.X || main.Height != _mainSize.Y)
            {
                _mainSize = new Vector2I(main.Width, main.Height);
                FillMainWithWorld();
            }

            Shelve();
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

        private static bool WantedNow()
        {
            if (ForcedOff || Client.Game == null || ProfileManager.CurrentProfile == null)
            {
                return false;
            }

            if (!(Client.Game.UO?.World?.InGame ?? false))
            {
                return false;
            }

            return ProfileManager.CurrentProfile.DualScreenEnabled;
        }

        private void Activate()
        {
            float dpi = Client.Game.DpiScale;

            _logicalWidth = Math.Max(1, (int)Math.Round(_physicalWidth / dpi));
            _logicalHeight = Math.Max(1, (int)Math.Round(_physicalHeight / dpi));

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
                + $"is {_logicalWidth}x{_logicalHeight} at dpi scale {dpi:F2}, main window {MainWidth} wide"
            );
        }

        private void Deactivate()
        {
            Active = false;

            _display?.Close();
            CloseSimulator();

            _target?.Dispose();
            _target = null;
            _lastFrame = null;

            // Whatever is on the shelf comes back on screen, by the same
            // rule upstream uses for a gump that ended up off the window.
            foreach (Gump g in UIManager.Gumps)
            {
                if (!g.IsDisposed)
                {
                    g.SetInScreen();
                }
            }

            GD.Print("[GUO] dual screen: inactive");
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
        private void Shelve()
        {
            _seen.RemoveWhere(g => g.IsDisposed);

            uint player = Client.Game.UO.World.Player?.Serial ?? 0;
            uint backpack = Client.Game.UO.World.Player?.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0;

            foreach (Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || _seen.Contains(g))
                {
                    continue;
                }

                Slot slot = SlotFor(g, player, backpack);

                if (slot == Slot.None || g.Width <= 0 || g.Height <= 0)
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

        private enum Slot
        {
            None,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
        }

        private static Slot SlotFor(Gump g, uint player, uint backpack)
        {
            switch (g)
            {
                case PaperDollGump p when p.LocalSerial == player:
                    return Slot.TopLeft;

                case ContainerGump c when backpack != 0 && c.LocalSerial == backpack:
                    return Slot.TopRight;

                case StatusGumpBase:
                    return Slot.BottomLeft;

                case JournalGump:
                case ResizableJournal:
                    return Slot.BottomRight;
            }

            return Slot.None;
        }

        private void Place(Gump g, Slot slot)
        {
            int x = 0, y = 0;

            switch (slot)
            {
                case Slot.TopRight:
                    x = _logicalWidth - g.Width;

                    break;

                case Slot.BottomLeft:
                    y = _logicalHeight - g.Height;

                    break;

                case Slot.BottomRight:
                    x = _logicalWidth - g.Width;
                    y = _logicalHeight - g.Height;

                    break;
            }

            g.X = MainWidth + Math.Max(0, x);
            g.Y = Math.Max(0, y);
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
                    case 3: // ACTION_CANCEL
                        Finger(index, at, down: false);

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
