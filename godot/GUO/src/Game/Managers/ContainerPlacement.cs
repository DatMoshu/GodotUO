// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;

namespace GUO.Game.Managers
{
    /// <summary>
    /// Where a container gump goes on a screen with no room to spare: clear
    /// of the character, inside the client area, above the touch gump bar,
    /// and not on top of a gump that is already open.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream cascades containers from the top left,
    /// 20 px at a time, which on a phone lands the backpack on the character
    /// and a second container on the first. When the profile's
    /// FitContainerPlacement is on (a mobile default, see PlatformDefaults)
    /// and the player has not chosen "override container location" under
    /// Options, ContainerManager hands its answer to Place, which keeps it
    /// only if it passes the rules above.
    ///
    /// A position the client remembered (a container reopened, or restored
    /// from gumps.xml at login) is kept, moved just enough to be inside the
    /// area, unless it covers the character: the old cascade put it there,
    /// not the player, so it is placed afresh. One that covers another open
    /// gump gives way only to a spot covering less. A new container goes to the
    /// right-most, then top-most, free spot, scanning in 20 px steps; the
    /// right edge first because the character stands in the middle and the
    /// touch bar runs along the bottom. When nothing is free (a bank box at
    /// 130 % on a phone is wider and taller than half the screen) it takes
    /// the spot covering the least of the character's body, then of the area
    /// around it and the open gumps,
    /// and a container taller than the area gives up the top bar's row
    /// rather than the touch bar's.
    /// </remarks>
    internal static class ContainerPlacement
    {
        private const int Margin = 4;
        private const int Step = 20;

        /// <summary>Half the size, in client px at zoom 1, of the area kept clear around the character.</summary>
        private const int KeepOutHalfWidth = 70;
        private const int KeepOutHalfHeight = 90;

        /// <summary>More than any client's area in px, so covering the character's body costs more than covering every gump.</summary>
        private const long CharacterWeight = 1L << 24;

        public static bool Active(Profile profile) =>
            profile != null && profile.FitContainerPlacement && !profile.OverrideContainerLocation;

        public static Point Place(uint serial, ushort graphic, Point proposed, bool remembered)
        {
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(graphic);

            if (gumpInfo.Texture == null)
            {
                return proposed;
            }

            float scale = UIManager.ContainerScale;

            return Place(serial, new Point((int)(gumpInfo.UV.Width * scale), (int)(gumpInfo.UV.Height * scale)), proposed, remembered);
        }

        /// <summary>
        /// As above, for a gump whose size is known (GridContainerGump, a
        /// restored gump); <paramref name="self"/> is the gump being placed
        /// when it is already open, so it does not count as covered.
        /// </summary>
        public static Point Place(uint serial, Point size, Point proposed, bool remembered, Gump self = null)
        {
            int width = size.X;
            int height = size.Y;

            Rectangle area = UsableArea();
            Rectangle keepOut = KeepOut();
            Rectangle core = new Rectangle(keepOut.X + keepOut.Width / 4, keepOut.Y + keepOut.Height / 4, keepOut.Width / 2, keepOut.Height / 2);

            // A remembered spot clear of the character is kept when it covers
            // no other gump, and otherwise only beaten by a spot covering less
            // (a restored paperdoll may have been moved onto it).
            Point kept = Clamp(proposed, width, height, area);
            long keptCover = long.MaxValue;

            if (remembered && !new Rectangle(kept.X, kept.Y, width, height).Intersects(keepOut))
            {
                keptCover = CoveredGumps(new Rectangle(kept.X, kept.Y, width, height), serial, self);

                if (keptCover == 0)
                {
                    return kept;
                }
            }

            // The right-most, then top-most, spot that covers the least of the
            // character and of the open gumps; a free spot covers none
            // and ends the scan. A container too big for a free spot (a bank
            // box at 130 % on a phone) still gets the least bad one rather
            // than a cascade on top of everything.
            Point best = Clamp(proposed, width, height, area);
            long bestCover = long.MaxValue;
            int top = Math.Min(area.Y, Math.Max(Margin, area.Bottom - height));

            List<int> xs = new List<int>();
            List<int> ys = new List<int>();

            for (int x = Math.Max(area.X, area.Right - width); x >= area.X; x -= Step)
            {
                xs.Add(x);
            }

            for (int y = top; y == top || y + height <= area.Bottom; y += Step)
            {
                ys.Add(y);
            }

            // Flush against the edges of the character's area and of the open
            // gumps too, which a 20 px step would miss by a sliver.
            AddEdges(xs, ys, keepOut, width, height, area, top);

            foreach (Gump gump in UIManager.Gumps)
            {
                if (gump != self && !gump.IsDisposed && gump.IsVisible && !(gump is WorldViewportGump))
                {
                    Point other = Measure(gump);
                    AddEdges(xs, ys, new Rectangle(gump.X, gump.Y, other.X, other.Y), width, height, area, top);
                }
            }

            xs.Sort((l, r) => r.CompareTo(l));
            ys.Sort();

            foreach (int x in xs)
            {
                foreach (int y in ys)
                {
                    Rectangle candidate = new Rectangle(x, y, width, height);
                    // The character's body outranks any gump: a spot on it
                    // loses to every spot that is not, however crowded. The
                    // rest of the area around it counts like a gump.
                    long cover = Overlap(candidate, core) * CharacterWeight + Overlap(candidate, keepOut) + CoveredGumps(candidate, serial, self);

                    if (cover < bestCover)
                    {
                        best = new Point(x, y);
                        bestCover = cover;

                        if (cover == 0)
                        {
                            return best;
                        }
                    }
                }
            }

            return bestCover < keptCover ? best : kept;
        }

        /// <summary>
        /// Moves gumps restored from gumps.xml (a paperdoll, a status bar)
        /// off the character, by the same scan as a container, when the
        /// profile keeps containers clear. A gump that does not cover the
        /// character stays where the player left it.
        /// </summary>
        public static void ClearRestored(IEnumerable<Gump> gumps)
        {
            if (!Active(ProfileManager.CurrentProfile))
            {
                return;
            }

            Rectangle keepOut = KeepOut();

            if (keepOut.IsEmpty)
            {
                return;
            }

            foreach (Gump gump in gumps)
            {
                if (
                    gump.IsDisposed
                    || !gump.IsVisible
                    || gump is WorldViewportGump
                    || gump is TopBarGump
                    || gump is AnchorableGump anchored && UIManager.AnchorManager[anchored] != null
                )
                {
                    continue;
                }

                Point size = Measure(gump);

                if (!new Rectangle(gump.X, gump.Y, size.X, size.Y).Intersects(keepOut))
                {
                    continue;
                }

                Point from = gump.Location;
                gump.Location = Place(gump.LocalSerial, size, from, false, gump);

                Godot.GD.Print($"[GUO] restored {gump.GetType().Name} moved off the character: {from.X},{from.Y} -> {gump.X},{gump.Y}");
            }
        }

        /// <summary>Candidate spots just left of, right of, above and below a rectangle, when inside the area.</summary>
        private static void AddEdges(List<int> xs, List<int> ys, Rectangle r, int width, int height, Rectangle area, int top)
        {
            foreach (int x in new[] { r.X - width, r.Right })
            {
                if (x >= area.X && x + width <= area.Right && !xs.Contains(x))
                {
                    xs.Add(x);
                }
            }

            foreach (int y in new[] { r.Y - height, r.Bottom })
            {
                if (y >= top && y + height <= area.Bottom && !ys.Contains(y))
                {
                    ys.Add(y);
                }
            }
        }

        /// <summary>
        /// A gump's size before its first Update: Control.Update sizes a gump
        /// from its children a frame after it is added, and a restored gump
        /// is checked before that, so take the children's extent the same way,
        /// on the page Gump.Update will open (page 1 when none is set yet).
        /// </summary>
        private static Point Measure(Gump gump)
        {
            int w = gump.Width, h = gump.Height;
            int page = gump.ActivePage == 0 ? 1 : gump.ActivePage;

            foreach (UI.Controls.Control c in gump.Children)
            {
                if ((c.Page == 0 || c.Page == page) && c.IsVisible)
                {
                    w = Math.Max(w, c.Bounds.Right);
                    h = Math.Max(h, c.Bounds.Bottom);
                }
            }

            return new Point(w, h);
        }

        /// <summary>
        /// The client area less a margin, the top bar and the touch gump bar,
        /// in client px.
        /// </summary>
        private static Rectangle UsableArea()
        {
            Rectangle bounds = Client.Game.ClientBounds;

            int top = Margin;
            TopBarGump topBar = UIManager.GetGump<TopBarGump>();

            if (topBar != null && topBar.IsVisible)
            {
                top = Math.Max(top, topBar.Y + Measure(topBar).Y + Margin);
            }

            int bottom = bounds.Height - Margin;
            float barFraction = GUO.Input.Touch.TouchInput.Bar?.ReservedFraction ?? 0f;

            if (barFraction > 0f)
            {
                bottom -= (int)Math.Ceiling(barFraction * bounds.Height);
            }

            return new Rectangle(Margin, top, Math.Max(0, bounds.Width - 2 * Margin), Math.Max(0, bottom - top));
        }

        /// <summary>
        /// The rectangle around the character. The camera keeps the player in
        /// the middle of the world view, so it is the middle of that gump.
        /// </summary>
        private static Rectangle KeepOut()
        {
            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport == null)
            {
                return Rectangle.Empty;
            }

            float zoom = Client.Game.GetScene<GameScene>()?.Camera.Zoom ?? 1f;

            if (zoom <= 0f)
            {
                zoom = 1f;
            }

            int halfW = (int)(KeepOutHalfWidth / zoom);
            int halfH = (int)(KeepOutHalfHeight / zoom);
            int cx = viewport.X + viewport.Width / 2;
            int cy = viewport.Y + viewport.Height / 2;

            return new Rectangle(cx - halfW, cy - halfH, halfW * 2, halfH * 2);
        }

        /// <summary>
        /// Area, in client px, of the open gumps the candidate covers: every
        /// visible gump but the world view, the top bar (already outside the
        /// area), the gump being placed, and a container gump for the same
        /// container (the classic view a grid is replacing).
        /// </summary>
        private static long CoveredGumps(Rectangle candidate, uint serial, Gump self)
        {
            long covered = 0;

            foreach (Gump gump in UIManager.Gumps)
            {
                if (
                    gump == self
                    || gump.IsDisposed
                    || !gump.IsVisible
                    || gump is WorldViewportGump
                    || gump is TopBarGump
                    || (gump is ContainerGump || gump is GridContainerGump) && gump.LocalSerial == serial
                )
                {
                    continue;
                }

                Point size = Measure(gump);
                covered += Overlap(candidate, new Rectangle(gump.X, gump.Y, size.X, size.Y));
            }

            return covered;
        }

        private static long Overlap(Rectangle a, Rectangle b)
        {
            int w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            int h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);

            return w > 0 && h > 0 ? (long)w * h : 0;
        }

        private static Point Clamp(Point p, int width, int height, Rectangle area)
        {
            // Too tall for the area: give up the top bar's row, never the
            // touch bar's, which is where the thumbs are.
            int x = Math.Max(area.X, Math.Min(p.X, area.Right - width));
            int y = Math.Min(Math.Max(area.Y, p.Y), area.Bottom - height);

            return new Point(x, Math.Max(Margin, y));
        }
    }
}
