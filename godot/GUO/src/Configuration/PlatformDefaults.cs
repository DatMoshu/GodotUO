// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;

namespace GUO.Configuration
{
    /// <summary>The platform a profile's defaults are chosen for.</summary>
    internal enum ProfilePlatform
    {
        Desktop,
        Mobile,
        Web
    }

    /// <summary>
    /// What a profile starts as on each platform, and how a profile saved
    /// before these defaults existed is brought up to them.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has one platform and one set of
    /// defaults, the property initialisers in Profile. GUO also runs on
    /// phones and in a browser, where some of those defaults are wrong. Every
    /// per-platform default lives in the table below and nowhere else. The
    /// desktop differs from upstream only in the entries its table lists.
    ///
    /// Each entry names a Profile field, the value it has in upstream's
    /// Profile (the desktop default), and the value on this platform.
    /// Profile.ProfileVersion records which table a profile has been
    /// through. When a profile loads with an older version (a new profile is
    /// version 0 too), every entry whose field still holds the desktop
    /// default is set to the platform value. A field the player changed
    /// away from the desktop default is left alone. Nothing records which
    /// fields a player touched, so "still holds the desktop default" stands
    /// in for "never changed". A player who deliberately set a field back to
    /// the desktop value gets the platform value once, on the first load
    /// after an upgrade, and can set it again under Options.
    ///
    /// To add a default: bump CurrentVersion and give the entry that version
    /// as `since`. A profile only goes through the entries added after the
    /// version it was saved with, so a player who turned an older default
    /// back off keeps it off.
    /// </remarks>
    internal static class PlatformDefaults
    {
        /// <summary>
        /// The table version this build writes. 0 means the profile predates
        /// platform defaults (every profile saved before GUO had them).
        /// </summary>
        public const int CurrentVersion = 10;

        /// <summary>The login screen's size, which every login gump is laid out for.</summary>
        private const int LoginWidth = 640;
        private const int LoginHeight = 480;

        private sealed class Entry
        {
            public readonly string Name;
            public readonly int Since;
            public readonly Func<Profile, bool> IsDesktopDefault;
            public readonly Action<Profile> Apply;

            public Entry(string name, int since, Func<Profile, bool> isDesktopDefault, Action<Profile> apply)
            {
                Name = name;
                Since = since;
                IsDesktopDefault = isDesktopDefault;
                Apply = apply;
            }
        }

        // The upstream default each entry is compared against, taken from a
        // fresh Profile so it cannot drift from Profile.cs.
        private static readonly Profile Upstream = new Profile();

        // v7: what the second screen's shelf takes when a gump opens
        // (DualScreen, ADR-0009 amendment): the four session gumps, on every
        // platform, since the feature only exists where a display does (or
        // the desktop simulator asks for one). "Others" and the shelf's own
        // scale (0 = the main screen's) stay at Profile's neutral values.
        private static readonly Entry[] DualScreenShelf =
        {
            BoolEntry(nameof(Profile.DualScreenShelvePaperdoll), p => p.DualScreenShelvePaperdoll, (p, v) => p.DualScreenShelvePaperdoll = v, true, since: 7),
            BoolEntry(nameof(Profile.DualScreenShelveBackpack), p => p.DualScreenShelveBackpack, (p, v) => p.DualScreenShelveBackpack = v, true, since: 7),
            BoolEntry(nameof(Profile.DualScreenShelveStatus), p => p.DualScreenShelveStatus, (p, v) => p.DualScreenShelveStatus = v, true, since: 7),
            BoolEntry(nameof(Profile.DualScreenShelveJournal), p => p.DualScreenShelveJournal, (p, v) => p.DualScreenShelveJournal = v, true, since: 7)
        };

        private static Entry[] WithShelf(params Entry[] entries)
        {
            var all = new Entry[entries.Length + DualScreenShelf.Length];
            entries.CopyTo(all, 0);
            DualScreenShelf.CopyTo(all, entries.Length);

            return all;
        }

        private static readonly Dictionary<ProfilePlatform, Entry[]> Table = new Dictionary<ProfilePlatform, Entry[]>
        {
            [ProfilePlatform.Desktop] = WithShelf
            (
                // v5: the world fills the window and the wheel zooms, as on
                // Mobile and Web, instead of upstream's 600x480 world in the
                // corner of a large window. Unticking "Game window full size"
                // under Options still gives the smaller, movable world.
                // (v4 is work/background's canvas background entry.)
                BoolEntry(nameof(Profile.GameWindowFullSize), p => p.GameWindowFullSize, (p, v) => p.GameWindowFullSize = v, true, since: 5),
                PointEntry(nameof(Profile.GameWindowPosition), p => p.GameWindowPosition, (p, v) => p.GameWindowPosition = v, _ => new Point(-5, -5), since: 5),
                PointEntry(nameof(Profile.GameWindowSize), p => p.GameWindowSize, (p, v) => p.GameWindowSize = v, FullWindowSize, since: 5),
                BoolEntry(nameof(Profile.EnableMousewheelScaleZoom), p => p.EnableMousewheelScaleZoom, (p, v) => p.EnableMousewheelScaleZoom = v, true, since: 5),
                BoolEntry(nameof(Profile.SaveScaleAfterClose), p => p.SaveScaleAfterClose, (p, v) => p.SaveScaleAfterClose = v, true, since: 5)
            ),

            [ProfilePlatform.Mobile] = WithShelf
            (
                // Carried over from the touch layer's MobileProfile.Apply
                // (branch work/android, ADR-0017 "Window and scale"), which
                // set these on a new profile before this table existed:
                // the world fills the screen, a pinch (the touch layer's
                // Ctrl+wheel) zooms, and the zoom is kept between sessions.
                // The window size is the one the "Game window full size"
                // option computes when ticked (OptionsGump).
                BoolEntry(nameof(Profile.GameWindowFullSize), p => p.GameWindowFullSize, (p, v) => p.GameWindowFullSize = v, true),
                PointEntry(nameof(Profile.GameWindowPosition), p => p.GameWindowPosition, (p, v) => p.GameWindowPosition = v, _ => new Point(-5, -5)),
                PointEntry(nameof(Profile.GameWindowSize), p => p.GameWindowSize, (p, v) => p.GameWindowSize = v, FullWindowSize),
                BoolEntry(nameof(Profile.EnableMousewheelScaleZoom), p => p.EnableMousewheelScaleZoom, (p, v) => p.EnableMousewheelScaleZoom = v, true),
                BoolEntry(nameof(Profile.SaveScaleAfterClose), p => p.SaveScaleAfterClose, (p, v) => p.SaveScaleAfterClose = v, true),

                // A phone has no window chrome to lose. The flag is set so
                // the profile says what the screen shows.
                BoolEntry(nameof(Profile.WindowBorderless), p => p.WindowBorderless, (p, v) => p.WindowBorderless = v, true),

                // Containers a finger can hit: the large gumps, scaled up,
                // with the items scaled along with them.
                BoolEntry(nameof(Profile.UseLargeContainerGumps), p => p.UseLargeContainerGumps, (p, v) => p.UseLargeContainerGumps = v, true),
                ByteEntry(nameof(Profile.ContainersScale), p => p.ContainersScale, (p, v) => p.ContainersScale = v, 130),
                BoolEntry(nameof(Profile.ScaleItemsInsideContainers), p => p.ScaleItemsInsideContainers, (p, v) => p.ScaleItemsInsideContainers = v, true),

                // The grid loot gump in place of the corpse gump (1 = grid
                // only). Until v8 this was 2, both, which opened two windows
                // on every corpse (Thor pass 2, bug 5).
                IntEntry(nameof(Profile.GridLootType), p => p.GridLootType, (p, v) => p.GridLootType = v, 1),

                // v2: containers open clear of the character, the touch bar
                // and each other (ContainerPlacement).
                BoolEntry(nameof(Profile.FitContainerPlacement), p => p.FitContainerPlacement, (p, v) => p.FitContainerPlacement = v, true, since: 2),

                // v3: containers open as a grid of finger-sized slots
                // (GridContainerGump).
                BoolEntry(nameof(Profile.GridContainers), p => p.GridContainers, (p, v) => p.GridContainers = v, true, since: 3),

                // v6: a video or frames background shows its first frame only
                // (CanvasBackground, ADR-0016): a battery, and a phone GPU.
                BoolEntry(nameof(Profile.CanvasBackgroundLowPower), p => p.CanvasBackgroundLowPower, (p, v) => p.CanvasBackgroundLowPower = v, true, since: 6),

                // v8: a profile still on the old mobile value, 2, moves to
                // grid only. It is not the upstream default, so the usual
                // test cannot see it; a player who chose 2 under Options is
                // indistinguishable and gets 1 once.
                new Entry(nameof(Profile.GridLootType), 8, p => p.GridLootType == 2, p => p.GridLootType = 1),

                // v9: the touch bar's chevron and macro row (TouchGumpBar):
                // Target next, Attack last and the rest a mouse reaches by
                // hotkey, for moving targets a finger cannot keep up with
                // (Thor pass 2, bug 7).
                BoolEntry(nameof(Profile.TouchMacroRow), p => p.TouchMacroRow, (p, v) => p.TouchMacroRow = v, true, since: 9),

                // v10: the idle screen saver (ScreenSaver): a phone is the
                // OLED panel most likely to be left on through a long run.
                BoolEntry(nameof(Profile.ScreenSaver), p => p.ScreenSaver, (p, v) => p.ScreenSaver = v, true, since: 10)
            ),

            [ProfilePlatform.Web] = WithShelf
            (
                // The canvas is the page, so the world fills it and the
                // wheel zooms. Containers stay desktop-sized: a browser has
                // a mouse. A browser on a phone is Mobile, not Web.
                BoolEntry(nameof(Profile.GameWindowFullSize), p => p.GameWindowFullSize, (p, v) => p.GameWindowFullSize = v, true),
                PointEntry(nameof(Profile.GameWindowPosition), p => p.GameWindowPosition, (p, v) => p.GameWindowPosition = v, _ => new Point(-5, -5)),
                PointEntry(nameof(Profile.GameWindowSize), p => p.GameWindowSize, (p, v) => p.GameWindowSize = v, FullWindowSize),
                BoolEntry(nameof(Profile.EnableMousewheelScaleZoom), p => p.EnableMousewheelScaleZoom, (p, v) => p.EnableMousewheelScaleZoom = v, true),
                BoolEntry(nameof(Profile.SaveScaleAfterClose), p => p.SaveScaleAfterClose, (p, v) => p.SaveScaleAfterClose = v, true),

                // v6: as on Mobile; a browser tab decodes video on the CPU.
                BoolEntry(nameof(Profile.CanvasBackgroundLowPower), p => p.CanvasBackgroundLowPower, (p, v) => p.CanvasBackgroundLowPower = v, true, since: 6)
            )
        };

        private static ProfilePlatform? _platform;

        /// <summary>
        /// The platform this run takes its defaults from: a phone or tablet
        /// (including a browser on one) is Mobile, any other browser is Web,
        /// everything else is Desktop. UO_PROFILE_PLATFORM
        /// (desktop|mobile|web) overrides it, so the mobile defaults can be
        /// checked on a desktop.
        /// </summary>
        public static ProfilePlatform Platform
        {
            get
            {
                if (_platform.HasValue)
                {
                    return _platform.Value;
                }

                string forced = OS.GetEnvironment("UO_PROFILE_PLATFORM");

                if (!string.IsNullOrWhiteSpace(forced) && Enum.TryParse(forced.Trim(), true, out ProfilePlatform p))
                {
                    _platform = p;
                }
                else if (OS.HasFeature("mobile") || OS.HasFeature("web_android") || OS.HasFeature("web_ios"))
                {
                    _platform = ProfilePlatform.Mobile;
                }
                else if (OS.HasFeature("web"))
                {
                    _platform = ProfilePlatform.Web;
                }
                else
                {
                    _platform = ProfilePlatform.Desktop;
                }

                return _platform.Value;
            }
        }

        /// <summary>
        /// Bring a profile up to this platform's defaults if it has not been
        /// through the current table. Returns true if the profile changed.
        /// </summary>
        public static bool Apply(Profile profile, bool isNew)
        {
            if (profile == null || profile.ProfileVersion >= CurrentVersion)
            {
                return false;
            }

            int from = profile.ProfileVersion;
            List<string> applied = new List<string>();

            foreach (Entry entry in Table[Platform])
            {
                // Only entries added since this profile's version: one it
                // has already been through may since have been changed back
                // by the player.
                if (entry.Since > from && entry.IsDesktopDefault(profile))
                {
                    entry.Apply(profile);
                    applied.Add(entry.Name);
                }
            }

            profile.ProfileVersion = CurrentVersion;

            GD.Print(
                $"[GUO] profile defaults: {(isNew ? "new" : "migrated")} {Platform.ToString().ToLowerInvariant()} profile v{from}->v{CurrentVersion}, "
                + (applied.Count == 0 ? "no fields changed" : $"set {string.Join(", ", applied)}")
            );

            return true;
        }

        /// <summary>
        /// The world size "Game window full size" gives: the client area, in
        /// client pixels, never smaller than the login screen.
        /// </summary>
        private static Point FullWindowSize(Profile profile)
        {
            Rectangle bounds = Client.Game != null ? Client.Game.ClientBounds : Rectangle.Empty;

            return new Point(Math.Max(LoginWidth, bounds.Width), Math.Max(LoginHeight, bounds.Height));
        }

        private static Entry BoolEntry(string name, Func<Profile, bool> get, Action<Profile, bool> set, bool value, int since = 1)
        {
            bool upstream = get(Upstream);

            return new Entry(name, since, p => get(p) == upstream, p => set(p, value));
        }

        private static Entry ByteEntry(string name, Func<Profile, byte> get, Action<Profile, byte> set, byte value, int since = 1)
        {
            byte upstream = get(Upstream);

            return new Entry(name, since, p => get(p) == upstream, p => set(p, value));
        }

        private static Entry IntEntry(string name, Func<Profile, int> get, Action<Profile, int> set, int value, int since = 1)
        {
            int upstream = get(Upstream);

            return new Entry(name, since, p => get(p) == upstream, p => set(p, value));
        }

        private static Entry PointEntry(string name, Func<Profile, Point> get, Action<Profile, Point> set, Func<Profile, Point> value, int since = 1)
        {
            Point upstream = get(Upstream);

            return new Entry(name, since, p => get(p) == upstream, p => set(p, value(p)));
        }
    }
}
