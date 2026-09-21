// SPDX-License-Identifier: BSD-2-Clause

using GUO.Compat;
using Godot;

namespace GUO.Input
{
    internal static class Mouse
    {
        public const int MOUSE_DELAY_DOUBLE_CLICK = 350;

        /* Log a button press event at the given time. */
        public static void ButtonPress(MouseButtonType type)
        {
            CancelDoubleClick = false;

            switch (type)
            {
                case MouseButtonType.Left:
                    LButtonPressed = true;
                    LClickPosition = Position;

                    break;

                case MouseButtonType.Middle:
                    MButtonPressed = true;
                    MClickPosition = Position;

                    break;

                case MouseButtonType.Right:
                    RButtonPressed = true;
                    RClickPosition = Position;

                    break;

                case MouseButtonType.XButton1:
                case MouseButtonType.XButton2:
                    XButtonPressed = true;

                    break;
            }

        }

        /* Log a button release event at the given time */
        public static void ButtonRelease(MouseButtonType type)
        {
            switch (type)
            {
                case MouseButtonType.Left:
                    LButtonPressed = false;

                    break;

                case MouseButtonType.Middle:
                    MButtonPressed = false;

                    break;

                case MouseButtonType.Right:
                    RButtonPressed = false;

                    break;

                case MouseButtonType.XButton1:
                case MouseButtonType.XButton2:
                    XButtonPressed = false;

                    break;
            }

        }

        public static Point Position;

        public static Point LClickPosition;

        public static Point RClickPosition;

        public static Point MClickPosition;

        public static uint LastLeftButtonClickTime { get; set; }

        public static uint LastMidButtonClickTime { get; set; }

        public static uint LastRightButtonClickTime { get; set; }

        public static bool CancelDoubleClick { get; set; }

        public static bool LButtonPressed { get; set; }

        public static bool RButtonPressed { get; set; }

        public static bool MButtonPressed { get; set; }

        public static bool XButtonPressed { get; set; }

        public static bool IsDragging { get; set; }

        public static Point LDragOffset => LButtonPressed ? Position - LClickPosition : Point.Zero;

        public static Point RDragOffset => RButtonPressed ? Position - RClickPosition : Point.Zero;

        public static Point MDragOffset => MButtonPressed ? Position - MClickPosition : Point.Zero;

        public static bool MouseInWindow { get; set; }

        /// <summary>
        /// Take the pointer position from a mouse event.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream's SDL_GetMouseState is a poll, and
        /// the port polled with it -- DisplayServer.MouseGetPosition minus the
        /// window origin. Godot hands every mouse event the position it
        /// happened at, and that is the position the event should be handled
        /// at: a poll answers "where is the pointer now", which is a different
        /// question and a different answer once anything moves the pointer
        /// between an event being queued and being handled. Synthesised input
        /// made that visible -- a press and its release could be read at two
        /// different places, so the press hit a button and the release did
        /// not, and the button never fired.
        /// </remarks>
        public static void Update(Vector2 position)
        {
            // Scale the mouse coordinates for the DPI setting.
            //
            // PORT DEVIATION (GUO): upstream also scales by
            // PreferredBackBufferWidth / ClientBounds.Width, FNA's "faux
            // backbuffer" -- a render surface that can differ in size from the
            // window. Godot's window and its default surface are the same
            // size, so that ratio is 1 and the term is gone.
            Position.X = (int) ((double) position.X / Client.Game.DpiScale);

            Position.Y = (int) ((double) position.Y / Client.Game.DpiScale);

            Refresh();
        }

        /// <summary>
        /// Per-frame state that does not depend on an event. The position is
        /// not touched: it is whatever the last event said, and nothing moves
        /// the pointer without an event.
        /// </summary>
        public static void Refresh()
        {
            IsDragging = LButtonPressed || RButtonPressed || MButtonPressed;
        }
    }
}