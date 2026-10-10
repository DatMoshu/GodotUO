// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad.

using System;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Input.Touch;
using GUO.Pregame3D;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// The menu wheel (the owner's design, 2026-10-03): hold its input (LT by
    /// default) and eight windows ring the screen in the client's art while
    /// the game goes on; either stick points at one (the middle is none);
    /// letting go opens the one pointed at. A quick tap (under 0.2 s, no stick)
    /// opens again the last window the wheel opened.
    /// </summary>
    /// <remarks>
    /// Each slice opens what the client already does for that window: the top
    /// bar's and the macros' own calls (GameActions.Open*, the "Open" macro),
    /// so a shard sees the same requests a mouse would make. Which window sits
    /// in which slice is <see cref="PadBindings.Wheel"/>.
    /// </remarks>
    internal static class PadWheel
    {
        public const double TapSeconds = 0.2;
        private const float PointAt = 0.5f, Centre = 0.3f;
        private const float Radius = 112f;            // art pixels, centre to a slice's middle
        private static readonly Vector2 SliceSize = new(64, 40); // icon only; the name is the hub

        private static ulong _openedAt;
        private static bool _moved;
        private static int _focus = -1;
        private static WheelWindow? _last;

        private static Control _root;
        private static readonly PanelContainer[] _slices = new PanelContainer[8];
        private static readonly Label[] _labels = new Label[8];
        private static readonly TextureRect[] _icons = new TextureRect[8];
        private static Label _hub, _hubHint;
        private static PanelContainer _hubCard;
        private static bool _built;

        public static bool IsOpen { get; private set; }

        /// <summary>The slice pointed at, 0 (top) clockwise to 7, or -1.</summary>
        public static int Focus => _focus;

        /// <summary>The window the wheel last opened, for the tap.</summary>
        public static WheelWindow? Last => _last;

        /// <summary>For probes: what the last release did ("opened Backpack", "reopened Paperdoll", "nothing").</summary>
        public static string LastResult { get; private set; } = "";

        public static void Begin()
        {
            if (IsOpen)
            {
                return;
            }

            IsOpen = true;
            _openedAt = Godot.Time.GetTicksMsec();
            _moved = false;
            _focus = -1;
            Show();
        }

        /// <summary>Once a frame while open: the stick further from the middle points.</summary>
        public static void Steer(float lx, float ly, float rx, float ry)
        {
            if (!IsOpen)
            {
                return;
            }

            Vector2 l = new(lx, ly), r = new(rx, ry);
            Vector2 v = r.Length() > l.Length() ? r : l;

            if (v.Length() >= PointAt)
            {
                _moved = true;
                // 0 at the top, clockwise: screen y grows downward.
                float deg = Mathf.RadToDeg(Mathf.Atan2(v.X, -v.Y));
                int slice = (int) Math.Round(((deg % 360f) + 360f) % 360f / 45f) % 8;
                SetFocus(slice);
            }
            else if (v.Length() < Centre)
            {
                SetFocus(-1);
            }
        }

        /// <summary>The wheel's input let go: open what is pointed at, or the last on a tap.</summary>
        public static void End()
        {
            if (!IsOpen)
            {
                return;
            }

            double held = (Godot.Time.GetTicksMsec() - _openedAt) / 1000.0;
            int focus = _focus;
            Close();

            if (focus >= 0)
            {
                WheelWindow w = PadBindings.Wheel[focus];
                _last = w;
                LastResult = "opened " + w;
                Open(w);
            }
            else if (!_moved && held < TapSeconds && _last is WheelWindow again)
            {
                LastResult = "reopened " + again;
                Open(again);
            }
            else
            {
                LastResult = "nothing";
            }

            GD.Print($"[GUO] pad wheel: {LastResult} (held {held:0.00} s)");
        }

        /// <summary>Shut without opening anything (Cancel while it is up).</summary>
        public static void Close()
        {
            IsOpen = false;
            _focus = -1;

            if (_root != null && GodotObject.IsInstanceValid(_root))
            {
                _root.Visible = false;
            }
        }

        // --- the windows ------------------------------------------------------------

        public static void Open(WheelWindow w)
        {
            World world = Client.Game?.UO?.World;

            if (world == null || !world.InGame || world.Player == null)
            {
                return;
            }

            // The screen is the window. The pack, the paperdoll, skills and the
            // spellbook also ask the shard, because their contents are not all
            // local; the screen does not wait for the reply.
            PadScreen.Open(w);
        }

        /// <summary>
        /// The macro row: the touch bar's second row where there is a bar,
        /// else the client's own macro editor (Options, Macros).
        /// </summary>
        public static void MacroRow(World world)
        {
            TouchGumpBar bar = TouchInput.Bar;

            if (bar != null && bar.HandleShown)
            {
                bar.ToggleRow();
            }
            else
            {
                GameActions.OpenSettings(world, 4);
            }
        }

        /// <summary>One step of the client's own macro engine, as a macro button runs it.</summary>
        public static void RunMacro(World world, MacroType type, MacroSubType sub = MacroSubType.MSC_NONE)
        {
            world.Macros.SetMacroToExecute(new MacroObject(type, sub));
        }

        // --- the view -----------------------------------------------------------------

        private static void SetFocus(int slice)
        {
            if (slice == _focus)
            {
                return;
            }

            _focus = slice;
            Restyle();
        }

        private static void Show()
        {
            PadOverlay layer = PadOverlay.Get();

            if (layer == null || !PadArt.Warm())
            {
                // Not drawn until the art is in; the wheel still works.
                return;
            }

            if (!_built || _root == null || !GodotObject.IsInstanceValid(_root))
            {
                Build(layer);
            }

            for (int i = 0; i < 8; i++)
            {
                _icons[i].Texture = PadArt.Art(PadArt.Icon(PadBindings.Wheel[i]));
            }

            _root.Visible = true;
            Restyle();
        }

        private static void Build(PadOverlay layer)
        {
            _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadWheel" };
            layer.Ui.AddChild(_root);

            for (int i = 0; i < 8; i++)
            {
                var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = SliceSize, Size = SliceSize };
                // Icon only. The name is the hub, and only while this slice is pointed at.
                _icons[i] = PadArt.Pic(null);
                _icons[i].ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                _icons[i].StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                _icons[i].CustomMinimumSize = Vector2.Zero;
                _icons[i].SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                card.AddChild(_icons[i]);
                _labels[i] = Overlay.Text("", UoTheme.Ink);
                _labels[i].Visible = false;
                card.AddChild(_labels[i]);
                _root.AddChild(card);
                _slices[i] = card;
            }

            _hubCard = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(96, 0) };
            _hubCard.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 8));
            VBoxContainer hubCol = Overlay.Column(0);
            _hub = Overlay.Text("", UoTheme.Heading);
            _hub.HorizontalAlignment = HorizontalAlignment.Center;
            _hubHint = Overlay.Text("", UoTheme.Muted);
            _hubHint.HorizontalAlignment = HorizontalAlignment.Center;
            hubCol.AddChild(_hub);
            hubCol.AddChild(_hubHint);
            _hubCard.AddChild(hubCol);
            _root.AddChild(_hubCard);
            _built = true;
        }

        private static void Restyle()
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root) || !_root.Visible)
            {
                return;
            }

            // Art pixels are scaled by UiScale on the way to the window.
            // Divide by the debug scale (and keep the automatic one) so LB+R3
            // does not resize the ring. The bag's grid is what that chord changes.
            Rect2 world = PadOverlay.WorldRect, client = PadOverlay.ClientRect;
            float debug = Math.Max(0.25f, PadOverlay.UiScale);
            float keep = Math.Max(1f, UoTheme.PixelScale) / debug;
            float radius = Radius * keep;
            Vector2 slice = SliceSize * keep;
            float reach = radius + slice.Y / 2f + 6f * keep;
            float across = radius + slice.X / 2f + 6f * keep;
            Vector2 centre = world.GetCenter();
            centre = new Vector2(
                Math.Clamp(centre.X, client.Position.X + across, Math.Max(client.Position.X + across, client.End.X - across)),
                Math.Clamp(centre.Y, client.Position.Y + reach, Math.Max(client.Position.Y + reach, client.End.Y - reach)));

            for (int i = 0; i < 8; i++)
            {
                bool lit = i == _focus;
                float a = Mathf.DegToRad(i * 45f);
                float r = radius + (lit ? 8f * keep : 0f);
                Vector2 at = centre + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * r - slice / 2f - (lit ? new Vector2(0, 2f * keep) : Vector2.Zero);
                PanelContainer card = _slices[i];
                card.CustomMinimumSize = slice;
                card.Position = at.Round();
                card.Size = slice;
                card.AddThemeStyleboxOverride("panel", lit ? Overlay.Frame(Overlay.Parchment, 6, 1) : Overlay.Frame(Overlay.Stone, 6));
                _labels[i].Visible = false;
                _icons[i].Modulate = lit ? Colors.White : new Color(0.82f, 0.82f, 0.82f);
                card.ZIndex = lit ? 1 : 0;
            }

            _hub.Text = _focus >= 0 ? PadBindings.Name(PadBindings.Wheel[_focus]) : "Point at a window";
            _hubHint.Text = _focus >= 0 ? "Let go to open" : (_last is WheelWindow w ? $"Tap: {PadBindings.Name(w)}" : "Middle: nothing");
            _hubCard.Scale = Vector2.One;
            _hubCard.ResetSize();
            Vector2 hubSize = _hubCard.GetCombinedMinimumSize();
            _hubCard.Scale = new Vector2(keep, keep);
            _hubCard.Position = (centre - hubSize * keep / 2f).Round();
        }
    }
}
