// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad. The behaviour is
// Ghostroads' "Set controls" (game/ui/set_controls.gd, game/input/bindings.gd,
// the owner's own project), ported as behaviour, not as code.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Input.Touch;
using GUO.Pregame3D;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// "Set controls": one job at a time, in <see cref="PadBindings.Order"/>,
    /// its name big and what it does under it; the player presses and holds
    /// what they want for it. The meter fills while exactly one input is held
    /// (<see cref="Hold"/> seconds), then that input is taken and the next job
    /// comes up. Holding three or more at once cancels the whole thing with
    /// nothing changed. After the twelve jobs, the menu wheel's eight slices
    /// are picked from a list (D-pad and A). Everything chosen is applied
    /// together at the end (<see cref="PadBindings.Apply"/>), which also clears
    /// an input from any other job.
    /// </summary>
    /// <remarks>
    /// Reached from Options (Controller buttons, "Set controls...") and offered
    /// once, after the first login on a pad (<see cref="MaybeOffer"/>), where B
    /// skips it. Axes count as inputs, each half on its own, on at
    /// <see cref="PadBindings.AxisOn"/> and off again under <see cref="PadBindings.AxisOff"/>.
    /// While it is up it takes every pad event (GamepadInput hands them here).
    /// </remarks>
    internal static class PadWizard
    {
        public const double Hold = 0.7;
        public const int CancelCount = 3;

        private enum Phase { Closed, Offer, Bind, Slots, Done }

        private static Phase _phase = Phase.Closed;
        private static int _step;
        private static readonly Dictionary<PadCommand, PadInput> _chosen = new();
        private static WheelWindow[] _slots;
        private static int _slot, _pick;
        private static readonly Dictionary<string, (PadInput input, bool stale)> _held = new();
        private static double _progress;
        private static double _doneFor;

        /// <summary>Set by Main: a scripted run (a probe) gets no offer unless it asks for one.</summary>
        public static bool OfferAllowed { get; set; } = true;

        public static bool IsOpen => _phase != Phase.Closed;

        public static bool Offering => _phase == Phase.Offer;

        /// <summary>For probes: "applied", "cancelled", "skipped", or "" while none has ended.</summary>
        public static string LastResult { get; private set; } = "";

        /// <summary>For probes: the words on the card, one row a line. No descriptions.</summary>
        public static string VisibleText { get; private set; } = "";

        /// <summary>For probes: button glyphs drawn on the card.</summary>
        public static int GlyphCount { get; private set; }

        /// <summary>For probes: which job is being asked for (Bind), or null.</summary>
        public static PadCommand? Asking => _phase == Phase.Bind ? PadBindings.Order[_step] : null;

        /// <summary>For probes: which slice is being chosen (Slots), or -1.</summary>
        public static int SlotAsking => _phase == Phase.Slots ? _slot : -1;

        public static double Progress => _progress;

        /// <summary>Raised when it closes, with the inputs still held then (GamepadInput lets their releases pass by).</summary>
        public static event Action<IEnumerable<string>> Closed;

        // --- opening ------------------------------------------------------------------------

        /// <summary>Once a frame: the first time a pad is used in the world, offer it.</summary>
        public static void MaybeOffer()
        {
            if (!OfferAllowed || IsOpen || PadBindings.WizardOffered || InputMode.Current != InputKind.Gamepad)
            {
                return;
            }

            var world = Client.Game?.UO?.World;

            if (world == null || !world.InGame || world.Player == null || Client.Game.Scene is not Game.Scenes.GameScene
                || Game.Managers.UIManager.IsModalOpen || !PadArt.Warm())
            {
                return;
            }

            PadBindings.WizardOffered = true;
            _phase = Phase.Offer;
            _held.Clear();
            View.Show();
            GD.Print("[GUO] pad wizard: offered (first login on a pad)");
        }

        /// <summary>Start "Set controls" (from Options, or A on the offer).</summary>
        public static void Open()
        {
            PadWheel.Close();
            PadRadar.Close();
            PadScreen.Close();
            _phase = Phase.Bind;
            _step = 0;
            _chosen.Clear();
            _slots = new WheelWindow[8];

            for (int i = 0; i < 8; i++)
            {
                _slots[i] = PadBindings.Wheel[i];
            }

            _progress = 0;
            StaleAll();
            LastResult = "";
            View.Show();
            GD.Print("[GUO] pad wizard: open");
        }

        /// <summary>Three at once, or closing the client's way: every binding stays as it was.</summary>
        public static void Cancel()
        {
            if (_phase is Phase.Closed or Phase.Done)
            {
                return;
            }

            _chosen.Clear();
            LastResult = "cancelled";
            GD.Print("[GUO] pad wizard: cancelled, nothing changed");
            Finish("Cancelled", "");
        }

        private static void Finish(string big, string small)
        {
            _phase = Phase.Done;
            _doneFor = 0.8;
            View.Done(big, small);
        }

        private static void Close()
        {
            var still = new List<string>(_held.Keys);
            _phase = Phase.Closed;
            _held.Clear();
            View.Hide();
            Closed?.Invoke(still);
        }

        // --- input ---------------------------------------------------------------------------

        /// <summary>Every pad event while it is up; true (taken) while it is up.</summary>
        public static bool Handle(InputEvent e)
        {
            if (!IsOpen)
            {
                return false;
            }

            switch (e)
            {
                case InputEventJoypadButton b:
                    JoyButton printed = GamepadInput.PrintedOrRaw(b.ButtonIndex, b.Device);
                    var input = PadInput.Button(printed);

                    if (b.Pressed)
                    {
                        Press(input);
                    }
                    else
                    {
                        _held.Remove(input.Id);
                    }

                    break;

                case InputEventJoypadMotion m:
                    var plus = PadInput.Axis(m.Axis, 1);
                    var minus = PadInput.Axis(m.Axis, -1);

                    if (m.AxisValue >= PadBindings.AxisOn)
                    {
                        if (!_held.ContainsKey(plus.Id))
                        {
                            Press(plus);
                        }
                    }
                    else if (m.AxisValue <= -PadBindings.AxisOn)
                    {
                        if (!_held.ContainsKey(minus.Id))
                        {
                            Press(minus);
                        }
                    }

                    if (m.AxisValue < PadBindings.AxisOff)
                    {
                        _held.Remove(plus.Id);
                    }

                    if (m.AxisValue > -PadBindings.AxisOff)
                    {
                        _held.Remove(minus.Id);
                    }

                    break;

                default:
                    return false;
            }

            return true;
        }

        private static void Press(PadInput input)
        {
            if (!_held.ContainsKey(input.Id))
            {
                _held[input.Id] = (input, false);
            }

            switch (_phase)
            {
                case Phase.Offer:
                    if (input == PadInput.Button(JoyButton.A))
                    {
                        Open();
                    }
                    else if (input == PadInput.Button(JoyButton.B))
                    {
                        LastResult = "skipped";
                        GD.Print("[GUO] pad wizard: skipped (B on the offer)");
                        Close();
                    }

                    break;

                case Phase.Slots:
                    // The list is a menu: the D-pad moves, the printed A picks.
                    if (input.IsAxis)
                    {
                        break;
                    }

                    int count = Choices.Length;

                    switch ((JoyButton) input.Index)
                    {
                        case JoyButton.DpadUp: _pick = (_pick - 1 + count) % count; View.Slots(); break;
                        case JoyButton.DpadDown: _pick = (_pick + 1) % count; View.Slots(); break;
                        case JoyButton.DpadLeft: _pick = (_pick - Rows + count) % count; View.Slots(); break;
                        case JoyButton.DpadRight: _pick = (_pick + Rows) % count; View.Slots(); break;
                        case JoyButton.A: TakeSlot(); break;
                    }

                    break;
            }
        }

        private static void StaleAll()
        {
            foreach (string id in new List<string>(_held.Keys))
            {
                _held[id] = (_held[id].input, true);
            }
        }

        // --- the clock -----------------------------------------------------------------------

        /// <summary>Once a frame (GamepadInput.Update).</summary>
        public static void Update(double delta)
        {
            switch (_phase)
            {
                case Phase.Closed:
                    return;

                case Phase.Done:
                    _doneFor -= delta;

                    if (_doneFor <= 0)
                    {
                        Close();
                    }

                    return;

                case Phase.Offer:
                    return;
            }

            int fresh = 0;
            PadInput only = default;

            foreach ((PadInput input, bool stale) in _held.Values)
            {
                if (!stale)
                {
                    fresh++;
                    only = input;
                }
            }

            if (fresh >= CancelCount)
            {
                Cancel();
                return;
            }

            if (_phase != Phase.Bind)
            {
                return;
            }

            if (fresh == 1)
            {
                _progress += delta;

                if (_progress >= Hold)
                {
                    Take(only);
                    return;
                }
            }
            else
            {
                _progress = Math.Max(_progress - delta * 3.0, 0.0);
            }

            View.Meter(_progress / Hold, fresh == 1 ? only.Label : "");
        }

        private static void Take(PadInput input)
        {
            PadCommand c = PadBindings.Order[_step];
            _chosen[c] = input;
            GD.Print($"[GUO] pad wizard: {PadBindings.Name(c)} = {input.Label}");
            _step++;
            _progress = 0;
            // Whatever is still down must come up before it counts again.
            StaleAll();

            if (_step >= PadBindings.Order.Length)
            {
                _phase = Phase.Slots;
                _slot = 0;
                _pick = Array.IndexOf(Choices, _slots[0]);
                View.Slots();
            }
            else
            {
                View.Step();
            }
        }

        // --- the wheel's slices ----------------------------------------------------------------

        public static readonly WheelWindow[] Choices = Enum.GetValues<WheelWindow>();
        public const int Rows = 7;

        public static readonly string[] SliceNames = { "top", "top right", "right", "bottom right", "bottom", "bottom left", "left", "top left" };

        private static void TakeSlot()
        {
            _slots[_slot] = Choices[Math.Clamp(_pick, 0, Choices.Length - 1)];
            GD.Print($"[GUO] pad wizard: wheel {SliceNames[_slot]} = {_slots[_slot]}");
            _slot++;

            if (_slot >= 8)
            {
                PadBindings.Apply(_chosen, _slots);
                LastResult = "applied";
                GD.Print("[GUO] pad wizard: applied");
                Finish("Saved", "");
                return;
            }

            _pick = Array.IndexOf(Choices, _slots[_slot]);
            View.Slots();
        }

        // --- the view ------------------------------------------------------------------------------

        private static class View
        {
            private static Control _root;
            private static ColorRect _dim;
            private static PanelContainer _card;
            private static VBoxContainer _col;

            private static void Ensure()
            {
                PadOverlay layer = PadOverlay.Get();

                if (_root != null && GodotObject.IsInstanceValid(_root))
                {
                    return;
                }

                _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadWizard" };
                layer.Ui.AddChild(_root);
                _dim = new ColorRect { Color = Overlay.Band, MouseFilter = Control.MouseFilterEnum.Ignore };
                _root.AddChild(_dim);
                _card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(320, 0) };
                _card.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 12));
                _root.AddChild(_card);
                _col = Overlay.Column(4);
                _card.AddChild(_col);
            }

            public static void Show()
            {
                if (!PadArt.Warm())
                {
                    return;
                }

                Ensure();
                _root.Visible = true;

                switch (_phase)
                {
                    case Phase.Offer: Offer(); break;
                    case Phase.Bind: Step(); break;
                }
            }

            public static void Hide()
            {
                if (_root != null && GodotObject.IsInstanceValid(_root))
                {
                    _root.Visible = false;
                }
            }

            private static void Clear()
            {
                foreach (Node n in _col.GetChildren())
                {
                    _col.RemoveChild(n);
                    n.QueueFree();
                }

                _meter = null;
                _holding = null;
            }

            private static Label Centred(string text, Color color, int scale = 1, bool wrap = false)
            {
                Label l = Overlay.Text(text, color, scale, wrap);
                l.HorizontalAlignment = HorizontalAlignment.Center;

                if (wrap)
                {
                    l.CustomMinimumSize = new Vector2(296, 0);
                }

                _col.AddChild(l);
                return l;
            }

            private static void Prompt((PadAction action, string words)[] items)
            {
                HBoxContainer row = Overlay.Row(14);
                row.Alignment = BoxContainer.AlignmentMode.Center;

                foreach ((PadAction action, string words) in items)
                {
                    HBoxContainer pair = Overlay.Row(3);
                    // The wizard's own A and B are the printed ones, whatever is bound.
                    pair.AddChild(PadArt.Cap(PadInput.Button(action == PadAction.Confirm ? JoyButton.A : JoyButton.B)));
                    pair.AddChild(Overlay.Text(words, UoTheme.Ink));
                    row.AddChild(pair);
                }

                _col.AddChild(row);
            }

            private static void Offer()
            {
                Clear();
                Centred("Set controls", UoTheme.Heading, 2);
                Prompt(new[] { (PadAction.Confirm, "Set controls"), (PadAction.Cancel, "Not now") });
                VisibleText = "Set controls\nSet controls\nNot now";
                GlyphCount = CountGlyphs(_col);
                Layout();
            }

            private static ProgressBar _meter;

            /// <summary>A status bar line as a box: its rounded ends kept, its middle tiled.</summary>
            private static StyleBox Line(ushort gump)
            {
                Texture2D t = PadArt.Gump(gump);

                if (t == null)
                {
                    return new StyleBoxEmpty();
                }

                return new StyleBoxTexture
                {
                    Texture = t, TextureMarginLeft = 4, TextureMarginRight = 4, TextureMarginTop = 0, TextureMarginBottom = 0,
                    AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                };
            }
            private static Label _holding;
            private static string _holdingWas = "";
            private const float MeterWidth = 240f;

            public static void Step()
            {
                if (_root == null || !GodotObject.IsInstanceValid(_root))
                {
                    Show();
                    return;
                }

                Clear();
                var words = new System.Text.StringBuilder();

                for (int i = 0; i < PadBindings.Order.Length; i++)
                {
                    PadCommand c = PadBindings.Order[i];
                    bool lit = i == _step;
                    PadInput? shown = _chosen.TryGetValue(c, out PadInput picked) ? picked : PadBindings.For(c);
                    string set = shown is PadInput s ? s.Label : "nothing";
                    words.Append(PadBindings.Name(c)).Append('\t').Append(set).Append('\n');

                    var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(300, 0) };
                    row.AddThemeStyleboxOverride("panel", lit
                        ? Overlay.Frame(Overlay.Parchment, 3, 1)
                        : new StyleBoxEmpty { ContentMarginLeft = 3, ContentMarginRight = 3, ContentMarginTop = 1, ContentMarginBottom = 1 });
                    HBoxContainer line = Overlay.Row(6);
                    Label name = Overlay.Text(PadBindings.Name(c), lit ? UoTheme.Danger : UoTheme.Ink);
                    name.CustomMinimumSize = new Vector2(120, 0);
                    name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    line.AddChild(name);
                    // Cap shows the binding (glyph or plate). A second static
                    // "LB" label made "LB LB"; keep an empty label only so the
                    // hold meter can preview the input being bound.
                    line.AddChild(PadArt.Cap(shown));
                    Label current = Overlay.Text("", UoTheme.Ink);
                    current.CustomMinimumSize = new Vector2(72, 0);
                    line.AddChild(current);
                    row.AddChild(line);
                    _col.AddChild(row);

                    if (lit)
                    {
                        _holding = current;
                        _holdingWas = set;
                    }
                }

                // The meter: the status bar's own track and fill lines, their ends kept.
                _meter = new ProgressBar
                {
                    ShowPercentage = false, MinValue = 0, MaxValue = 1, Step = 0, Value = _progress / Hold,
                    CustomMinimumSize = new Vector2(MeterWidth, 11), MouseFilter = Control.MouseFilterEnum.Ignore,
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                };
                _meter.AddThemeStyleboxOverride("background", Line(PadArt.LineTrack));
                _meter.AddThemeStyleboxOverride("fill", Line(PadArt.LineFill));
                _col.AddChild(_meter);
                VisibleText = words.ToString().TrimEnd();
                GlyphCount = CountGlyphs(_col);
                Layout();
            }

            private static int CountGlyphs(Node node)
            {
                int n = node is TextureRect ? 1 : 0;

                foreach (Node child in node.GetChildren())
                {
                    n += CountGlyphs(child);
                }

                return n;
            }

            public static void Meter(double fraction, string holding)
            {
                if (_meter == null || !GodotObject.IsInstanceValid(_meter))
                {
                    return;
                }

                _meter.Value = Math.Clamp(fraction, 0, 1);

                if (_holding != null && GodotObject.IsInstanceValid(_holding))
                {
                    // Cap already shows the binding (glyph or plate). Never
                    // mirror the label beside it — that was "A A" / "LB LB".
                    _holding.Text = "";
                    _ = holding;
                }
            }

            public static void Slots()
            {
                Ensure();
                Clear();
                Centred("Menu wheel", UoTheme.Heading);
                Centred(SliceNames[_slot], UoTheme.Danger, 2);

                var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
                grid.AddThemeConstantOverride("h_separation", 6);
                grid.AddThemeConstantOverride("v_separation", 1);
                grid.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;

                // Column-major, so the D-pad's up and down walk a column.
                for (int r = 0; r < Rows; r++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        int i = col * Rows + r;

                        if (i >= Choices.Length)
                        {
                            grid.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
                            continue;
                        }

                        WheelWindow w = Choices[i];
                        bool lit = i == _pick;
                        var cell = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(138, 0) };
                        cell.AddThemeStyleboxOverride("panel", lit ? Overlay.Frame(Overlay.Parchment, 3, 1) : new StyleBoxEmpty { ContentMarginLeft = 3, ContentMarginRight = 3, ContentMarginTop = 3, ContentMarginBottom = 3 });
                        HBoxContainer row = Overlay.Row(4);
                        TextureRect icon = PadArt.Pic(PadArt.Art(PadArt.Icon(w)));
                        icon.CustomMinimumSize = new Vector2(30, 22);
                        icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                        icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                        row.AddChild(icon);
                        string taken = Array.IndexOf(_slots, w) is int at && at >= 0 && at != _slot ? $" ({SliceNames[at]})" : "";
                        row.AddChild(Overlay.Text(PadBindings.Name(w) + taken, lit ? UoTheme.Danger : UoTheme.Ink));
                        cell.AddChild(row);
                        grid.AddChild(cell);
                    }
                }

                _col.AddChild(grid);
                Layout();
            }

            public static void Done(string big, string small)
            {
                Ensure();
                Clear();
                Centred(big, UoTheme.Heading, 3);

                if (small.Length > 0)
                {
                    Centred(small, UoTheme.Ink);
                }

                VisibleText = big;
                GlyphCount = 0;
                Layout();
            }

            private static void Layout()
            {
                Vector2 ui = PadOverlay.Get().UiSize;
                _dim.Position = Vector2.Zero;
                _dim.Size = ui;
                _card.ResetSize();
                _card.Scale = Vector2.One;
                Vector2 size = _card.GetCombinedMinimumSize();
                Rect2 area = PadOverlay.ClientRect;
                float fit = 1f;

                if (size.X > area.Size.X - 12f)
                {
                    fit = (area.Size.X - 12f) / Math.Max(1f, size.X);
                }

                if (size.Y * fit > area.Size.Y - 12f)
                {
                    fit = Math.Min(fit, (area.Size.Y - 12f) / Math.Max(1f, size.Y));
                }

                fit = Math.Clamp(fit, 0.35f, 1f);
                _card.Scale = new Vector2(fit, fit);
                _card.Size = size;
                Vector2 drawn = size * fit;
                _card.Position = (area.GetCenter() - drawn / 2f).Round();
            }
        }
    }
}
