// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;

namespace GUO.Input.Touch
{
    /// <summary>
    /// Turns Godot's touch events into the mouse and key events the client
    /// already understands, so that the game logic never sees a finger.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no touch input at all -- ClassicUO
    /// is a desktop client and every one of its input paths starts from an
    /// SDL mouse or keyboard event. This layer sits in front of
    /// <see cref="GodotInput"/> and feeds it the same <c>InputEventMouse*</c>
    /// and <c>InputEventKey</c> objects a mouse and a keyboard would have
    /// produced, chosen by a small gesture state machine. Nothing below
    /// <see cref="GodotInput.Handle"/> is touched, and when the layer is off
    /// -- which it is on every desktop run without <c>--touch</c> -- not one
    /// line of it runs. See ADR-0017, section "Touch controls".
    ///
    /// The gestures, and what each becomes:
    ///
    ///   * tap                          left press + release at the point
    ///   * two taps inside 350 ms        the second is sent on the finger-down
    ///                                   so the client's own double-click
    ///                                   timer (Mouse.MOUSE_DELAY_DOUBLE_CLICK)
    ///                                   sees it as a double click: use, open
    ///   * hold on the world             right press, held: the scene walks
    ///                                   toward the finger, and runs past 190
    ///                                   client pixels, exactly as it does for
    ///                                   a held right mouse button
    ///   * swipe on the world            the same right hold, started at once
    ///   * hold on an item in the world  left press: moving the finger then
    ///                                   drags the item, lifting drops it
    ///   * long press over a gump        right press + release: closes what
    ///                                   closes on a right click, opens the
    ///                                   context of what has one
    ///   * drag over a gump              left press at the start point, then
    ///                                   motion: moves the gump, or picks up
    ///                                   the item under the finger
    ///   * two fingers                   the distance between them, every
    ///                                   PinchStepPixels, is one Ctrl+wheel
    ///                                   notch, which is how the client zooms
    ///   * the gump bar                  a tap on one of its buttons calls the
    ///                                   same GameActions the top bar's
    ///                                   buttons call
    ///
    /// Everything is measured in viewport pixels, the space Godot's events
    /// come in and the space GodotInput hands to Mouse.Update.
    /// </remarks>
    internal static class TouchInput
    {
        /// <summary>
        /// Whether the layer is in front of the mouse path at all. Settable
        /// so the touch probe can step it aside for the login, which goes
        /// through the mouse path the input probe already covers.
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Milliseconds a still finger has to stay down before it is a hold
        /// rather than a tap. Below Mouse.MOUSE_DELAY_DOUBLE_CLICK on purpose:
        /// a tap has to be resolved before the next one can pair with it.
        /// </summary>
        public const int HoldMs = 220;

        /// <summary>
        /// Milliseconds before a still finger over a gump becomes a right
        /// click. Longer than a hold: closing a gump by accident is worse
        /// than waiting for it.
        /// </summary>
        public const int LongPressMs = 550;

        /// <summary>Viewport pixels a finger may wander and still be still.</summary>
        public const float MovePixels = 12f;

        /// <summary>Change in finger distance that is one zoom notch.</summary>
        public const float PinchStepPixels = 40f;

        /// <summary>
        /// What the layer did, most recent last, for the probe to read. Kept
        /// short; it is a trace, not a log.
        /// </summary>
        public static readonly List<string> Trace = new();

        private const int TraceLimit = 64;

        private enum Phase
        {
            Idle,

            /// <summary>One finger down; nothing decided yet.</summary>
            Pending,

            /// <summary>A left button is held on the finger's behalf.</summary>
            LeftHeld,

            /// <summary>A right button is held: the character walks.</summary>
            RightHeld,

            /// <summary>Two fingers down; only zoom until both lift.</summary>
            Pinch,

            /// <summary>The gesture is spent; wait for the finger to lift.</summary>
            Done,
        }

        private static Phase _phase;
        private static int _primary = -1;
        private static int _secondary = -1;
        private static Vector2 _downAt;
        private static Vector2 _lastAt;
        private static Vector2 _secondaryAt;
        private static ulong _downTime;
        private static ulong _lastTapTime;
        private static Vector2 _lastTapAt;
        private static float _pinchDistance;
        private static float _pinchAccumulated;
        private static TouchGumpBar _bar;

        /// <summary>
        /// Puts the layer in front of the mouse path.
        /// </summary>
        /// <param name="host">The node the gump bar hangs under.</param>
        /// <param name="emulateTouchFromMouse">
        /// On a desktop run with <c>--touch</c>: the left mouse button acts as
        /// one finger, so the layer can be driven without a touch screen.
        /// This is the runtime form of the project setting
        /// <c>input_devices/pointing/emulate_touch_from_mouse</c>, applied here
        /// rather than in project.godot so it is only ever on under the flag.
        /// </param>
        public static void Enable(Node host, bool emulateTouchFromMouse)
        {
            if (Enabled)
            {
                return;
            }

            Enabled = true;

            // Godot would otherwise turn every touch into a mouse event of its
            // own as well, and the client would see each finger twice.
            Godot.Input.EmulateMouseFromTouch = false;
            Godot.Input.EmulateTouchFromMouse = emulateTouchFromMouse;

            _bar = new TouchGumpBar();
            host.AddChild(_bar);

            GD.Print(
                $"[GUO] touch input: on (mouse emulates a finger: {emulateTouchFromMouse})"
            );
        }

        /// <summary>The overlay bar, for the probe and the scale hook.</summary>
        public static TouchGumpBar Bar => _bar;

        /// <summary>
        /// The scale the command line asked for (<c>--screen-scale N</c>);
        /// zero picks one from the display. Read by GameController before it
        /// loads the first scene, which is the only moment early enough.
        /// </summary>
        public static int RequestedScale { get; set; }

        /// <summary>
        /// Pick an integer screen scale for the display so the client's
        /// fixed-size art -- the 640x480 login screen above all -- is drawn
        /// at a size a finger can hit, through the client's own DPI path,
        /// which samples with PointClamp for any whole-number scale.
        /// </summary>
        /// <returns>The scale applied.</returns>
        public static int ApplyScreenScale(int requested)
        {
            Vector2I size = DisplayServer.WindowGetSize();
            int shorter = System.Math.Min(size.X, size.Y);

            // The login screen is 480 tall and has to fit; anything left over
            // goes to the world.
            int scale = requested > 0 ? requested : System.Math.Max(1, shorter / 480);

            // The client's DpiScale is the OS scale times ScreenScale, and an
            // Android display reports its density there (the Thor: 369 dpi,
            // 2.3x). The whole-number scale chosen here is the TOTAL the art
            // is drawn at, so the OS factor is divided out; otherwise the two
            // compounded and the login screen overflowed a 1080p display.
            float osScale = DisplayServer.ScreenGetScale(DisplayServer.WindowGetCurrentScreen());
            if (osScale <= 0f) osScale = 1f;

            if (Client.Game != null)
            {
                Client.Game.ScreenScale = scale / osScale;
            }

            GD.Print($"[GUO] touch input: window {size.X}x{size.Y}, os scale {osScale:0.00}, screen scale {scale}");

            return scale;
        }

        /// <summary>
        /// Every event Godot delivers, before <see cref="GodotInput"/> sees it.
        /// </summary>
        /// <returns>True when the event was consumed here.</returns>
        public static bool Handle(InputEvent e)
        {
            switch (e)
            {
                case InputEventScreenTouch touch:
                    HandleTouch(touch);

                    return true;

                case InputEventScreenDrag drag:
                    HandleDrag(drag);

                    return true;

                case InputEventMagnifyGesture magnify:
                    // A trackpad, or Android with pan-and-scale gestures on
                    // (project.godot: it has to be, or a symmetric pinch is
                    // never forwarded). Each event carries the change since
                    // the last; the factors are multiplied up and every
                    // MagnifyStep of growth or shrink is one notch.
                    Magnify(magnify.Factor, magnify.Position);

                    return true;

                case InputEventPanGesture:
                    // A two-finger scroll on Android. The client has no
                    // gesture for it; consumed so it cannot become a click.
                    return true;

                case InputEventMouseButton button:
                    // A real mouse, or Godot's own emulation of one, on a run
                    // where fingers are the pointer. The wheel is let through:
                    // it has no finger equivalent and cannot double a tap.
                    return button.ButtonIndex
                        is not (MouseButton.WheelUp or MouseButton.WheelDown
                            or MouseButton.WheelLeft or MouseButton.WheelRight);

                case InputEventMouseMotion:
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Once a frame: the timers. A finger that has not moved raises no
        /// event, so a hold can only be noticed from here.
        /// </summary>
        public static void Update()
        {
            PanForKeyboard();

            if (_phase != Phase.Pending)
            {
                return;
            }

            ulong held = Godot.Time.GetTicksMsec() - _downTime;

            if (held < HoldMs)
            {
                return;
            }

            if (OverWorld(_lastAt))
            {
                if (SelectedObject.Object is Item)
                {
                    // Held on an item: pick it up. The client picks up on a
                    // left drag past its own threshold, so the finger moving
                    // afterwards is what carries the item.
                    Press(MouseButton.Left, _lastAt);
                    _phase = Phase.LeftHeld;
                    Note("hold on item -> left press (drag)");
                }
                else
                {
                    StartWalk();
                }
            }
            else if (OverItemSlot())
            {
                // Held on an item in a container or on the paperdoll. A held
                // press is how the client picks one up, so this is a pickup
                // and never a right click; MobileUO learned that the hard way.
                Press(MouseButton.Left, _lastAt);
                _phase = Phase.LeftHeld;
                Note("hold on item slot -> left press (drag)");
            }
            else if (held >= LongPressMs)
            {
                // Over a gump, or nowhere: the right click.
                Press(MouseButton.Right, _lastAt);
                Release(MouseButton.Right, _lastAt);
                _phase = Phase.Done;
                Note("long press -> right click");
            }
        }

        /// <summary>
        /// Whether the control under the pointer is an item a held left
        /// button would pick up: an item in a container, or a piece of
        /// equipment on the paperdoll.
        /// </summary>
        private static bool OverItemSlot()
        {
            Game.UI.Controls.Control over = UIManager.MouseOverControl;

            return over is Game.UI.Controls.ItemGump
                || (over is Game.UI.Controls.GumpPic && over.Parent is Game.UI.Controls.PaperDollInteractable);
        }

        private static void HandleTouch(InputEventScreenTouch e)
        {
            if (e.Pressed)
            {
                FingerDown(e.Index, e.Position);
            }
            else
            {
                FingerUp(e.Index, e.Position);
            }
        }

        private static void HandleDrag(InputEventScreenDrag e)
        {
            if (TraceToLog && _phase == Phase.Pinch)
            {
                Note($"drag {e.Index} at {e.Position} (primary {_primary}, secondary {_secondary})");
            }

            if (e.Index == _primary)
            {
                FingerMove(e.Position);
            }
            else if (e.Index == _secondary)
            {
                _secondaryAt = e.Position;
                UpdatePinch();
            }
        }

        private static void FingerDown(int index, Vector2 at)
        {
            if (_phase == Phase.Idle)
            {
                _primary = index;
                _downAt = _lastAt = at;
                _downTime = Godot.Time.GetTicksMsec();

                if (_bar != null && _bar.HitTest(at, out string action))
                {
                    _bar.Invoke(action);
                    _phase = Phase.Done;
                    Note($"bar -> {action}");

                    return;
                }

                // Where the finger is, first: everything the client decides
                // about a click it decides from Mouse.Position.
                Motion(at);

                if (_downTime - _lastTapTime <= Mouse.MOUSE_DELAY_DOUBLE_CLICK
                    && at.DistanceTo(_lastTapAt) <= MovePixels * 2)
                {
                    // The second of two quick taps. It is never a hold, and
                    // sending the press now keeps it inside the client's
                    // double-click window, which counts from the first press.
                    Press(MouseButton.Left, at);
                    _phase = Phase.LeftHeld;
                    _lastTapTime = 0;
                    Note("second tap -> left press (double click)");

                    return;
                }

                _phase = Phase.Pending;

                return;
            }

            if (_secondary < 0 && index != _primary)
            {
                // A second finger. Whatever the first was doing stops -- a
                // walk in particular -- and only the distance matters now.
                EndHeld(_lastAt);
                _secondary = index;
                _secondaryAt = at;
                _pinchDistance = _lastAt.DistanceTo(at);
                _pinchAccumulated = 0;
                _phase = Phase.Pinch;
                Note("second finger -> pinch");
            }
        }

        private static void FingerMove(Vector2 at)
        {
            _lastAt = at;

            switch (_phase)
            {
                case Phase.Pending:
                    if (at.DistanceTo(_downAt) > MovePixels)
                    {
                        if (OverWorld(_downAt))
                        {
                            // A swipe: walk that way, from the first pixel.
                            StartWalk();
                        }
                        else
                        {
                            // Dragging a gump, or an item out of one. The
                            // press goes where the finger landed, so the
                            // client's own drag threshold measures from there.
                            Press(MouseButton.Left, _downAt);
                            _phase = Phase.LeftHeld;
                            Note("drag over gump -> left press");
                            Motion(at);
                        }
                    }

                    break;

                case Phase.LeftHeld:
                case Phase.RightHeld:
                    Motion(at);

                    break;

                case Phase.Pinch:
                    UpdatePinch();

                    break;
            }
        }

        private static void FingerUp(int index, Vector2 at)
        {
            if (index == _secondary)
            {
                _secondary = -1;

                // The first finger is still down, but the gesture it began is
                // over; it lifts into nothing.
                _phase = Phase.Done;

                return;
            }

            if (index != _primary)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Pending:
                    // Down and up with nothing decided: a tap.
                    Press(MouseButton.Left, at);
                    Release(MouseButton.Left, at);
                    _lastTapTime = Godot.Time.GetTicksMsec();
                    _lastTapAt = at;
                    Note($"tap -> left click at {at.X:0},{at.Y:0}");
                    SyncKeyboard();

                    break;

                case Phase.LeftHeld:
                    Release(MouseButton.Left, at);
                    Note("finger up -> left release");

                    break;

                case Phase.RightHeld:
                    Release(MouseButton.Right, at);
                    Note("finger up -> right release (stop)");

                    break;

                case Phase.Pinch:
                    _secondary = -1;
                    Note("both fingers up -> pinch over");

                    break;
            }

            _phase = Phase.Idle;
            _primary = -1;
        }

        /// <summary>Whether the platform's on-screen keyboard is up.</summary>
        public static bool KeyboardShown =>
            DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard)
            && DisplayServer.VirtualKeyboardGetHeight() > 0;

        /// <summary>
        /// Raise the platform's on-screen keyboard. Its keys arrive as the
        /// InputEventKeys a real keyboard would send, which GodotInput already
        /// turns into the client's text input; nothing else is needed.
        /// </summary>
        public static void ShowKeyboard(string existingText, bool password)
        {
            if (!DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            {
                GD.Print("[GUO] touch input: no virtual keyboard on this platform");

                return;
            }

            // Never the Default type: with it the IME composes words and
            // autocorrects them, and Godot replays each correction as a run
            // of backspaces and re-typed characters into a field that has
            // its own caret, which mangled the account name on the Thor
            // ("guoprobeoprob"). The password and e-mail input types are
            // the two every Android IME must not suggest or correct in,
            // so what is typed is what the field gets; the e-mail keyboard
            // is an ordinary one with an "@" key.
            DisplayServer.VirtualKeyboardType type = password
                ? DisplayServer.VirtualKeyboardType.Password
                : DisplayServer.VirtualKeyboardType.EmailAddress;
            DisplayServer.VirtualKeyboardShow(existingText ?? string.Empty, type: type);
            Note($"keyboard shown ({type})");
        }

        public static void HideKeyboard()
        {
            if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            {
                DisplayServer.VirtualKeyboardHide();
                Note("keyboard hidden");
            }
        }

        /// <summary>
        /// After a tap: the keyboard follows the text field. A tap that
        /// landed on an editable text box -- the client gave it the keyboard
        /// focus on the mouse-down -- raises the keyboard; a tap anywhere
        /// else lowers it. The chat line has the focus by default in the
        /// world, so focus alone is not the test; the tap has to land on it.
        /// </summary>
        private static void SyncKeyboard()
        {
            if (!DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            {
                return;
            }

            Game.UI.Controls.Control focus = UIManager.KeyboardFocusControl;
            Game.UI.Controls.Control over = UIManager.MouseOverControl;

            if (focus is Game.UI.Controls.StbTextBox box && box.IsEditable && over != null
                && (over == box || box.Children.Contains(over) || over.Children.Contains(box)))
            {
                // The login gump hides the real password in a fake field;
                // its type says so, and the platform's password keyboard
                // neither suggests nor remembers.
                bool password = box.GetType().Name.Contains("Password");
                ShowKeyboard(password ? string.Empty : box.Text, password);
                Note("tap on text box -> keyboard");
            }
            else if (KeyboardShown)
            {
                HideKeyboard();
            }
        }

        private static Game.UI.Controls.Control _pannedGump;
        private static int _pannedBy;

        /// <summary>
        /// Once a frame: keep the focused text field above the keyboard. The
        /// window is not resized for the keyboard on an immersive Android
        /// run (Godot keeps its full-screen surface and the keyboard lies
        /// over it), so the login screen's fields, in the lower half of a
        /// centred 640x480, would be typed into blind. The field's gump is
        /// slid up by just enough and put back when the keyboard goes. The
        /// world viewport is never moved: its position is a profile value.
        /// </summary>
        private static void PanForKeyboard()
        {
            if (!DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            {
                return;
            }

            int keyboard = DisplayServer.VirtualKeyboardGetHeight();

            if (keyboard <= 0)
            {
                LiftChat(0);

                if (_pannedGump != null)
                {
                    if (!_pannedGump.IsDisposed)
                    {
                        _pannedGump.Y += _pannedBy;
                    }

                    Note($"keyboard gone -> gump back down {_pannedBy}");
                    _pannedGump = null;
                    _pannedBy = 0;
                }

                return;
            }

            if (UIManager.KeyboardFocusControl is not Game.UI.Controls.StbTextBox box
                || box.IsDisposed || !box.IsEditable || Client.Game == null)
            {
                LiftChat(0);

                return;
            }

            Game.UI.Controls.Control root = box.RootParent;
            float scale = Client.Game.DpiScale;
            int visible = (int)((DisplayServer.WindowGetSize().Y - keyboard) / scale);

            // The chat line lives in the world viewport, which never moves:
            // the line itself (and the history drawn above it) goes up.
            if (UIManager.SystemChat != null && box == UIManager.SystemChat.TextBoxControl)
            {
                int home = box.ScreenCoordinateY + box.Height + UIManager.SystemChat.KeyboardLift;
                LiftChat(System.Math.Max(0, home + 4 - visible));

                return;
            }

            LiftChat(0);

            if (root == null || root is Game.UI.Gumps.WorldViewportGump)
            {
                return;
            }

            if (_pannedGump != null && _pannedGump != root)
            {
                if (!_pannedGump.IsDisposed)
                {
                    _pannedGump.Y += _pannedBy;
                }

                _pannedGump = null;
                _pannedBy = 0;
            }

            // Two fields of room below the focused one, so the next field of a
            // form (the password under the account name) is in reach as well.
            int need = box.ScreenCoordinateY + box.Height * 3 + 8 - visible;

            if (need <= 0)
            {
                return;
            }

            root.Y -= need;
            _pannedBy += need;
            _pannedGump = root;
            Note($"keyboard {keyboard}px -> gump up {need}");
        }

        /// <summary>Raise the chat input line by this many client px (0 puts it back).</summary>
        private static void LiftChat(int by)
        {
            Game.UI.Gumps.SystemChatControl chat = UIManager.SystemChat;

            if (chat == null || chat.IsDisposed || chat.KeyboardLift == by)
            {
                return;
            }

            chat.KeyboardLift = by;
            chat.Resize();
            Note($"keyboard -> chat line up {by}");
        }

        private static void StartWalk()
        {
            Press(MouseButton.Right, _lastAt);
            _phase = Phase.RightHeld;
            Note($"hold on world -> right press (walk), under the finger: {SelectedObject.Object?.GetType().Name ?? "nothing"}");
        }

        private static void EndHeld(Vector2 at)
        {
            switch (_phase)
            {
                case Phase.LeftHeld:
                    Release(MouseButton.Left, at);

                    break;

                case Phase.RightHeld:
                    Release(MouseButton.Right, at);

                    break;
            }
        }

        /// <summary>Relative change in finger distance that is one zoom notch.</summary>
        public const float MagnifyStep = 1.12f;

        private static float _magnifyAccumulated = 1f;

        private static void Magnify(float factor, Vector2 at)
        {
            if (factor <= 0f)
            {
                return;
            }

            _magnifyAccumulated *= factor;

            while (_magnifyAccumulated >= MagnifyStep)
            {
                _magnifyAccumulated /= MagnifyStep;
                ZoomBy(1, at);
            }

            while (_magnifyAccumulated <= 1f / MagnifyStep)
            {
                _magnifyAccumulated *= MagnifyStep;
                ZoomBy(-1, at);
            }
        }

        private static void UpdatePinch()
        {
            float distance = _lastAt.DistanceTo(_secondaryAt);
            _pinchAccumulated += distance - _pinchDistance;
            _pinchDistance = distance;

            Vector2 centre = (_lastAt + _secondaryAt) / 2;

            while (_pinchAccumulated >= PinchStepPixels)
            {
                _pinchAccumulated -= PinchStepPixels;
                ZoomBy(1, centre);
            }

            while (_pinchAccumulated <= -PinchStepPixels)
            {
                _pinchAccumulated += PinchStepPixels;
                ZoomBy(-1, centre);
            }
        }

        /// <summary>
        /// One zoom notch, as the client takes it: Ctrl held, one wheel
        /// click over the world, Ctrl released.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): the client only zooms on the wheel when the
        /// profile's "enable mousewheel scale zoom" is on, an option a desktop
        /// player finds under Options. On a phone or in a browser the
        /// platform defaults turn it on (PlatformDefaults), and a player who
        /// turns it off is obeyed. On a desktop run with --touch (and the
        /// touch probe) the profile is a desktop one that never asked for it,
        /// so the first pinch turns it on.
        /// </remarks>
        private static void ZoomBy(int direction, Vector2 at)
        {
            Profile profile = ProfileManager.CurrentProfile;

            if (profile != null && !profile.EnableMousewheelScaleZoom)
            {
                if (PlatformDefaults.Platform != ProfilePlatform.Desktop)
                {
                    Note("pinch ignored: wheel zoom is off in this profile");

                    return;
                }

                profile.EnableMousewheelScaleZoom = true;
                Note("pinch -> profile.EnableMousewheelScaleZoom = true");
            }

            Motion(at);

            GodotInput.Handle(new InputEventKey
            {
                Keycode = Key.Ctrl,
                CtrlPressed = true,
                Pressed = true,
            });

            GodotInput.Handle(new InputEventMouseButton
            {
                ButtonIndex = direction > 0 ? MouseButton.WheelUp : MouseButton.WheelDown,
                Position = at,
                CtrlPressed = true,
                Pressed = true,
            });

            GodotInput.Handle(new InputEventKey
            {
                Keycode = Key.Ctrl,
                CtrlPressed = false,
                Pressed = false,
            });

            Note(direction > 0 ? "pinch out -> ctrl+wheel up (zoom in)" : "pinch in -> ctrl+wheel down (zoom out)");
        }

        /// <summary>
        /// Whether a point is on the world and not on a gump. Asked after the
        /// pointer has been moved there, because the client answers from
        /// Mouse.Position and what it found under it on the last draw.
        /// </summary>
        private static bool OverWorld(Vector2 at)
        {
            if (Client.Game?.Scene == null || ProfileManager.CurrentProfile == null)
            {
                return false;
            }

            return UIManager.IsMouseOverWorld;
        }

        private static void Motion(Vector2 at)
        {
            GodotInput.Handle(new InputEventMouseMotion { Position = at });
        }

        private static void Press(MouseButton button, Vector2 at)
        {
            GodotInput.Handle(new InputEventMouseButton
            {
                ButtonIndex = button,
                Position = at,
                Pressed = true,
            });
        }

        private static void Release(MouseButton button, Vector2 at)
        {
            GodotInput.Handle(new InputEventMouseButton
            {
                ButtonIndex = button,
                Position = at,
                Pressed = false,
            });
        }

        /// <summary>Echo the trace to the log as well (<c>--touch-trace</c>).</summary>
        public static bool TraceToLog { get; set; }

        internal static void Note(string what)
        {
            if (TraceToLog)
            {
                GD.Print($"[GUO] touch: {what}");
            }

            if (Trace.Count >= TraceLimit)
            {
                Trace.RemoveAt(0);
            }

            Trace.Add(what);
        }
    }
}
