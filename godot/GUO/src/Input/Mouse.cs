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

        public static void Update()
        {
            // PORT DEVIATION (GUO): upstream asks SDL for the pointer twice
            // over -- SDL_GetMouseState is window-relative and only right
            // while the window has the pointer, so when it does not it asks
            // for the desktop position and subtracts the window's. Godot
            // reports the desktop position either way, so one subtraction
            // answers both cases and the branch goes.
            Vector2I mouse = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();

            Position.X = mouse.X;
            Position.Y = mouse.Y;

            // Scale the mouse coordinates for the DPI setting.
            //
            // PORT DEVIATION (GUO): upstream also scales by
            // PreferredBackBufferWidth / ClientBounds.Width, FNA's "faux
            // backbuffer" -- a render surface that can differ in size from the
            // window. Godot's window and its default surface are the same
            // size, so that ratio is 1 and the term is gone.
            Position.X = (int) ((double) Position.X / Client.Game.DpiScale);

            Position.Y = (int) ((double) Position.Y / Client.Game.DpiScale);

            IsDragging = LButtonPressed || RButtonPressed || MButtonPressed;
        }
    }
}