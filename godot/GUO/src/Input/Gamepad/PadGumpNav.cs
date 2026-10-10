// GUO addition, not a port: generic focus-graph over classic gump controls
// for gamepad. One navigator for arbitrary server gumps — not a pad screen
// per layout. See docs/wiki/Controller.md (Planned) and docs/controller_backlog.md G1.

using System;
using System.Collections.Generic;
using GumpControl = GUO.Game.UI.Controls.Control;
using GUO.Compat;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// D-pad / A / B over the topmost navigable classic gump while the pad is
    /// in use. Builds a focus list of mouse-accepting controls (buttons,
    /// checkboxes, text entries, scroll hits), sorted by on-screen position.
    /// A activates the focus (left-click at its centre); B closes the gump;
    /// LB/RB nudge scroll bars. Targeting stays on the radar.
    /// </summary>
    internal static class PadGumpNav
    {
        private static readonly List<GumpControl> _focus = new();
        private static int _index;
        private static Gump _gump;

        public static bool IsActive =>
            InputMode.Current == InputKind.Gamepad
            && !PadScreen.IsOpen
            && !PadWheel.IsOpen
            && !PadRadar.IsOpen
            && !PadWizard.IsOpen
            && !IsTargeting()
            && TopNavigable() != null;

        private static bool IsTargeting()
        {
            var world = Client.Game?.UO?.World;
            return world != null && world.TargetManager != null && world.TargetManager.IsTargeting;
        }

        public static int FocusCount => _focus.Count;

        public static string FocusName =>
            _index >= 0 && _index < _focus.Count && _focus[_index] is GumpControl c && !c.IsDisposed
                ? c.GetType().Name
                : null;

        /// <summary>Let CleanShots still paint the gump we are navigating.</summary>
        public static bool Draws(Gump g) =>
            g != null && !g.IsDisposed && IsActive && ReferenceEquals(g, _gump);

        public static bool HandleButton(Godot.JoyButton button, bool pressed)
        {
            if (!pressed)
            {
                return false;
            }

            // Radar / wheel / pad screen / wizard own the pad; do not steal LB/RB
            // (or A/B/D-pad) while they are up — that blocked R1 use-on-release
            // after a context menu when a classic gump still sat under the radar.
            if (!IsActive)
            {
                return false;
            }

            // Target cursors keep Cancel (B) and confirm — do not steal them.
            if (IsTargeting())
            {
                return false;
            }

            Refresh();

            if (_gump == null)
            {
                return false;
            }

            switch (button)
            {
                case Godot.JoyButton.DpadUp:
                    Move(0, -1);
                    return true;
                case Godot.JoyButton.DpadDown:
                    Move(0, 1);
                    return true;
                case Godot.JoyButton.DpadLeft:
                    Move(-1, 0);
                    return true;
                case Godot.JoyButton.DpadRight:
                    Move(1, 0);
                    return true;
                default:
                    return false;
            }
        }

        // Commands are resolved after layout and PadBindings, including axis bindings.
        public static bool HandleCommand(PadCommand command)
        {
            if (command is not (PadCommand.Use or PadCommand.Cancel or PadCommand.TargetLast or PadCommand.NextHostile)
                || !IsActive)
            {
                return false;
            }

            Refresh();
            if (_gump == null) return false;

            switch (command)
            {
                case PadCommand.Use: Activate(); break;
                case PadCommand.Cancel: TryClose(); break;
                case PadCommand.TargetLast: NudgeScroll(-1); break;
                case PadCommand.NextHostile: NudgeScroll(1); break;
            }

            return true;
        }

        private static Gump TopNavigable()
        {
            for (LinkedListNode<Gump> n = UIManager.Gumps.First; n != null; n = n.Next)
            {
                Gump g = n.Value;

                if (g == null || g.IsDisposed || !g.IsVisible || !g.IsEnabled)
                {
                    continue;
                }

                if (g is WorldViewportGump or TopBarGump or NameOverHeadHandlerGump
                    or NameOverheadGump)
                {
                    continue;
                }

                // Context / popup menus are transient; leave them to Y/radar for now.
                string typeName = g.GetType().Name;
                if (typeName.Contains("Popup", StringComparison.Ordinal) || typeName.Contains("Context", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!HasFocusable(g))
                {
                    continue;
                }

                return g;
            }

            return null;
        }

        private static bool HasFocusable(Gump g)
        {
            foreach (GumpControl c in Walk(g))
            {
                if (CanFocus(c) && !ReferenceEquals(c, g))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<GumpControl> Walk(GumpControl root)
        {
            if (root == null || root.IsDisposed || !root.IsVisible || !root.IsEnabled)
            {
                yield break;
            }

            yield return root;

            foreach (GumpControl child in root.Children)
            {
                if (child.Page != 0 && child.Page != root.ActivePage) continue;

                foreach (GumpControl c in Walk(child))
                {
                    yield return c;
                }
            }
        }

        private static bool CanFocus(GumpControl c)
        {
            if (c == null || c.IsDisposed || !c.IsVisible || !c.IsEnabled || !c.AcceptMouseInput)
            {
                return false;
            }

            return c is GUO.Game.UI.Controls.Button or NiceButton or Checkbox or StbTextBox or HitBox
                or GUO.Game.UI.Controls.ScrollBar or ScrollFlag or HSliderBar or Combobox or ClickableColorBox
                or ItemGump;
        }

        private static void Refresh()
        {
            Gump top = TopNavigable();

            if (top == null)
            {
                _gump = null;
                _focus.Clear();
                _index = 0;
                return;
            }

            // Page, nested visibility/enabled state and positions can change
            // without changing the top-level child count. Rebuild from the same
            // rendered tree for each navigation event, preserving the focused control.
            GumpControl previous = ReferenceEquals(top, _gump) && _index >= 0 && _index < _focus.Count
                ? _focus[_index] : null;
            _gump = top;
            _focus.Clear();

            foreach (GumpControl c in Walk(top))
            {
                if (CanFocus(c) && !ReferenceEquals(c, top))
                {
                    _focus.Add(c);
                }
            }

            _focus.Sort((a, b) =>
            {
                int cmp = a.ScreenCoordinateY.CompareTo(b.ScreenCoordinateY);
                return cmp != 0 ? cmp : a.ScreenCoordinateX.CompareTo(b.ScreenCoordinateX);
            });
            int retained = previous == null ? -1 : _focus.IndexOf(previous);
            _index = retained >= 0 ? retained : 0;
        }

        private static void Move(int dx, int dy)
        {
            if (_focus.Count == 0)
            {
                return;
            }

            GumpControl cur = _focus[_index];
            int cx = cur.ScreenCoordinateX + cur.Width / 2;
            int cy = cur.ScreenCoordinateY + cur.Height / 2;
            int best = _index;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _focus.Count; i++)
            {
                if (i == _index)
                {
                    continue;
                }

                GumpControl o = _focus[i];
                int ox = o.ScreenCoordinateX + o.Width / 2;
                int oy = o.ScreenCoordinateY + o.Height / 2;
                int vx = ox - cx;
                int vy = oy - cy;

                if (dx != 0 && Math.Sign(vx) != dx)
                {
                    continue;
                }

                if (dy != 0 && Math.Sign(vy) != dy)
                {
                    continue;
                }

                float along = dx != 0 ? Math.Abs(vx) : Math.Abs(vy);
                float cross = dx != 0 ? Math.Abs(vy) : Math.Abs(vx);
                float score = along + cross * 2.5f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best == _index)
            {
                _index = dy > 0 || dx > 0
                    ? (_index + 1) % _focus.Count
                    : (_index - 1 + _focus.Count) % _focus.Count;
            }
            else
            {
                _index = best;
            }
        }

        private static void Activate()
        {
            if (_index < 0 || _index >= _focus.Count)
            {
                return;
            }

            GumpControl c = _focus[_index];

            if (c == null || c.IsDisposed)
            {
                return;
            }

            int x = c.ScreenCoordinateX + Math.Max(1, c.Width / 2);
            int y = c.ScreenCoordinateY + Math.Max(1, c.Height / 2);
            Mouse.Position = new Point(x, y);
            float scale = (float)Client.Game.DpiScale;
            Godot.Vector2 at = new Godot.Vector2(x, y) * scale;
            GodotInput.Handle(new Godot.InputEventMouseButton { ButtonIndex = Godot.MouseButton.Left, Pressed = true, Position = at });
            GodotInput.Handle(new Godot.InputEventMouseButton { ButtonIndex = Godot.MouseButton.Left, Pressed = false, Position = at });
        }

        private static void TryClose()
        {
            if (_gump == null || _gump.IsDisposed)
            {
                return;
            }

            _gump.InvokeMouseCloseGumpWithRClick();
            Refresh();
        }

        private static void NudgeScroll(int dir)
        {
            if (_gump == null)
            {
                return;
            }

            foreach (GumpControl c in Walk(_gump))
            {
                if (c is ScrollBarBase bar && !bar.IsDisposed)
                {
                    bar.Value = Math.Clamp(bar.Value + dir * Math.Max(1, bar.ScrollStep), bar.MinValue, bar.MaxValue);
                    return;
                }
            }
        }
    }
}
