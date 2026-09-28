// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Utility.Logging;

namespace GUO.Platform.Android
{
    /// <summary>
    /// The second screen's settings as the running client sees them: which
    /// gumps the shelf takes, the shelf's pixel scale, and whether the
    /// shelf is used at all. Read and written by the pre-game card on the
    /// second screen, by Options, and by <see cref="DualScreen"/> each frame.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no second screen. The values live
    /// in the profile (Profile.DualScreen*, PlatformDefaults v7); this class
    /// is what makes them reachable from the login screen, where no profile
    /// is loaded yet. Before a profile exists it reads the newest saved
    /// profile.json for its keys, the same path the canvas background takes
    /// (Renderer.CanvasBackground, ADR-0016), so the second screen comes up
    /// the way the last session left it. An edit made on the login screen is
    /// kept for the session and written into the profile the player then
    /// logs into, where the profile's own save keeps it. With a profile
    /// loaded, reads and writes go straight to it.
    /// </remarks>
    internal static class DualScreenSettings
    {
        /// <summary>Largest shelf scale the panel is worth; 0 means "as the main screen".</summary>
        public const int MaxScale = 3;

        /// <summary>One set of values; the profile's shape, without the profile.</summary>
        public sealed class Values
        {
            public bool Enabled = true;
            public bool Paperdoll = true;
            public bool Backpack = true;
            public bool Status = true;
            public bool Journal = true;
            public bool Others;
            public int Scale;
            public int ScalePercent;

            public Values Clone() => (Values) MemberwiseClone();

            public static Values From(Profile p) => new Values
            {
                Enabled = p.DualScreenEnabled,
                Paperdoll = p.DualScreenShelvePaperdoll,
                Backpack = p.DualScreenShelveBackpack,
                Status = p.DualScreenShelveStatus,
                Journal = p.DualScreenShelveJournal,
                Others = p.DualScreenShelveOthers,
                Scale = Math.Clamp(p.DualScreenScale, 0, MaxScale),
                ScalePercent = Math.Clamp(p.DualScreenScalePercent, 0, MaxScale * 100),
            };

            public void Into(Profile p)
            {
                p.DualScreenEnabled = Enabled;
                p.DualScreenShelvePaperdoll = Paperdoll;
                p.DualScreenShelveBackpack = Backpack;
                p.DualScreenShelveStatus = Status;
                p.DualScreenShelveJournal = Journal;
                p.DualScreenShelveOthers = Others;
                p.DualScreenScale = Scale;
                p.DualScreenScalePercent = ScalePercent;
            }
        }

        private static Values _session;
        private static bool _sessionEdited;
        private static bool _preProfileRead;
        private static Profile _carriedInto;

        /// <summary>
        /// The values in force now. With a profile loaded, the profile's;
        /// before that, this session's edits, else the last saved profile's,
        /// else the defaults a new profile would get.
        /// </summary>
        public static Values Current
        {
            get
            {
                Profile profile = ProfileManager.CurrentProfile;

                if (profile != null)
                {
                    CarryIntoProfileOnce(profile);

                    return Values.From(profile);
                }

                if (!_preProfileRead)
                {
                    _preProfileRead = true;
                    _session = ReadMostRecentlySavedProfile() ?? new Values();
                }

                return _session.Clone();
            }
        }

        /// <summary>Set the values in force; into the profile if there is one, else for the session.</summary>
        public static void Set(Values values)
        {
            Profile profile = ProfileManager.CurrentProfile;

            if (profile != null)
            {
                values.Into(profile);

                return;
            }

            _preProfileRead = true;
            _session = values.Clone();
            _sessionEdited = true;
        }

        /// <summary>Change one value in place; see <see cref="Set"/>.</summary>
        public static void Edit(Action<Values> change)
        {
            Values v = Current;
            change(v);
            Set(v);
        }

        /// <summary>
        /// Once per loaded profile: what the player set on the login screen
        /// goes into the profile they logged into. Without an edit, the
        /// profile's own values stand, and the session copy is dropped so
        /// the next login screen (after a log out) reads the profile again.
        /// </summary>
        private static void CarryIntoProfileOnce(Profile profile)
        {
            if (ReferenceEquals(_carriedInto, profile))
            {
                return;
            }

            _carriedInto = profile;

            if (_sessionEdited && _session != null)
            {
                _session.Into(profile);
                GD.Print("[GUO] dual screen: login-screen settings carried into the profile");
            }

            _sessionEdited = false;
            _session = null;
            _preProfileRead = false;
        }

        /// <summary>
        /// The newest profile.json under the profiles root, read only for its
        /// second-screen keys, and brought through PlatformDefaults in memory
        /// so a profile saved before v7 reads as it will once it is loaded.
        /// </summary>
        private static Values ReadMostRecentlySavedProfile()
        {
            try
            {
                string root = string.IsNullOrWhiteSpace(Settings.GlobalSettings?.ProfilesPath)
                    ? Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles")
                    : Settings.GlobalSettings.ProfilesPath;

                if (!Directory.Exists(root))
                {
                    return null;
                }

                string newest = Directory
                    .EnumerateFiles(root, "profile.json", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null)
                {
                    return null;
                }

                Profile profile = ConfigurationResolver.Load<Profile>(newest, ProfileJsonContext.DefaultToUse.Profile);

                if (profile == null)
                {
                    return null;
                }

                Values values = Values.From(profile);

                if (profile.ProfileVersion < 7)
                {
                    // What PlatformDefaults v7 will set when this profile is
                    // loaded; done here by hand so nothing is logged or
                    // saved for a profile that is only being peeked at.
                    values.Paperdoll = values.Backpack = values.Status = values.Journal = true;
                }

                return values;
            }
            catch (Exception ex)
            {
                Log.Warn($"dual screen: could not read the last profile: {ex.Message}");

                return null;
            }
        }
    }
}
