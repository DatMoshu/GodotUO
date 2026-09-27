// GUO addition, not a port: upstream ClassicUO (at the reviewed pin) has no
// gamepad support at all.

using System;
using Godot;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// Gamepad events, in front of GodotInput the way the touch layer is.
    /// Every joypad event is consumed here; nothing else in the client reads
    /// them.
    /// </summary>
    internal static class GamepadInput
    {
        /// <summary>Log every joypad event to the console (and logcat): --gamepad-trace.</summary>
        public static bool Trace { get; set; }

        private static bool _connectHooked;

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

                    return true;

                case InputEventJoypadMotion motion:
                    if (Trace && Math.Abs(motion.AxisValue) > 0.5f)
                    {
                        GD.Print($"[GUO] gamepad: device {motion.Device} axis {(int) motion.Axis} ({motion.Axis}) {motion.AxisValue:0.00}");
                    }

                    return true;
            }

            return false;
        }

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
            Describe((int) device, connected ? "connected" : "disconnected");
        }

        private static void Describe(int device, string what)
        {
            GD.Print($"[GUO] gamepad: device {device} {what}: \"{Godot.Input.GetJoyName(device)}\" guid {Godot.Input.GetJoyGuid(device)} known {Godot.Input.IsJoyKnown(device)} info {Godot.Input.GetJoyInfo(device)}");
        }
    }
}
