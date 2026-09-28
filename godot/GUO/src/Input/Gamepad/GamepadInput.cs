// GUO addition, not a port: upstream ClassicUO (at the reviewed pin) has no
// gamepad support at all.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;

namespace GUO.Input.Gamepad
{
    /// <summary>How a pad's A/B/X/Y reach Godot, relative to what is printed on it.</summary>
    internal enum GamepadLayout
    {
        /// <summary>Not recognised: the face buttons wait for a manual choice.</summary>
        Unknown,

        /// <summary>Godot's A/B/X/Y are the printed A/B/X/Y (the Thor in Standard mode, SDL-mapped pads).</summary>
        Labels,

        /// <summary>A&lt;-&gt;B and X&lt;-&gt;Y swapped (the Thor's built-in pad in XBox mode).</summary>
        Swapped
    }

    /// <summary>
    /// Gamepad events, in front of the touch layer and GodotInput the way the
    /// touch layer is in front of GodotInput. Every joypad event is consumed
    /// here and turned into what the client already understands:
    /// <list type="bullet">
    /// <item>D-pad or left stick: walk, as GameScene walks on the arrow keys;</item>
    /// <item>printed A: a left click at the pointer (confirm);</item>
    /// <item>printed B: Escape (cancel: a target cursor, a text field);</item>
    /// <item>printed Y: the touch bar's macro row, open or closed;</item>
    /// <item>printed X: the window menu (size, lock, screen) for the topmost
    /// window, or closed again; mobile only, as the menu is. While it is up, A
    /// presses the card's control under the pointer and B closes it;</item>
    /// <item>right stick: moves the pointer.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The face buttons act by their printed label, so the layout is resolved
    /// first (docs/thor-controller-layout.md): from Profile.GamepadLayout, or
    /// when that is "auto" from the pad's name and the device model. A pad
    /// that cannot be resolved gets no face-button actions and one message
    /// saying where to choose; it is never guessed. Walking needs no layout.
    /// </remarks>
    internal static class GamepadInput
    {
        /// <summary>Log every joypad event to the console (and logcat): --gamepad-trace.</summary>
        public static bool Trace { get; set; }

        private const float StickDeadzone = 0.5f;
        private const float PointerSpeed = 900f; // window pixels per second at full tilt

        private static bool _connectHooked;
        private static readonly Dictionary<int, GamepadLayout> _layouts = new();
        private static readonly HashSet<string> _toldUnknown = new();
        private static readonly bool[] _dpad = new bool[4];   // up, down, left, right
        private static readonly bool[] _stick = new bool[4];
        private static readonly bool[] _held = new bool[4];
        private static float _rightX, _rightY;


        /// <summary>Returns true when the event was a joypad event and is handled.</summary>
        public static bool Handle(InputEvent e)
        {
            HookConnections();

            switch (e)
            {
                case InputEventJoypadButton button:
                    if (Trace)
                    {
                        GD.Print($"[GUO] gamepad: device {button.Device} \"{Godot.Input.GetJoyName(button.Device)}\" button {(int) button.ButtonIndex} ({button.ButtonIndex}) {(button.Pressed ? "down" : "up")}");
                    }

                    OnButton(button);

                    return true;

                case InputEventJoypadMotion motion:
                    if (Trace && Math.Abs(motion.AxisValue) > StickDeadzone)
                    {
                        GD.Print($"[GUO] gamepad: device {motion.Device} axis {(int) motion.Axis} ({motion.Axis}) {motion.AxisValue:0.00}");
                    }

                    OnMotion(motion);

                    return true;
            }

            return false;
        }

        /// <summary>Once a frame: a held direction walks, a tilted right stick moves the pointer.</summary>
        public static void Update(double delta)
        {
            Walk();

            if (Math.Abs(_rightX) < 0.2f && Math.Abs(_rightY) < 0.2f)
            {
                return;
            }

            float scale = (float) Client.Game.DpiScale;
            Vector2 at = new Vector2(Mouse.Position.X, Mouse.Position.Y) * scale
                + new Vector2(_rightX, _rightY) * PointerSpeed * (float) delta;
            Vector2I size = DisplayServer.WindowGetSize();
            at = new Vector2(Math.Clamp(at.X, 0, size.X - 1), Math.Clamp(at.Y, 0, size.Y - 1));

            GodotInput.Handle(new InputEventMouseMotion { Position = at });
        }

        // ==========================
        // === Layout ===============
        // ==========================

        /// <summary>The layout from the profile, or from the pad and the device when that is "auto".</summary>
        public static GamepadLayout Resolve(int device)
        {
            string manual = ProfileManager.CurrentProfile?.GamepadLayout ?? "auto";

            if (manual == "labels")
            {
                return GamepadLayout.Labels;
            }

            if (manual == "swapped")
            {
                return GamepadLayout.Swapped;
            }

            if (_layouts.TryGetValue(device, out GamepadLayout known))
            {
                return known;
            }

            return _layouts[device] = Detect(Godot.Input.GetJoyName(device), OS.GetModelName(), Godot.Input.IsJoyKnown(device));
        }

        /// <summary>
        /// Measured on the AYN Thor (docs/thor-controller-layout.md): its
        /// Controller style tile makes the built-in pad "Odin Controller"
        /// (printed labels) or "Xbox Series X Controller" (swapped). The Odin
        /// 2 Mini's pad is also "Odin Controller" but is not measured, so it
        /// is Unknown until someone measures it. Any other pad Godot has
        /// an SDL mapping for delivers the printed labels of an Xbox-style
        /// pad. Everything else is Unknown.
        /// </summary>
        public static GamepadLayout Detect(string name, string model, bool sdlKnown)
        {
            bool thor = model != null && model.Contains("Thor", StringComparison.OrdinalIgnoreCase);
            bool ayn = thor || (model != null && model.Contains("Odin", StringComparison.OrdinalIgnoreCase));

            if (thor && name == "Odin Controller")
            {
                return GamepadLayout.Labels;
            }

            if (thor && name != null && name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            {
                return GamepadLayout.Swapped;
            }

            // An AYN handheld other than the Thor (the Odin 2 Mini) names its
            // pad the same way but is not measured: a manual choice, not a guess.
            if (ayn || name == "Odin Controller")
            {
                return GamepadLayout.Unknown;
            }

            return sdlKnown ? GamepadLayout.Labels : GamepadLayout.Unknown;
        }

        /// <summary>A Godot face button as the label printed on the pad, or null.</summary>
        private static JoyButton? Printed(JoyButton logical, GamepadLayout layout)
        {
            switch (layout)
            {
                case GamepadLayout.Labels:
                    return logical;

                case GamepadLayout.Swapped:
                    return logical switch
                    {
                        JoyButton.A => JoyButton.B,
                        JoyButton.B => JoyButton.A,
                        JoyButton.X => JoyButton.Y,
                        JoyButton.Y => JoyButton.X,
                        _ => logical
                    };

                default:
                    return null;
            }
        }

        // ==========================
        // === Dispatch =============
        // ==========================

        private static void OnButton(InputEventJoypadButton e)
        {
            switch (e.ButtonIndex)
            {
                case JoyButton.DpadUp: _dpad[0] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadDown: _dpad[1] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadLeft: _dpad[2] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadRight: _dpad[3] = e.Pressed; UpdateArrows(); return;
                case JoyButton.A or JoyButton.B or JoyButton.X or JoyButton.Y: break;
                default: return;
            }

            GamepadLayout layout = Resolve(e.Device);
            JoyButton? printed = Printed(e.ButtonIndex, layout);

            if (printed == null)
            {
                TellUnknown(e.Device);

                return;
            }

            switch (printed.Value)
            {
                case JoyButton.A:
                    Click(e.Pressed);

                    break;

                case JoyButton.B:
                    if (Touch.WindowMenu.IsOpen)
                    {
                        if (e.Pressed)
                        {
                            Touch.WindowMenu.Close();
                        }

                        break;
                    }

                    PressKey(Godot.Key.Escape, e.Pressed);

                    break;

                case JoyButton.X:
                    if (e.Pressed)
                    {
                        if (Touch.WindowMenu.IsOpen)
                        {
                            Touch.WindowMenu.Close();
                        }
                        else
                        {
                            Touch.GumpPresentation.OpenMenuForTop();
                        }
                    }

                    break;

                case JoyButton.Y:
                    if (e.Pressed)
                    {
                        Touch.TouchInput.Bar?.ToggleRow();
                    }

                    break;
            }
        }

        private static void OnMotion(InputEventJoypadMotion e)
        {
            switch (e.Axis)
            {
                case JoyAxis.LeftX:
                    _stick[2] = e.AxisValue < -StickDeadzone;
                    _stick[3] = e.AxisValue > StickDeadzone;
                    UpdateArrows();

                    break;

                case JoyAxis.LeftY:
                    _stick[0] = e.AxisValue < -StickDeadzone;
                    _stick[1] = e.AxisValue > StickDeadzone;
                    UpdateArrows();

                    break;

                case JoyAxis.RightX:
                    _rightX = e.AxisValue;

                    break;

                case JoyAxis.RightY:
                    _rightY = e.AxisValue;

                    break;
            }
        }

        /// <summary>The D-pad and the stick together, as the four arrow keys.</summary>
        private static void UpdateArrows()
        {
            for (int i = 0; i < 4; i++)
            {
                _held[i] = _dpad[i] || _stick[i];
            }
        }

        /// <summary>
        /// Walk the held direction, as GameScene.Update does for the arrow
        /// keys (DirectionFromKeyboardArrows, then Player.Walk, which paces
        /// itself). Not through synthetic arrow keys: GameScene only takes
        /// those while the chat box has the keyboard focus.
        /// </summary>
        private static void Walk()
        {
            if (!(_held[0] || _held[1] || _held[2] || _held[3]))
            {
                return;
            }

            var world = Client.Game?.UO?.World;
            Profile profile = ProfileManager.CurrentProfile;

            if (world == null || !world.InGame || world.Player == null || profile == null || profile.DisableArrowBtn
                || world.Player.Pathfinder.AutoWalking || !(Client.Game.Scene is Game.Scenes.GameScene))
            {
                return;
            }

            Game.Data.Direction dir = Game.Data.DirectionHelper.DirectionFromKeyboardArrows(_held[0], _held[1], _held[2], _held[3]);

            if (dir != Game.Data.Direction.NONE)
            {
                world.Player.Walk(dir, profile.AlwaysRun);
            }
        }

        private static void PressKey(Key key, bool pressed)
        {
            GodotInput.Handle(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
        }

        private static void Click(bool pressed)
        {
            float scale = (float) Client.Game.DpiScale;
            Vector2 at = new Vector2(Mouse.Position.X, Mouse.Position.Y) * scale;
            var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at };

            // The window menu takes pointer events before the game does, as it
            // does a touch; outside the card a press closes it.
            if (Touch.WindowMenu.HandleInput(click))
            {
                return;
            }

            GodotInput.Handle(click);
        }

        private static void TellUnknown(int device)
        {
            string name = Godot.Input.GetJoyName(device);

            if (!_toldUnknown.Add(name))
            {
                return;
            }

            string text = $"Controller \"{name}\": its button layout is not known, so A/B/X/Y do nothing yet. Choose it under Options > Video > Controller buttons.";
            GD.Print("[GUO] gamepad: " + text);

            if (Client.Game?.UO?.World?.InGame ?? false)
            {
                Game.GameActions.Print(Client.Game.UO.World, text, 0x35, Game.Data.MessageType.System);
            }
        }

        /// <summary>Forget resolved layouts, e.g. when the manual choice changes.</summary>
        public static void ForgetLayouts()
        {
            _layouts.Clear();
            _toldUnknown.Clear();
        }

        // ==========================
        // === Connections ==========
        // ==========================

        private static void HookConnections()
        {
            if (_connectHooked)
            {
                return;
            }

            _connectHooked = true;
            Godot.Input.Singleton.JoyConnectionChanged += OnJoyConnectionChanged;

            foreach (int device in Godot.Input.GetConnectedJoypads())
            {
                Describe(device, "present");
            }
        }

        private static void OnJoyConnectionChanged(long device, bool connected)
        {
            // The Thor's Controller style tile reconnects the pad under a new
            // name: resolve again, and let go of anything it was holding.
            _layouts.Remove((int) device);
            Array.Clear(_dpad);
            Array.Clear(_stick);
            UpdateArrows();
            _rightX = _rightY = 0;

            Describe((int) device, connected ? "connected" : "disconnected");
        }

        private static void Describe(int device, string what)
        {
            string name = Godot.Input.GetJoyName(device);
            GD.Print($"[GUO] gamepad: device {device} {what}: \"{name}\" guid {Godot.Input.GetJoyGuid(device)} known {Godot.Input.IsJoyKnown(device)}"
                + (what == "disconnected" ? "" : $", layout {Detect(name, OS.GetModelName(), Godot.Input.IsJoyKnown(device))} (model \"{OS.GetModelName()}\")"));
        }
    }
}
