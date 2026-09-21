// SPDX-License-Identifier: MS-PL
//
// Value-for-value with FNA's Microsoft.Xna.Framework.Input.ButtonState
// (FNA, Microsoft Public License -- see docs/upstream/). The member order is
// what matters: ported code casts between this and int, and Released must
// stay 0.

namespace GUO.Compat
{
    /// <summary>
    /// Defines a button state for buttons of mouse, gamepad or joystick.
    /// </summary>
    /// <remarks>
    /// This is in Compat rather than the SDL or input layer because it is what
    /// Compat is for: a bare XNA value type with no behaviour behind it. Two
    /// ported files name it and nothing else from FNA -- `Control.cs` and
    /// `InputEventArgs.cs` -- which between them are the base class for every
    /// gump and the mouse event type the whole UI is written against. Twenty
    /// lines here is what moves them from the rewrite tier to the shim tier.
    /// </remarks>
    public enum ButtonState
    {
        /// <summary>
        /// The button is released.
        /// </summary>
        Released,

        /// <summary>
        /// The button is pressed.
        /// </summary>
        Pressed
    }
}
