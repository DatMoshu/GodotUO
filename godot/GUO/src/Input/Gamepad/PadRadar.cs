// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Input.Touch;
using GUO.Pregame3D;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// The interact radar (the owner's design, 2026-10-03): hold its input (RT
    /// by default) and everything usable within <see cref="PadBindings.RadarRange"/>
    /// tiles is marked: mobiles, doors, corpses, containers and movable items
    /// on the ground, from the client's own world lists. The right stick snaps
    /// the choice to the nearest thing the way it is pushed; the Target last
    /// and Next hostile inputs (LB/RB) step through them nearest-first. Use
    /// double-clicks the choice (an attack on a mobile in war mode, as the
    /// client's double-click is), Attack last's input looks (a single click),
    /// Macro row's asks for its context menu. Letting go with nothing pressed
    /// uses the choice.
    /// </summary>
    /// <remarks>
    /// With a target cursor up the radar is how a pad picks the target: Use
    /// sends it to the choice and Cancel cancels the cursor. The choice is
    /// highlighted the way the client highlights what the mouse is over
    /// (GameScene.DrawWorld reads <see cref="Highlight"/>), plus a tile mark,
    /// the thing's art icon when it has one, the target brackets of the client's
    /// "new target system", and a name plate only on the highlighted choice.
    /// </remarks>
    internal static class PadRadar
    {
        private const float Push = 0.6f, Rest = 0.3f;
        private const float ResnapDegrees = 40f;
        private const float ConeDegrees = 75f;

        private static readonly List<Entity> _near = new();
        private static Entity _chosen;
        private static bool _acted;
        private static bool _pushed;
        private static Vector2 _pushDir;
        private static int _refresh;

        public static bool IsOpen { get; private set; }

        /// <summary>The choice, or null.</summary>
        public static Entity Chosen => _chosen != null && !_chosen.IsDestroyed ? _chosen : null;

        /// <summary>What the world draws highlighted: the choice while the radar is up.</summary>
        public static BaseGameObject Highlight => IsOpen ? Chosen : null;

        /// <summary>What is near, nearest first.</summary>
        public static IReadOnlyList<Entity> Near => _near;

        /// <summary>For probes: the last thing done ("use 0x...", "look ...", "target ...").</summary>
        public static string LastAction { get; private set; } = "";

        private static World World => Client.Game?.UO?.World;

        public static void Begin()
        {
            World world = World;

            if (IsOpen || world == null || !world.InGame || world.Player == null)
            {
                return;
            }

            IsOpen = true;
            _acted = false;
            _chosen = null;
            _pushed = false;
            Refresh(world);
            RadarView.Show();
            GD.Print($"[GUO] pad radar: open, {_near.Count} near");
        }

        /// <summary>The radar's input let go: use the choice unless a button already acted.</summary>
        public static void End()
        {
            if (!IsOpen)
            {
                return;
            }

            if (!_acted && Chosen != null)
            {
                Use();
            }

            Close();
        }

        public static void Close()
        {
            IsOpen = false;
            _chosen = null;
            _near.Clear();
            RadarView.Hide();
        }

        /// <summary>Once a frame while open: refresh what is near, follow the right stick.</summary>
        public static void Update(float rx, float ry)
        {
            World world = World;

            if (!IsOpen)
            {
                return;
            }

            if (world == null || !world.InGame || world.Player == null)
            {
                Close();
                return;
            }

            if (++_refresh % 6 == 0)
            {
                Refresh(world);
            }

            var stick = new Vector2(rx, ry);
            float len = stick.Length();

            if (len >= Push)
            {
                Vector2 dir = stick / len;

                if (!_pushed || Mathf.RadToDeg(Mathf.Acos(Math.Clamp(dir.Dot(_pushDir), -1f, 1f))) > ResnapDegrees)
                {
                    _pushed = true;
                    _pushDir = dir;
                    Snap(dir);
                }
            }
            else if (len < Rest)
            {
                _pushed = false;
            }

            RadarView.Update();
        }

        private static void Refresh(World world)
        {
            int range = PadBindings.RadarRange;
            _near.Clear();

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (m != null && !m.IsDestroyed && m != world.Player && m.Distance <= range)
                {
                    _near.Add(m);
                }
            }

            foreach (Item i in world.Items.Values)
            {
                if (i == null || i.IsDestroyed || !i.OnGround || i.IsMulti || i.Distance > range)
                {
                    continue;
                }

                bool usable = i.IsCorpse || i.ItemData.IsDoor || i.ItemData.IsContainer || (i.Flags & Flags.Movable) != 0;

                if (usable)
                {
                    _near.Add(i);
                }
            }

            _near.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Serial.CompareTo(b.Serial));

            if (_chosen != null && (!_near.Contains(_chosen) || _chosen.IsDestroyed))
            {
                _chosen = null;
            }
        }

        /// <summary>The nearest thing the way <paramref name="dir"/> points (screen space), angle-weighted.</summary>
        private static void Snap(Vector2 dir)
        {
            World world = World;
            Vector2 from = RadarView.Foot(world.Player);
            Entity best = null;
            float bestScore = float.MaxValue;

            foreach (Entity e in _near)
            {
                Vector2 d = RadarView.Foot(e) - from;
                float len = d.Length();

                if (len < 1f)
                {
                    // Underfoot: any direction reaches it, last.
                    len = 1f;
                    d = dir;
                }

                float cos = d.Normalized().Dot(dir);

                if (Mathf.RadToDeg(Mathf.Acos(Math.Clamp(cos, -1f, 1f))) > ConeDegrees)
                {
                    continue;
                }

                float score = len * (1f + 2f * (1f - cos));

                if (score < bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            if (best != null)
            {
                Choose(best);
            }
        }

        /// <summary>LB/RB: the next or previous thing, nearest first, round.</summary>
        public static void Step(int by)
        {
            if (_near.Count == 0)
            {
                return;
            }

            int at = _chosen == null ? -1 : _near.IndexOf(_chosen);
            int next = at < 0 ? (by > 0 ? 0 : _near.Count - 1) : ((at + by) % _near.Count + _near.Count) % _near.Count;
            Choose(_near[next]);
        }

        private static void Choose(Entity e)
        {
            _chosen = e;
            GD.Print($"[GUO] pad radar: chose {NameOf(e)} 0x{e.Serial:X8} ({e.Distance} tiles)");
            RadarView.Update();
        }

        // --- the card's buttons ---------------------------------------------------------

        /// <summary>A: double-click the choice, or with a target cursor up, target it.</summary>
        public static void Use()
        {
            World world = World;
            Entity e = Chosen;

            if (world == null || e == null)
            {
                return;
            }

            _acted = true;

            if (world.TargetManager.IsTargeting)
            {
                LastAction = $"target 0x{e.Serial:X8}";
                world.TargetManager.Target(e.Serial);
            }
            else if (e is Item item && item.IsCorpse)
            {
                LastAction = $"use 0x{e.Serial:X8}";
                GameActions.OpenCorpse(world, e.Serial);
            }
            else
            {
                LastAction = $"use 0x{e.Serial:X8}";
                GameActions.DoubleClick(world, e.Serial);
            }

            GD.Print($"[GUO] pad radar: {LastAction} ({NameOf(e)})");
        }

        /// <summary>X: look (single-click: the name over its head).</summary>
        public static void Look()
        {
            World world = World;
            Entity e = Chosen;

            if (world == null || e == null)
            {
                return;
            }

            _acted = true;
            LastAction = $"look 0x{e.Serial:X8}";
            GameActions.SingleClick(world, e.Serial);
            GD.Print($"[GUO] pad radar: {LastAction} ({NameOf(e)})");
        }

        /// <summary>Y: the choice's context menu, as the client asks for one.</summary>
        public static void Context()
        {
            Entity e = Chosen;

            if (e == null)
            {
                return;
            }

            _acted = true;
            LastAction = $"menu 0x{e.Serial:X8}";
            GameActions.OpenPopupMenu(e.Serial, true);
            GD.Print($"[GUO] pad radar: {LastAction} ({NameOf(e)})");
        }

        /// <summary>B: cancel a target cursor, else shut the radar without using anything.</summary>
        public static void Cancel()
        {
            World world = World;
            _acted = true;

            if (world != null && world.TargetManager.IsTargeting)
            {
                LastAction = "cancel target";
                world.TargetManager.CancelTarget();
                RadarView.Update();
                return;
            }

            LastAction = "closed";
            Close();
        }

        public static string NameOf(Entity e)
        {
            if (e == null)
            {
                return "";
            }

            string name = e.Name;

            if (string.IsNullOrWhiteSpace(name) && e is Item i)
            {
                name = i.ItemData.Name;
            }

            name = (name ?? "").Trim();

            if (name.Length == 0)
            {
                return e is Item it && it.ItemData.IsDoor ? "a door" : "something";
            }

            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>Red in the radar: criminals, murderers, enemies. Grey (attackable) animals stay gold.</summary>
        public static bool Hostile(Entity e) => e is Mobile m && m.NotorietyFlag is NotorietyFlag.Criminal or NotorietyFlag.Murderer
            or NotorietyFlag.Enemy;
    }

    /// <summary>The radar drawn: tile marks and brackets on the world, a name plate, the card.</summary>
    internal static class RadarView
    {
        private static RadarMarks _marks;
        private static PanelContainer _plate, _card;
        private static Label _plateName, _title, _sub;
        private static HBoxContainer _rowA, _rowB;

        /// <summary>A thing's feet (its tile's middle) in window pixels.</summary>
        public static Vector2 Foot(GameObject o)
        {
            Compat.Point p = o.RealScreenPosition;
            p.X += (int) o.Offset.X + 22;
            p.Y += (int) (o.Offset.Y - o.Offset.Z) + 22;
            return ToWindow(p);
        }

        /// <summary>A world-space point (as RealScreenPosition) in window pixels.</summary>
        public static Vector2 ToWindow(Compat.Point world)
        {
            Compat.Point s = Client.Game.Scene.Camera.WorldToScreen(world, true);
            float dpi = Client.Game.DpiScale;
            return new Vector2(s.X * dpi, s.Y * dpi);
        }

        /// <summary>How tall the thing stands, in world pixels: over its head.</summary>
        public static int Height(Entity e)
        {
            if (e is Mobile m)
            {
                // As HealthLinesManager places a mobile's bar over its head.
                Client.Game.UO.Animations.GetAnimationDimensions(m.AnimIndex, m.GetGraphicForAnimation(), 0, 0, m.IsMounted, 0,
                    out _, out int centerY, out _, out int height);
                return Math.Clamp(height + centerY + 8, 30, 180);
            }

            return Math.Clamp(Client.Game.UO.Arts.GetRealArtBounds(e.Graphic).Height, 12, 120);
        }

        public static void Show()
        {
            PadOverlay layer = PadOverlay.Get();

            if (layer == null || !PadArt.Warm())
            {
                return;
            }

            if (_marks == null || !GodotObject.IsInstanceValid(_marks))
            {
                Build(layer);
            }

            _marks.Visible = true;
            _card.Visible = true;
            Update();
        }

        public static void Hide()
        {
            if (_marks != null && GodotObject.IsInstanceValid(_marks))
            {
                _marks.Visible = false;
                _plate.Visible = false;
                _card.Visible = false;
            }
        }

        private static void Build(PadOverlay layer)
        {
            _marks = new RadarMarks { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "RadarMarks" };
            layer.Screen.AddChild(_marks);

            _plate = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _plate.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 5, 1));
            _plateName = Overlay.Text("", UoTheme.Heading);
            _plate.AddChild(_plateName);
            layer.Ui.AddChild(_plate);

            _card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _card.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Stone, 8));
            VBoxContainer col = Overlay.Column(3);
            _title = Overlay.Text("", UoTheme.Heading);
            _title.HorizontalAlignment = HorizontalAlignment.Center;
            _sub = Overlay.Text("", UoTheme.Muted);
            _sub.HorizontalAlignment = HorizontalAlignment.Center;
            _rowA = Overlay.Row(10);
            _rowA.Alignment = BoxContainer.AlignmentMode.Center;
            _rowB = Overlay.Row(10);
            _rowB.Alignment = BoxContainer.AlignmentMode.Center;
            col.AddChild(_title);
            col.AddChild(_sub);
            col.AddChild(_rowA);
            col.AddChild(_rowB);
            _card.AddChild(col);
            layer.Ui.AddChild(_card);
        }

        private static readonly List<(PadCommand, string)> _a = new(), _b = new();

        public static void Update()
        {
            if (_marks == null || !GodotObject.IsInstanceValid(_marks) || !_marks.Visible)
            {
                return;
            }

            _marks.QueueRedraw();
            World world = Client.Game.UO.World;
            Entity chosen = PadRadar.Chosen;
            bool targeting = world.TargetManager.IsTargeting;

            _title.Text = chosen != null ? PadRadar.NameOf(chosen) : targeting ? "Pick a target" : "Nothing chosen";
            _title.AddThemeColorOverride("font_color", chosen != null && PadRadar.Hostile(chosen) ? UoTheme.Danger : UoTheme.Heading);
            _sub.Text = chosen != null
                ? $"{chosen.Distance} tile{(chosen.Distance == 1 ? "" : "s")} away" + (targeting ? " - target cursor up" : "")
                : $"{PadRadar.Near.Count} near: right stick or LB / RB";

            _a.Clear();
            _b.Clear();

            if (targeting)
            {
                _a.Add((PadCommand.Use, "Target"));
                _a.Add((PadCommand.Cancel, "Cancel target"));
            }
            else if (world.Player.InWarMode)
            {
                _a.Add((PadCommand.Use, chosen is Mobile ? "Attack" : "Use"));
                _a.Add((PadCommand.AttackLast, "Attack last"));
                _a.Add((PadCommand.MacroRow, "Menu"));
            }
            else
            {
                _a.Add((PadCommand.Use, "Use"));
                _a.Add((PadCommand.AttackLast, "Look"));
                _a.Add((PadCommand.MacroRow, "Menu"));
            }

            _b.Add((PadCommand.TargetLast, "Back"));
            _b.Add((PadCommand.NextHostile, "Next"));

            if (!targeting)
            {
                _b.Add((PadCommand.Cancel, "Close"));
            }

            Fill(_rowA, _a);
            Fill(_rowB, _b);

            // At the foot of the world view, scaled down so a large UI scale cannot push it off the Deck.
            Rect2 view = PadOverlay.WorldRect;
            _card.ResetSize();
            _card.Scale = Vector2.One;
            Vector2 cs = _card.GetCombinedMinimumSize();
            float fit = 1f;

            if (cs.X > view.Size.X - 8f)
            {
                fit = (view.Size.X - 8f) / Math.Max(1f, cs.X);
            }

            if (cs.Y * fit > view.Size.Y - 12f)
            {
                fit = Math.Min(fit, (view.Size.Y - 12f) / Math.Max(1f, cs.Y));
            }

            fit = Math.Clamp(fit, 0.4f, 1f);
            _card.Scale = new Vector2(fit, fit);
            _card.Size = cs;
            Vector2 drawn = cs * fit;
            float cx = Math.Clamp(view.GetCenter().X - drawn.X / 2f, view.Position.X, Math.Max(view.Position.X, view.End.X - drawn.X));
            float cy = Math.Clamp(view.End.Y - drawn.Y - 8f, view.Position.Y, Math.Max(view.Position.Y, view.End.Y - drawn.Y));
            _card.Position = new Vector2(Mathf.Round(cx), Mathf.Round(cy));

            if (chosen != null)
            {
                _plateName.Text = PadRadar.NameOf(chosen);
                _plateName.AddThemeColorOverride("font_color", PadRadar.Hostile(chosen) ? UoTheme.Danger : UoTheme.Heading);
                _plate.ResetSize();
                _plate.Scale = Vector2.One;
                Vector2 ps = _plate.GetCombinedMinimumSize();
                float s = PadOverlay.UiScale;
                Vector2 head = Head(chosen) / s;
                Rect2 inside = PadOverlay.WorldRect;
                float pfit = 1f;

                if (ps.X > inside.Size.X - 4f)
                {
                    pfit = (inside.Size.X - 4f) / Math.Max(1f, ps.X);
                }

                pfit = Math.Clamp(pfit, 0.45f, 1f);
                _plate.Scale = new Vector2(pfit, pfit);
                _plate.Size = ps;
                Vector2 pd = ps * pfit;
                float px = Math.Clamp(head.X - pd.X / 2f, inside.Position.X, Math.Max(inside.Position.X, inside.End.X - pd.X));
                float py = Math.Clamp(head.Y - pd.Y - 10, inside.Position.Y, Math.Max(inside.Position.Y, inside.End.Y - pd.Y));
                _plate.Position = new Vector2(Mathf.Round(px), Mathf.Round(py));
                _plate.Visible = true;
            }
            else
            {
                _plate.Visible = false;
            }
        }

        /// <summary>Over the thing's head, in window pixels.</summary>
        public static Vector2 Head(Entity e)
        {
            Compat.Point p = e.RealScreenPosition;
            p.X += (int) e.Offset.X + 22;
            p.Y += (int) (e.Offset.Y - e.Offset.Z) + 22 - Height(e);
            return ToWindow(p);
        }

        private static void Fill(HBoxContainer row, List<(PadCommand command, string words)> items)
        {
            foreach (Node n in row.GetChildren())
            {
                row.RemoveChild(n);
                n.QueueFree();
            }

            foreach ((PadCommand command, string words) in items)
            {
                HBoxContainer pair = Overlay.Row(3);
                pair.AddChild(PadArt.Cap(PadBindings.For(command)));
                pair.AddChild(Overlay.Text(words, UoTheme.Ink));
                row.AddChild(pair);
            }
        }
    }

    /// <summary>The marks on the world: a tile outline under each thing near, the choice lit and bracketed.</summary>
    internal sealed partial class RadarMarks : Control
    {
        private static readonly Color Gold = UoTheme.Gold;
        private static readonly Color Red = new("d04030");

        public override void _Draw()
        {
            if (!PadRadar.IsOpen || Client.Game?.Scene?.Camera == null)
            {
                return;
            }

            Entity chosen = PadRadar.Chosen;
            float s = PadOverlay.UiScale;

            foreach (Entity e in PadRadar.Near)
            {
                if (e == chosen || e.IsDestroyed)
                {
                    continue;
                }

                Color c = PadRadar.Hostile(e) ? Red : Gold;
                Diamond(e, new Color(c, 0.85f), null, Math.Max(1f, s));
                Icon(e, false);
            }

            if (chosen != null)
            {
                Color c = PadRadar.Hostile(chosen) ? Red : Gold;
                Diamond(chosen, new Color(c, 1f), new Color(c, 0.30f), Math.Max(2f, 2f * s));
                Icon(chosen, true);
                Brackets(chosen, c);
            }
        }

        /// <summary>
        /// The thing's art, when the client has a static for it. No name:
        /// the name plate is only the highlighted choice.
        /// </summary>
        private void Icon(Entity e, bool lit)
        {
            Texture2D art = PadArt.Art(e.Graphic);

            if (art == null)
            {
                return;
            }

            float s = Math.Clamp(PadOverlay.UiScale, 0.5f, 4f);
            float side = Math.Clamp(26f * s, 18f, 48f);
            Vector2 foot = RadarView.Foot(e);
            var rect = new Rect2(foot - new Vector2(side * 0.5f, side + 6f), new Vector2(side, side));
            DrawTextureRect(art, rect, false, lit ? Colors.White : new Color(1f, 1f, 1f, 0.85f));
        }

        /// <summary>The thing's tile as the client draws a tile: a 44 px diamond, through the camera.</summary>
        private void Diamond(Entity e, Color line, Color? fill, float width)
        {
            Compat.Point p = e.RealScreenPosition;
            int cx = p.X + (int) e.Offset.X + 22, cy = p.Y + (int) (e.Offset.Y - e.Offset.Z) + 22;
            Vector2[] pts =
            {
                RadarView.ToWindow(new Compat.Point(cx, cy - 22)),
                RadarView.ToWindow(new Compat.Point(cx + 22, cy)),
                RadarView.ToWindow(new Compat.Point(cx, cy + 22)),
                RadarView.ToWindow(new Compat.Point(cx - 22, cy)),
            };

            if (fill is Color f)
            {
                DrawColoredPolygon(pts, f);
            }

            var loop = new Vector2[] { pts[0], pts[1], pts[2], pts[3], pts[0] };
            DrawPolyline(loop, line, width, false);
        }

        /// <summary>The client's target brackets (HealthLinesManager's new target system), over the head and at the feet.</summary>
        private void Brackets(Entity e, Color c)
        {
            float s = PadOverlay.UiScale;
            Texture2D top = PadArt.Gump(PadArt.BracketTop), bottom = PadArt.Gump(PadArt.BracketBottom);
            Vector2 head = RadarView.Head(e), foot = RadarView.Foot(e);

            if (top != null)
            {
                Vector2 size = top.GetSize() * s;
                DrawTextureRect(top, new Rect2((head - new Vector2(size.X / 2f, size.Y)).Round(), size), false, c);
            }

            if (bottom != null)
            {
                Vector2 size = bottom.GetSize() * s;
                DrawTextureRect(bottom, new Rect2((foot - new Vector2(size.X / 2f, size.Y * 0.25f)).Round(), size), false, c);
            }
        }
    }
}
