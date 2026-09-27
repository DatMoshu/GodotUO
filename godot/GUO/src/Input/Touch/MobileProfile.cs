// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch
{
    /// <summary>
    /// Where the login screen sits on a display the client cannot shrink to it.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no mobile target; it shrinks the
    /// window to 640x480 for the login screen and grows it again for the
    /// world. A phone's window is the display, so the login gumps are moved
    /// to its centre instead. What a profile starts as on each platform
    /// (world size, zoom) is Configuration/PlatformDefaults, not this class.
    /// Off the touch layer this class does nothing. See ADR-0007, section
    /// "Window and scale".
    /// </remarks>
    internal static class MobileProfile
    {
        /// <summary>The login screen's size, which every login gump is laid out for.</summary>
        private const int LoginWidth = 640;
        private const int LoginHeight = 480;

        /// <summary>Whether the mobile defaults apply on this run.</summary>
        public static bool Active => TouchInput.Enabled;

        /// <summary>
        /// Put a login-screen gump in the middle of the window. The desktop
        /// shrinks its window to 640x480 for the login screen; a phone
        /// cannot, so the gump is moved instead of the window.
        /// </summary>
        public static void CentreLoginGump(Gump gump)
        {
            if (!Active || gump == null || Client.Game == null)
            {
                return;
            }

            Compat.Rectangle bounds = Client.Game.ClientBounds;

            gump.X = System.Math.Max(0, (bounds.Width - LoginWidth) / 2);
            gump.Y = System.Math.Max(0, (bounds.Height - LoginHeight) / 2);
        }
    }
}
