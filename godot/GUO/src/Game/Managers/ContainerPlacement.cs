// SPDX-License-Identifier: BSD-2-Clause

using System;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;

namespace GUO.Game.Managers
{
    /// <summary>
    /// Where a container gump goes on a screen with no room to spare: clear
    /// of the character, inside the client area, above the touch gump bar,
    /// and not on top of a container that is already open.
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
    /// not the player, so it is placed afresh. A new container goes to the
    /// right-most, then top-most, free spot, scanning in 20 px steps; the
    /// right edge first because the character stands in the middle and the
    /// touch bar runs along the bottom. When nothing is free (a bank box at
    /// 130 % on a phone is wider and taller than half the screen) it takes
    /// the spot covering the least of the character and the open containers,
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

        /// <summary>As above, for a gump whose size is known (GridContainerGump).</summary>
        public static Point Place(uint serial, Point size, Point proposed, bool remembered)
        {
            int width = size.X;
            int height = size.Y;

            Rectangle area = UsableArea();
            Rectangle keepOut = KeepOut();

            if (remembered)
            {
                Point kept = Clamp(proposed, width, height, area);

                if (!new Rectangle(kept.X, kept.Y, width, height).Intersects(keepOut))
                {
                    return kept;
                }
            }

            // The right-most, then top-most, spot that covers the least of the
            // character and of the open containers; a free spot covers none
            // and ends the scan. A container too big for a free spot (a bank
            // box at 130 % on a phone) still gets the least bad one rather
            // than a cascade on top of everything.
            Point best = Clamp(proposed, width, height, area);
            long bestCover = long.MaxValue;
            int top = Math.Min(area.Y, Math.Max(Margin, area.Bottom - height));

            for (int x = Math.Max(area.X, area.Right - width); x >= area.X; x -= Step)
            {
                for (int y = top; y == top || y + height <= area.Bottom; y += Step)
                {
                    Rectangle candidate = new Rectangle(x, y, width, height);
                    long cover = Overlap(candidate, keepOut) + CoveredContainers(candidate, serial);

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

            return best;
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
                top = Math.Max(top, topBar.Y + topBar.Height + Margin);
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

        /// <summary>Area, in client px, of the open containers other than this one that the candidate covers.</summary>
        private static long CoveredContainers(Rectangle candidate, uint serial)
        {
            long covered = 0;

            foreach (Gump gump in UIManager.Gumps)
            {
                if ((gump is ContainerGump || gump is GridContainerGump) && !gump.IsDisposed && gump.IsVisible && gump.LocalSerial != serial)
                {
                    covered += Overlap(candidate, new Rectangle(gump.X, gump.Y, gump.Width, gump.Height));
                }
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
