// GUO addition, not a port: upstream ClassicUO has no gamepad, so it never
// needs to know which kind of input the player is using.

using System;
using Godot;
using GUO.Input.Gamepad;

namespace GUO.Input
{
    /// <summary>What the player is using right now.</summary>
    internal enum InputKind
    {
        KeyboardMouse,
        Gamepad,
        Touch
    }

    /// <summary>Which glyph set a pad's buttons are drawn from.</summary>
    internal enum PadFamily
    {
        Xbox,
        PlayStation,
        Nintendo,
        SteamDeck,
        Generic
    }

    /// <summary>A job a pad does, for asking which button does it (<see cref="InputMode.ButtonFor"/>).</summary>
    internal enum PadAction
    {
        Confirm,     // printed A
        Cancel,      // printed B
        WindowMenu,  // printed X
        Y,           // printed Y: the macro row
        Walk,        // the D-pad or the left stick
        Pointer,     // the right stick
        LB,
        RB,
        LT,
        RT,
        Start,
        Back
    }

    /// <summary>
    /// The input the player last used, switched the moment another kind is
    /// used (the owner's decision of 2026-09-28, ADR-0025): a pad button or a
    /// stick past its deadzone makes it Gamepad; a key, a mouse button or the
    /// mouse moving a few pixels makes it KeyboardMouse; a touch makes it Touch.
    /// There is no delay either way. The UI follows <see cref="Changed"/>:
    /// GUOUI's glyphs, and the pointer here (<see cref="PointerHidden"/>).
    /// </summary>
    /// <remarks>
    /// A mouse event Godot made from a touch (device -1) is the touch, not a
    /// mouse, and the pointer moves the pad's right stick makes go straight to
    /// GodotInput, not through <see cref="Note"/>, so neither switches back.
    /// </remarks>
    internal static class InputMode
    {
        private const float StickDeadzone = 0.5f;
        private const float MouseSlop = 4f; // window pixels the mouse must move to count
        private const ulong PointerIdleMs = 4000;

        private static float _mouseTravel;
        private static ulong _pointerUsedAt;

        public static InputKind Current { get; private set; } = OS.HasFeature("mobile") ? InputKind.Touch : InputKind.KeyboardMouse;

        /// <summary>
        /// On the main thread, when <see cref="Current"/> changes, and when
        /// another pad (or a pad of another family) is picked up while it
        /// stays Gamepad.
        /// </summary>
        public static event Action<InputKind> Changed;

        /// <summary>The pad last used; -1 if none yet.</summary>
        public static int PadDevice { get; private set; } = -1;

        /// <summary>Godot's name for <see cref="PadDevice"/>.</summary>
        public static string PadName { get; private set; } = "";

        /// <summary>How <see cref="PadDevice"/>'s A/B/X/Y map to their printed labels.</summary>
        public static GamepadLayout PadLayout => PadDevice < 0 ? GamepadLayout.Unknown : GamepadInput.Resolve(PadDevice);

        public static PadFamily PadFamily { get; private set; } = PadFamily.Generic;

        /// <summary>
        /// The printed button that does <paramref name="action"/> on the pad in
        /// use, for its glyph; null when the layout is unknown (the face
        /// buttons do nothing yet) and for Walk, Pointer, LT and RT, which are
        /// a D-pad, sticks and triggers rather than buttons.
        /// </summary>
        public static JoyButton? ButtonFor(PadAction action)
        {
            switch (action)
            {
                case PadAction.Confirm: return Face(JoyButton.A);
                case PadAction.Cancel: return Face(JoyButton.B);
                case PadAction.WindowMenu: return Face(JoyButton.X);
                case PadAction.Y: return Face(JoyButton.Y);
                case PadAction.LB: return JoyButton.LeftShoulder;
                case PadAction.RB: return JoyButton.RightShoulder;
                case PadAction.Start: return JoyButton.Start;
                case PadAction.Back: return JoyButton.Back;
                default: return null;
            }
        }

        private static JoyButton? Face(JoyButton printed) => PadLayout == GamepadLayout.Unknown ? null : printed;

        /// <summary>
        /// Hide the client's own cursor: with the pad in use and its pointer
        /// idle, as a console game does. It shows again the moment the right
        /// stick moves or the mouse is used, and stays while a target cursor
        /// is up or an item is held, since then it says where A lands.
        /// </summary>
        public static bool PointerHidden
        {
            get
            {
                if (Current != InputKind.Gamepad || Godot.Time.GetTicksMsec() - _pointerUsedAt < PointerIdleMs)
                {
                    return false;
                }

                var world = Client.Game?.UO?.World;

                return !(world?.TargetManager.IsTargeting ?? false) && !(Client.Game?.UO?.GameCursor?.ItemHold.Enabled ?? false)
                    && !Touch.WindowMenu.IsOpen;
            }
        }

        /// <summary>The pad pointer moved, or a pad button acted at it: show the pointer.</summary>
        public static void PointerUsed()
        {
            _pointerUsedAt = Godot.Time.GetTicksMsec();
        }

        /// <summary>Every input event, before anything handles it (GameController._Input).</summary>
        public static void Note(InputEvent e)
        {
            switch (e)
            {
                case InputEventJoypadButton button when button.Pressed:
                    UsePad(button.Device);

                    break;

                case InputEventJoypadMotion motion when Math.Abs(motion.AxisValue) > StickDeadzone:
                    UsePad(motion.Device);

                    break;

                case InputEventScreenTouch:
                case InputEventScreenDrag:
                    Switch(InputKind.Touch);

                    break;

                case InputEventMouse mouse when mouse.Device == InputEvent.DeviceIdEmulation:
                    // Godot's mouse from a touch: the touch already counted.
                    break;

                case InputEventMouseMotion motion:
                    if (Current == InputKind.KeyboardMouse)
                    {
                        break;
                    }

                    _mouseTravel += motion.Relative.Length();

                    if (_mouseTravel >= MouseSlop)
                    {
                        Switch(InputKind.KeyboardMouse);
                    }

                    break;

                case InputEventMouseButton { Pressed: true }:
                case InputEventKey { Pressed: true }:
                    Switch(InputKind.KeyboardMouse);

                    break;
            }
        }

        private static void UsePad(int device)
        {
            bool otherPad = device != PadDevice;

            if (otherPad)
            {
                PadDevice = device;
                PadName = Godot.Input.GetJoyName(device);
                PadFamily = FamilyOf(PadName);
            }

            if (Current != InputKind.Gamepad)
            {
                Switch(InputKind.Gamepad);
            }
            else if (otherPad)
            {
                GD.Print($"[GUO] input mode: Gamepad, now \"{PadName}\" ({PadFamily})");
                Changed?.Invoke(Current);
            }
        }

        /// <summary>
        /// The glyph set for a pad's name. The Thor's built-in pad is "Odin
        /// Controller" or "Xbox Series X Controller" by its Controller style
        /// tile, with the buttons where an Xbox pad has them either way.
        /// "Steam Deck" is the Deck's built-in pad as SDL's controller
        /// database names it; to be confirmed on the Deck (S5).
        /// </summary>
        public static PadFamily FamilyOf(string name)
        {
            string n = name ?? "";
            bool Has(string word) => n.Contains(word, StringComparison.OrdinalIgnoreCase);

            if (Has("Steam Deck"))
            {
                return PadFamily.SteamDeck;
            }

            if (Has("PlayStation") || Has("DualSense") || Has("DualShock") || Has("PS3") || Has("PS4") || Has("PS5") || Has("Sony"))
            {
                return PadFamily.PlayStation;
            }

            if (Has("Nintendo") || Has("Switch") || Has("Joy-Con") || Has("Pro Controller"))
            {
                return PadFamily.Nintendo;
            }

            if (Has("Xbox") || Has("XInput") || Has("X-Box") || Has("Odin Controller"))
            {
                return PadFamily.Xbox;
            }

            return PadFamily.Generic;
        }

        /// <summary>For probes: set the mode as if that input had just been used.</summary>
        public static void Switch(InputKind kind)
        {
            _mouseTravel = 0;

            if (kind == InputKind.Gamepad)
            {
                PointerUsed();
            }

            if (Current == kind)
            {
                return;
            }

            Current = kind;
            GD.Print($"[GUO] input mode: {kind}" + (kind == InputKind.Gamepad ? $" (\"{PadName}\", {PadFamily})" : ""));
            Changed?.Invoke(kind);
        }
    }
}
