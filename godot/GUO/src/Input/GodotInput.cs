// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Network;
using GUO.Platform.Sdl;
using GUO.Renderer;

namespace GUO.Input
{
    /// <summary>
    /// The Godot replacement for upstream's SDL event filter.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream installs
    /// <c>SDL_SetEventFilter(HandleSdlEvent)</c> in GameController.Initialize
    /// and does every bit of its input inside it. Godot owns the window and
    /// delivers input through <c>_Input</c>, so the filter cannot be ported;
    /// this is its replacement and the order of what it does is copied from
    /// it line by line, because that order is the behaviour. In particular:
    ///
    ///   * a key down asks the plugin host first, and a plugin that swallows
    ///     the key also suppresses the text input that would follow it;
    ///   * the scene sees mouse and key events before the UI does, and the UI
    ///     only gets what the scene did not take;
    ///   * a double click is a second press inside MOUSE_DELAY_DOUBLE_CLICK,
    ///     tracked per button, and a handled double click poisons the next
    ///     release with 0xFFFF_FFFF so it does not fire a single click too.
    ///
    /// What is deliberately different, and why.
    ///
    ///   * Text input. SDL raises SDL_EVENT_TEXT_INPUT separately from the
    ///     key press. Godot folds both into one InputEventKey and puts the
    ///     composed character in <c>Unicode</c>, so the text is dispatched
    ///     from the key-down path rather than from an event of its own. The
    ///     guards are upstream's: nothing while a modifier that makes the key
    ///     a command is down, and nothing when a plugin took the key.
    ///   * Mouse position. Upstream polls SDL for it; here it comes off the
    ///     event, which is where Godot puts it. See Mouse.Update.
    ///   * Window enter/leave and focus are notifications, not events, so
    ///     they stay in GameController._Notification where Godot puts them.
    /// </remarks>
    internal static class GodotInput
    {
        /// <summary>
        /// Set when a plugin claimed the last key down. Upstream keeps this on
        /// GameController as <c>_ignoreNextTextInput</c>; it lives here
        /// because here is the only place that reads or writes it.
        /// </summary>
        private static bool _ignoreNextTextInput;

        public static void Handle(InputEvent e)
        {
            switch (e)
            {
                case InputEventKey key:
                    HandleKey(key);
                    break;

                case InputEventMouseMotion motion:
                    HandleMotion(motion);
                    break;

                case InputEventMouseButton button:
                    HandleButton(button);
                    break;
            }
        }

        private static void HandleKey(InputEventKey e)
        {
            SDL.SDL_KeyboardEvent ev = default;

            ev.key = (uint)ToKeycode(e.Keycode != Key.None ? e.Keycode : e.PhysicalKeycode);
            ev.mod = ToKeymod(e);
            ev.down = e.Pressed;
            ev.repeat = e.Echo;

            if (e.Pressed)
            {
                Keyboard.OnKeyDown(ev);

                if (Plugin.ProcessHotkeys((int)ev.key, (int)ev.mod, true))
                {
                    _ignoreNextTextInput = false;

                    UIManager.KeyboardFocusControl?.InvokeKeyDown(
                        (SDL.SDL_Keycode)ev.key,
                        ev.mod
                    );

                    Client.Game.Scene.OnKeyDown(ev);
                }
                else
                {
                    _ignoreNextTextInput = true;
                }

                HandleTextInput(e);

                return;
            }

            Keyboard.OnKeyUp(ev);
            UIManager.KeyboardFocusControl?.InvokeKeyUp((SDL.SDL_Keycode)ev.key, ev.mod);
            Client.Game.Scene.OnKeyUp(ev);
            Plugin.ProcessHotkeys(0, 0, false);

            if ((SDL.SDL_Keycode)ev.key == SDL.SDL_Keycode.SDLK_PRINTSCREEN)
            {
                Client.Game.TakeScreenshot();
            }
        }

        /// <summary>
        /// The SDL_EVENT_TEXT_INPUT arm of upstream's filter, reached from the
        /// key press because that is where Godot puts the character.
        /// </summary>
        private static void HandleTextInput(InputEventKey e)
        {
            if (_ignoreNextTextInput)
            {
                return;
            }

            // Ctrl+C is a command, not the letter c. Alt the same. SDL decides
            // this for upstream by not raising a text event at all.
            if (e.CtrlPressed || e.AltPressed || e.MetaPressed)
            {
                return;
            }

            long unicode = e.Unicode;

            // Control characters are keys, not text: return, tab and backspace
            // all carry one, and upstream's text handler never sees them.
            if (unicode < 0x20 || unicode == 0x7F)
            {
                return;
            }

            string s = char.ConvertFromUtf32((int)unicode);

            UIManager.KeyboardFocusControl?.InvokeTextInput(s);
            Client.Game.Scene.OnTextInput(s);
        }

        private static void HandleMotion(InputEventMouseMotion e)
        {
            GameCursor cursor = Client.Game.UO.GameCursor;

            if (cursor != null && !cursor.AllowDrawSDLCursor)
            {
                cursor.AllowDrawSDLCursor = true;
                cursor.Graphic = 0xFFFF;
            }

            Mouse.Update(e.Position);

            if (Mouse.IsDragging)
            {
                if (!Client.Game.Scene.OnMouseDragging())
                {
                    UIManager.OnMouseDragging();
                }
            }
        }

        private static void HandleButton(InputEventMouseButton e)
        {
            if (e.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                if (!e.Pressed)
                {
                    // A Godot wheel event arrives as a press and a release of
                    // the same button. SDL has one event per notch, so only
                    // the press is forwarded or every scroll would count twice.
                    return;
                }

                Mouse.Update(e.Position);

                bool isScrolledUp = e.ButtonIndex == MouseButton.WheelUp;

                Plugin.ProcessMouse(0, isScrolledUp ? 1 : -1);

                if (!Client.Game.Scene.OnMouseWheel(isScrolledUp))
                {
                    UIManager.OnMouseWheel(isScrolledUp);
                }

                return;
            }

            MouseButtonType buttonType = ToButtonType(e.ButtonIndex);

            if (buttonType == MouseButtonType.None)
            {
                Utility.Logging.Log.Warn($"No mouse button handled: {e.ButtonIndex}");

                return;
            }

            if (e.Pressed)
            {
                ButtonDown(buttonType, e.Position);
            }
            else
            {
                ButtonUp(buttonType, e.Position);
            }
        }

        private static void ButtonDown(MouseButtonType buttonType, Vector2 at)
        {
            uint lastClickTime = LastClickTime(buttonType);

            // The position first: ButtonPress records the click position from
            // Mouse.Position, so the event's position has to be in before it
            // rather than after, as the polled version could get away with.
            Mouse.Update(at);
            Mouse.ButtonPress(buttonType);

            uint ticks = Time.Ticks;

            if (lastClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
            {
                lastClickTime = 0;

                bool res =
                    Client.Game.Scene.OnMouseDoubleClick(buttonType)
                    || UIManager.OnMouseDoubleClick(buttonType);

                if (!res)
                {
                    if (!Client.Game.Scene.OnMouseDown(buttonType))
                    {
                        UIManager.OnMouseButtonDown(buttonType);
                    }
                }
                else
                {
                    lastClickTime = 0xFFFF_FFFF;
                }
            }
            else
            {
                if (buttonType != MouseButtonType.Left && buttonType != MouseButtonType.Right)
                {
                    Plugin.ProcessMouse((int)buttonType, 0);
                }

                if (!Client.Game.Scene.OnMouseDown(buttonType))
                {
                    UIManager.OnMouseButtonDown(buttonType);
                }

                lastClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
            }

            SetLastClickTime(buttonType, lastClickTime);
        }

        private static void ButtonUp(MouseButtonType buttonType, Vector2 at)
        {
            Mouse.Update(at);

            if (LastClickTime(buttonType) != 0xFFFF_FFFF)
            {
                if (
                    !Client.Game.Scene.OnMouseUp(buttonType)
                    || UIManager.LastControlMouseDown(buttonType) != null
                )
                {
                    UIManager.OnMouseButtonUp(buttonType);
                }
            }

            Mouse.ButtonRelease(buttonType);
            Mouse.Refresh();
        }

        private static uint LastClickTime(MouseButtonType type)
        {
            return type switch
            {
                MouseButtonType.Left => Mouse.LastLeftButtonClickTime,
                MouseButtonType.Middle => Mouse.LastMidButtonClickTime,
                MouseButtonType.Right => Mouse.LastRightButtonClickTime,
                _ => 0,
            };
        }

        private static void SetLastClickTime(MouseButtonType type, uint value)
        {
            switch (type)
            {
                case MouseButtonType.Left:
                    Mouse.LastLeftButtonClickTime = value;
                    break;

                case MouseButtonType.Middle:
                    Mouse.LastMidButtonClickTime = value;
                    break;

                case MouseButtonType.Right:
                    Mouse.LastRightButtonClickTime = value;
                    break;
            }
        }

        private static MouseButtonType ToButtonType(MouseButton button)
        {
            return button switch
            {
                MouseButton.Left => MouseButtonType.Left,
                MouseButton.Middle => MouseButtonType.Middle,
                MouseButton.Right => MouseButtonType.Right,
                MouseButton.Xbutton1 => MouseButtonType.XButton1,
                MouseButton.Xbutton2 => MouseButtonType.XButton2,
                _ => MouseButtonType.None,
            };
        }

        private static SDL.SDL_Keymod ToKeymod(InputEventKey e)
        {
            SDL.SDL_Keymod mod = SDL.SDL_Keymod.SDL_KMOD_NONE;

            // Godot reports a modifier as held, not as which side is held.
            // The left flag is the one the ported code tests through the
            // SHIFT/CTRL/ALT masks, which are both sides ORed together.
            if (e.ShiftPressed)
            {
                mod |= SDL.SDL_Keymod.SDL_KMOD_LSHIFT;
            }

            if (e.CtrlPressed)
            {
                mod |= SDL.SDL_Keymod.SDL_KMOD_LCTRL;
            }

            if (e.AltPressed)
            {
                mod |= SDL.SDL_Keymod.SDL_KMOD_LALT;
            }

            if (e.MetaPressed)
            {
                mod |= SDL.SDL_Keymod.SDL_KMOD_LGUI;
            }

            return mod;
        }

        /// <summary>
        /// Godot's key to SDL's keycode.
        /// </summary>
        /// <remarks>
        /// Printable keys need no table. Both enums are ASCII there, with one
        /// difference: Godot names a letter key by its capital (Key.A = 'A'),
        /// SDL by its lowercase (SDLK_A = 'a'), because SDL's keycode is the
        /// character the unmodified key produces. Everything above ASCII is
        /// two unrelated numbering schemes and has to be listed.
        /// </remarks>
        private static SDL.SDL_Keycode ToKeycode(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return SDL.SDL_Keycode.SDLK_A + (uint)(key - Key.A);
            }

            if ((uint)key >= 0x20 && (uint)key <= 0x7E)
            {
                return (SDL.SDL_Keycode)(uint)key;
            }

            return _special.TryGetValue(key, out SDL.SDL_Keycode code)
                ? code
                : SDL.SDL_Keycode.SDLK_UNKNOWN;
        }

        private static readonly Dictionary<Key, SDL.SDL_Keycode> _special = new()
        {
            { Key.Escape, SDL.SDL_Keycode.SDLK_ESCAPE },
            { Key.Tab, SDL.SDL_Keycode.SDLK_TAB },
            { Key.Backtab, SDL.SDL_Keycode.SDLK_TAB },
            { Key.Backspace, SDL.SDL_Keycode.SDLK_BACKSPACE },
            { Key.Enter, SDL.SDL_Keycode.SDLK_RETURN },
            { Key.KpEnter, SDL.SDL_Keycode.SDLK_KP_ENTER },
            { Key.Insert, SDL.SDL_Keycode.SDLK_INSERT },
            { Key.Delete, SDL.SDL_Keycode.SDLK_DELETE },
            { Key.Pause, SDL.SDL_Keycode.SDLK_PAUSE },
            { Key.Print, SDL.SDL_Keycode.SDLK_PRINTSCREEN },
            { Key.Sysreq, SDL.SDL_Keycode.SDLK_SYSREQ },
            { Key.Clear, SDL.SDL_Keycode.SDLK_CLEAR },
            { Key.Home, SDL.SDL_Keycode.SDLK_HOME },
            { Key.End, SDL.SDL_Keycode.SDLK_END },
            { Key.Left, SDL.SDL_Keycode.SDLK_LEFT },
            { Key.Up, SDL.SDL_Keycode.SDLK_UP },
            { Key.Right, SDL.SDL_Keycode.SDLK_RIGHT },
            { Key.Down, SDL.SDL_Keycode.SDLK_DOWN },
            { Key.Pageup, SDL.SDL_Keycode.SDLK_PAGEUP },
            { Key.Pagedown, SDL.SDL_Keycode.SDLK_PAGEDOWN },
            { Key.Shift, SDL.SDL_Keycode.SDLK_LSHIFT },
            { Key.Ctrl, SDL.SDL_Keycode.SDLK_LCTRL },
            { Key.Meta, SDL.SDL_Keycode.SDLK_LGUI },
            { Key.Alt, SDL.SDL_Keycode.SDLK_LALT },
            { Key.Capslock, SDL.SDL_Keycode.SDLK_CAPSLOCK },
            { Key.Numlock, SDL.SDL_Keycode.SDLK_NUMLOCKCLEAR },
            { Key.Scrolllock, SDL.SDL_Keycode.SDLK_SCROLLLOCK },
            { Key.F1, SDL.SDL_Keycode.SDLK_F1 },
            { Key.F2, SDL.SDL_Keycode.SDLK_F2 },
            { Key.F3, SDL.SDL_Keycode.SDLK_F3 },
            { Key.F4, SDL.SDL_Keycode.SDLK_F4 },
            { Key.F5, SDL.SDL_Keycode.SDLK_F5 },
            { Key.F6, SDL.SDL_Keycode.SDLK_F6 },
            { Key.F7, SDL.SDL_Keycode.SDLK_F7 },
            { Key.F8, SDL.SDL_Keycode.SDLK_F8 },
            { Key.F9, SDL.SDL_Keycode.SDLK_F9 },
            { Key.F10, SDL.SDL_Keycode.SDLK_F10 },
            { Key.F11, SDL.SDL_Keycode.SDLK_F11 },
            { Key.F12, SDL.SDL_Keycode.SDLK_F12 },
            { Key.Menu, SDL.SDL_Keycode.SDLK_MENU },
            { Key.Kp0, SDL.SDL_Keycode.SDLK_KP_0 },
            { Key.Kp1, SDL.SDL_Keycode.SDLK_KP_1 },
            { Key.Kp2, SDL.SDL_Keycode.SDLK_KP_2 },
            { Key.Kp3, SDL.SDL_Keycode.SDLK_KP_3 },
            { Key.Kp4, SDL.SDL_Keycode.SDLK_KP_4 },
            { Key.Kp5, SDL.SDL_Keycode.SDLK_KP_5 },
            { Key.Kp6, SDL.SDL_Keycode.SDLK_KP_6 },
            { Key.Kp7, SDL.SDL_Keycode.SDLK_KP_7 },
            { Key.Kp8, SDL.SDL_Keycode.SDLK_KP_8 },
            { Key.Kp9, SDL.SDL_Keycode.SDLK_KP_9 },
            { Key.KpMultiply, SDL.SDL_Keycode.SDLK_KP_MULTIPLY },
            { Key.KpDivide, SDL.SDL_Keycode.SDLK_KP_DIVIDE },
            { Key.KpSubtract, SDL.SDL_Keycode.SDLK_KP_MINUS },
            { Key.KpAdd, SDL.SDL_Keycode.SDLK_KP_PLUS },
            { Key.KpPeriod, SDL.SDL_Keycode.SDLK_KP_PERIOD },
        };
    }
}
