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
    /// here and turned into what the client already understands. What each
    /// input does is the action map (<see cref="PadBindings"/>, rebindable by
    /// "Set controls", <see cref="PadWizard"/>); the defaults:
    /// <list type="bullet">
    /// <item>D-pad or left stick: walk, as GameScene walks on the arrow keys
    /// (fixed, unless a direction is bound to a job);</item>
    /// <item>right stick: moves the pointer (fixed);</item>
    /// <item>A: a left click at the pointer (confirm); B: Escape (cancel);</item>
    /// <item>X: attack last; on mobile layouts, the window menu for the
    /// topmost window instead (size, lock, screen), as before;</item>
    /// <item>Y: the macro row; LB: target last; RB: next hostile;</item>
    /// <item>L3: always run; R3: war mode; LB held and R3: cycle the debug UI
    /// scale (below 1 as well as above); Start: options; Back: the drawer.
    /// The D-pad is not part of that chord and still walks;</item>
    /// <item>LT held: the menu wheel (<see cref="PadWheel"/>); RT held: the
    /// interact radar (<see cref="PadRadar"/>).</item>
    /// </list>
    /// While the window menu is up, the D-pad selects the card's controls
    /// instead of walking, A presses the selected one and B closes it.
    /// </summary>
    /// <remarks>
    /// The face buttons act by their printed label, so the layout is resolved
    /// first (docs/thor-controller-layout.md): from Profile.GamepadLayout, or
    /// when that is "auto" from the pad's name and the device model. A pad
    /// that cannot be resolved gets no face-button actions and one message
    /// saying where to choose; it is never guessed. Walking needs no layout.
    ///
    /// On by default on every platform, the Windows desktop included, where
    /// upstream has no pad at all: the owner's decision of 2026-09-28
    /// (ADR-0025), over desktop 1:1. Options > "Use a controller"
    /// (Profile.Gamepad) turns it off, and then a pad does nothing
    /// (<see cref="Enabled"/>). Which input is in use, pad or keyboard and
    /// mouse, is <see cref="InputMode"/>.
    /// </remarks>
    internal static class GamepadInput
    {
        /// <summary>For probes: force the gate on or off; null follows the platform and the profile.</summary>
        public static bool? Forced { get; set; }

        /// <summary>Whether pad events do anything; see the remarks.</summary>
        public static bool Enabled => Forced ?? (ProfileManager.CurrentProfile?.Gamepad ?? true);

        /// <summary>Log every joypad event to the console (and logcat): --gamepad-trace.</summary>
        public static bool Trace { get; set; }

        private const float StickDeadzone = 0.5f;
        private const float PointerSpeed = 900f; // window pixels per second at full tilt

        private static bool _connectHooked;
        private static readonly Dictionary<int, GamepadLayout> _layouts = new();

        /// <summary>For a stand-in pad (<see cref="InputMode.StandIn"/>): its layout, as if detected.</summary>
        internal static void KnownLayout(int device, GamepadLayout layout) => _layouts[device] = layout;
        private static readonly HashSet<string> _toldUnknown = new();
        private static readonly bool[] _dpad = new bool[4];   // up, down, left, right
        private static readonly bool[] _stick = new bool[4];
        private static readonly bool[] _held = new bool[4];
        private static float _rightX, _rightY, _leftX, _leftY;
        internal const int TouchDevice = 240;
        private static bool _touchSource;

        // Each half of each axis, on past PadBindings.AxisOn and off under AxisOff.
        private static readonly HashSet<string> _axisOn = new();

        // Inputs still down when "Set controls" closed: their release is not an action.
        private static readonly HashSet<string> _swallow = new();
        private static bool _wizardHooked;

        // Whether A's press went to the client as a click (so its release does), and B's as Escape.
        private static bool _clickDown, _escapeDown;

        // LB held: R3 cycles the debug UI scale instead of doing R3's job. Lone R3 does not.
        private static bool _lbHeld;


        /// <summary>Returns true when the event was a joypad event and is handled.</summary>
        public static bool Handle(InputEvent e)
        {
            if (!Enabled && e.Device != TouchDevice)
            {
                // Not ours to take: the client goes on as if no pad were there.
                if (!_touchSource) ReleaseAll();

                return false;
            }

            HookConnections();

            // The pad-first pregame (docs/ui/pregame_3d.md) takes the D-pad, the left
            // stick, the face buttons, Start and the shoulders while it is up,
            // as the window menu takes the D-pad; the right stick still moves
            // the pointer. Off or not up, one false test.
            if (GUO.Pregame3D.PregameScreen.HandlePad(e))
            {
                return true;
            }

            // "Set controls" (and its offer) takes every pad event while it is up.
            HookWizard();

            if (PadWizard.IsOpen && e is InputEventJoypadButton or InputEventJoypadMotion)
            {
                ReleaseAll();
                PadWizard.Handle(e);

                return true;
            }

            switch (e)
            {
                case InputEventJoypadButton button:
                    _touchSource = button.Device == TouchDevice;
                    if (Trace)
                    {
                        GD.Print($"[GUO] gamepad: device {button.Device} \"{Godot.Input.GetJoyName(button.Device)}\" button {(int) button.ButtonIndex} ({button.ButtonIndex}) {(button.Pressed ? "down" : "up")}");
                    }

                    OnButton(button);

                    return true;

                case InputEventJoypadMotion motion:
                    _touchSource = motion.Device == TouchDevice;
                    if (Trace && Math.Abs(motion.AxisValue) > StickDeadzone)
                    {
                        GD.Print($"[GUO] gamepad: device {motion.Device} axis {(int) motion.Axis} ({motion.Axis}) {motion.AxisValue:0.00}");
                    }

                    OnMotion(motion);

                    return true;
            }

            return false;
        }

        /// <summary>
        /// Once a frame: a held direction walks, a tilted right stick moves the
        /// pointer; the menu wheel and the radar follow the sticks while they
        /// are up, and "Set controls" counts its hold.
        /// </summary>
        public static void Update(double delta)
        {
            if (!Enabled && !_touchSource)
            {
                ReleaseAll();

                return;
            }

            PadWizard.Update(delta);
            PadWizard.MaybeOffer();

            if (PadWizard.IsOpen)
            {
                return;
            }

            // A full-screen window takes the sticks: no walking, no pointer.
            if (PadScreen.IsOpen)
            {
                PadScreen.Tick();
                PadScreen.Steer(_leftX, _leftY, (float) delta);

                return;
            }

            // The wheel takes both sticks: no walking, no pointer, while it is up.
            if (PadWheel.IsOpen)
            {
                PadWheel.Steer(_leftX, _leftY, _rightX, _rightY);

                return;
            }

            Walk();

            // The radar takes the right stick; the left one still walks.
            if (PadRadar.IsOpen)
            {
                PadRadar.Update(_rightX, _rightY);

                return;
            }

            if (Math.Abs(_rightX) < 0.2f && Math.Abs(_rightY) < 0.2f)
            {
                return;
            }

            InputMode.PointerUsed();
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
            // Printed touchscreen labels never inherit a hardware button swap.
            if (device == TouchDevice) return GamepadLayout.Labels;
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

        /// <summary>
        /// A button by its printed label: the face buttons through the pad's
        /// layout (null while it is unknown), every other button as it is.
        /// </summary>
        internal static JoyButton? PrintedButton(JoyButton b, int device) =>
            b is JoyButton.A or JoyButton.B or JoyButton.X or JoyButton.Y ? Printed(b, Resolve(device)) : b;

        /// <summary>As <see cref="PrintedButton"/>, but an unknown pad's face buttons as they come ("Set controls" binds what it is given).</summary>
        internal static JoyButton PrintedOrRaw(JoyButton b, int device) => PrintedButton(b, device) ?? b;

        // ==========================
        // === Dispatch =============
        // ==========================

        private static void OnButton(InputEventJoypadButton e)
        {
            if (e.ButtonIndex == JoyButton.LeftShoulder)
            {
                _lbHeld = e.Pressed;
            }

            // LB + R3 (click). Physical buttons, not the rebinding: lone R3 still
            // does its job (war mode, by default). Does not touch the D-pad.
            if (e.Pressed && e.ButtonIndex == JoyButton.RightStick && _lbHeld)
            {
                PadOverlay.CycleDebugScale();

                return;
            }

            // Generic focus-graph over a classic server/client gump (G1).
            if (PadGumpNav.HandleButton(e.ButtonIndex, e.Pressed))
            {
                return;
            }

            // A full-screen window takes the D-pad: move in its list or grid, not walk.
            if (PadScreen.IsOpen && e.ButtonIndex is JoyButton.DpadUp or JoyButton.DpadDown or JoyButton.DpadLeft or JoyButton.DpadRight)
            {
                if (e.Pressed)
                {
                    int dx = e.ButtonIndex == JoyButton.DpadLeft ? -1 : e.ButtonIndex == JoyButton.DpadRight ? 1 : 0;
                    int dy = e.ButtonIndex == JoyButton.DpadUp ? -1 : e.ButtonIndex == JoyButton.DpadDown ? 1 : 0;
                    PadScreen.Move(dx, dy);
                }

                return;
            }

            // An open window menu takes the D-pad: select its controls, not walk.
            if (Touch.WindowMenu.IsOpen && e.ButtonIndex is JoyButton.DpadUp or JoyButton.DpadDown or JoyButton.DpadLeft or JoyButton.DpadRight)
            {
                if (e.Pressed)
                {
                    Touch.WindowMenu.Navigate(e.ButtonIndex switch
                    {
                        JoyButton.DpadUp => "ui_up",
                        JoyButton.DpadDown => "ui_down",
                        JoyButton.DpadLeft => "ui_left",
                        _ => "ui_right",
                    });
                }

                return;
            }

            JoyButton? printed = PrintedButton(e.ButtonIndex, e.Device);

            if (printed == null)
            {
                TellUnknown(e.Device);

                return;
            }

            var input = PadInput.Button(printed.Value);

            if (!e.Pressed && _swallow.Remove(input.Id))
            {
                return;
            }

            _swallow.Remove(input.Id);

            // Every job is read from the action map (PadBindings); an input
            // bound to none still walks if it is the D-pad.
            if (PadBindings.CommandFor(input) is PadCommand command)
            {
                Dispatch(command, e.Pressed);

                return;
            }

            switch (e.ButtonIndex)
            {
                case JoyButton.DpadUp: _dpad[0] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadDown: _dpad[1] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadLeft: _dpad[2] = e.Pressed; UpdateArrows(); return;
                case JoyButton.DpadRight: _dpad[3] = e.Pressed; UpdateArrows(); return;
            }
        }

        /// <summary>One job, pressed or let go, from whichever input it is bound to.</summary>
        private static void Dispatch(PadCommand command, bool pressed)
        {
            var world = Client.Game?.UO?.World;
            bool inWorld = world != null && world.InGame && world.Player != null && Client.Game.Scene is Game.Scenes.GameScene;

            switch (command)
            {
                case PadCommand.Use:
                    if (PadScreen.IsOpen)
                    {
                        if (pressed)
                        {
                            PadScreen.Activate();
                        }

                        return;
                    }

                    if (PadRadar.IsOpen)
                    {
                        if (pressed)
                        {
                            PadRadar.Use();
                        }

                        return;
                    }

                    if (!pressed && !_clickDown)
                    {
                        return;
                    }

                    InputMode.PointerUsed();

                    if (!Touch.WindowMenu.Accept(pressed))
                    {
                        _clickDown = pressed;
                        Click(pressed);
                    }

                    return;

                case PadCommand.Cancel:
                    if (pressed && PadScreen.IsOpen)
                    {
                        PadScreen.Close();

                        return;
                    }

                    if (pressed && PadRadar.IsOpen)
                    {
                        PadRadar.Cancel();

                        return;
                    }

                    if (pressed && PadWheel.IsOpen)
                    {
                        PadWheel.Close();

                        return;
                    }

                    if (Touch.WindowMenu.IsOpen)
                    {
                        if (pressed)
                        {
                            Touch.WindowMenu.Close();
                        }

                        return;
                    }

                    if (!pressed && !_escapeDown)
                    {
                        return;
                    }

                    _escapeDown = pressed;
                    PressKey(Godot.Key.Escape, pressed);

                    return;

                case PadCommand.MenuWheel:
                    if (pressed && PadScreen.IsOpen)
                    {
                        PadScreen.Close();
                    }

                    if (pressed && !PadRadar.IsOpen && inWorld)
                    {
                        PadWheel.Begin();
                    }
                    else if (!pressed)
                    {
                        PadWheel.End();
                    }

                    return;

                case PadCommand.InteractRadar:
                    if (pressed && PadScreen.IsOpen)
                    {
                        PadScreen.Close();
                    }

                    if (pressed && !PadWheel.IsOpen && inWorld)
                    {
                        PadRadar.Begin();
                    }
                    else if (!pressed)
                    {
                        PadRadar.End();
                    }

                    return;
            }

            // The rest act once, on the press.
            if (!pressed)
            {
                return;
            }

            if (PadScreen.IsOpen)
            {
                switch (command)
                {
                    case PadCommand.TargetLast: PadScreen.Page(-1); break;
                    case PadCommand.NextHostile: PadScreen.Page(1); break;
                }

                return;
            }

            switch (command)
            {
                case PadCommand.AttackLast:
                    if (PadScreen.IsOpen)
                    {
                        if (pressed && PadScreen.HasItemActions)
                        {
                            PadScreen.Drop();
                        }

                        return;
                    }

                    if (PadRadar.IsOpen)
                    {
                        if (Client.Game?.UO?.World?.Player?.InWarMode == true)
                        {
                            if (pressed) PadWheel.RunMacro(Client.Game.UO.World, Game.Managers.MacroType.AttackLast);
                        }
                        else
                        {
                            PadRadar.Look();
                        }
                    }
                    else if (Touch.WindowMenu.IsOpen)
                    {
                        Touch.WindowMenu.Close();
                    }
                    else if (Touch.GumpPresentation.Active)
                    {
                        // Mobile layouts keep this button's older job: the window
                        // menu for the topmost window (ADR-0025).
                        Touch.GumpPresentation.OpenMenuForTop();
                    }
                    else if (inWorld)
                    {
                        PadWheel.RunMacro(world, Game.Managers.MacroType.AttackLast);
                    }

                    return;

                case PadCommand.TargetLast:
                    if (PadRadar.IsOpen)
                    {
                        PadRadar.Step(-1);
                    }
                    else if (inWorld)
                    {
                        PadWheel.RunMacro(world, Game.Managers.MacroType.LastTarget);
                    }

                    return;

                case PadCommand.NextHostile:
                    if (PadRadar.IsOpen)
                    {
                        PadRadar.Step(1);
                    }
                    else if (inWorld)
                    {
                        PadWheel.RunMacro(world, Game.Managers.MacroType.SelectNext, Game.Managers.MacroSubType.Hostile);
                    }

                    return;

                case PadCommand.WarMode:
                    if (inWorld)
                    {
                        PadWheel.RunMacro(world, Game.Managers.MacroType.WarPeace);
                    }

                    return;

                case PadCommand.AlwaysRun:
                    if (inWorld)
                    {
                        PadWheel.RunMacro(world, Game.Managers.MacroType.AlwaysRun);
                    }

                    return;

                case PadCommand.MacroRow:
                    if (PadScreen.IsOpen)
                    {
                        if (pressed && PadScreen.HasItemActions)
                        {
                            PadScreen.Equip();
                        }

                        return;
                    }

                    if (PadRadar.IsOpen)
                    {
                        PadRadar.Context();
                    }
                    else if (Touch.TouchInput.Bar is { } bar && bar.HandleShown)
                    {
                        bar.ToggleRow();
                    }
                    else if (inWorld)
                    {
                        // No macro row on this layout (a Deck): the button used to
                        // call ToggleRow on a bar whose handle is hidden, which
                        // returns without opening anything.
                        PadScreen.Open(WheelWindow.Macros);
                    }

                    return;

                case PadCommand.Options:
                    if (inWorld)
                    {
                        // Adaptive Android keeps the macros drawer on this button;
                        // elsewhere the pad's Options opens the client settings.
                        if (Platform.Android.AdaptiveLayout.Active)
                        {
                            Platform.Android.AdaptiveLayout.ToggleMacros();
                        }
                        else
                        {
                            Game.GameActions.OpenSettings(world);
                        }
                    }

                    return;

                case PadCommand.Drawer:
                    if (PadScreen.IsOpen)
                    {
                        if (pressed && PadScreen.HasItemActions)
                        {
                            PadScreen.Context();
                        }

                        return;
                    }

                    // The one-screen drawer: a no-op with a second screen, or with the panel off.
                    GUO.Platform.Android.DualScreen.ToggleDrawer();

                    return;
            }
        }

        private static void OnMotion(InputEventJoypadMotion e)
        {
            switch (e.Axis)
            {
                case JoyAxis.LeftX: _leftX = e.AxisValue; break;
                case JoyAxis.LeftY: _leftY = e.AxisValue; break;
                case JoyAxis.RightX: _rightX = e.AxisValue; break;
                case JoyAxis.RightY: _rightY = e.AxisValue; break;
            }

            // Each half of the axis is an input of its own: a trigger, or a
            // stick pushed one way. A half bound to a job acts on and off.
            Half(PadInput.Axis(e.Axis, 1), e.AxisValue);
            Half(PadInput.Axis(e.Axis, -1), -e.AxisValue);

            // The left stick walks, on whatever half is bound to no job.
            if (e.Axis is JoyAxis.LeftX or JoyAxis.LeftY)
            {
                int neg = e.Axis == JoyAxis.LeftX ? 2 : 0;
                _stick[neg] = e.AxisValue < -StickDeadzone && PadBindings.CommandFor(PadInput.Axis(e.Axis, -1)) == null;
                _stick[neg + 1] = e.AxisValue > StickDeadzone && PadBindings.CommandFor(PadInput.Axis(e.Axis, 1)) == null;
                UpdateArrows();
            }
        }

        private static void Half(PadInput input, float value)
        {
            bool was = _axisOn.Contains(input.Id);
            bool now = was ? value > PadBindings.AxisOff : value >= PadBindings.AxisOn;

            if (now == was)
            {
                return;
            }

            if (now)
            {
                _axisOn.Add(input.Id);
                _swallow.Remove(input.Id);
            }
            else
            {
                _axisOn.Remove(input.Id);

                if (_swallow.Remove(input.Id))
                {
                    return;
                }
            }

            if (PadBindings.CommandFor(input) is PadCommand command)
            {
                Dispatch(command, now);
            }
        }

        private static void HookWizard()
        {
            if (_wizardHooked)
            {
                return;
            }

            _wizardHooked = true;
            PadWizard.Closed += held =>
            {
                _swallow.UnionWith(held);
                _axisOn.Clear();
                _leftX = _leftY = _rightX = _rightY = 0f;
            };
        }

        /// <summary>Drop anything held when the gate closes, so nothing keeps walking.</summary>
        internal static void ReleaseAll()
        {
            _touchSource = false;
            _lbHeld = false;

            if (_held[0] || _held[1] || _held[2] || _held[3] || _rightX != 0f || _rightY != 0f || _leftX != 0f || _leftY != 0f)
            {
                Array.Clear(_dpad);
                Array.Clear(_stick);
                UpdateArrows();
                _rightX = _rightY = _leftX = _leftY = 0f;
            }

            if (PadWheel.IsOpen)
            {
                PadWheel.Close();
            }

            if (PadRadar.IsOpen)
            {
                PadRadar.Close();
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
            _rightX = _rightY = _leftX = _leftY = 0;
            _axisOn.Clear();
            PadWheel.Close();
            PadRadar.Close();

            Describe((int) device, connected ? "connected" : "disconnected");
        }

        private static void Describe(int device, string what)
        {
            string name = Godot.Input.GetJoyName(device);
            GD.Print($"[GUO] gamepad: device {device} {what}: \"{name}\" guid {Godot.Input.GetJoyGuid(device)} known {Godot.Input.IsJoyKnown(device)}"
                + (what == "disconnected" ? "" : $", layout {Detect(name, OS.GetModelName(), Godot.Input.IsJoyKnown(device))}, glyphs {InputMode.FamilyOf(name)}"
                    + $" (model \"{OS.GetModelName()}\"{Board()})"));
        }

        /// <summary>
        /// On Linux, the board the client runs on, from the firmware's DMI
        /// table: a Steam Deck is "Valve Jupiter" (LCD) or "Valve Galileo"
        /// (OLED), where OS.GetModelName says only "GenericDevice". Logged
        /// with the pad, for the Deck's layout check (S5); nothing decides by it.
        /// </summary>
        private static string Board()
        {
            if (!OperatingSystem.IsLinux())
            {
                return "";
            }

            try
            {
                string vendor = System.IO.File.ReadAllText("/sys/devices/virtual/dmi/id/board_vendor").Trim();
                string board = System.IO.File.ReadAllText("/sys/devices/virtual/dmi/id/board_name").Trim();
                return $", board \"{vendor} {board}\"";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }
}
