// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Text.Json;
using Godot;
using GUO.Compat;
using GUO.Configuration;

namespace GUO.Tests;

/// <summary>Explicit headless scene; never loaded by the game.</summary>
public sealed partial class ProfileMigrationProbe : Node
{
    private int _checks;
    private void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Save(Profile p) => JsonSerializer.Serialize(p, ProfileJsonContext.DefaultToUse.Profile);
    private static Profile Load(string json) => JsonSerializer.Deserialize(json, ProfileJsonContext.DefaultToUse.Profile);

    public override void _Ready()
    {
        try
        {
            var platform = PlatformDefaults.Platform;
            bool desktop = platform == ProfilePlatform.Desktop;
            bool mobile = platform == ProfilePlatform.Mobile;
            for (int from = 4; from <= 10; from++)
            {
                // Sparse on-disk fixtures exercise the real generated JSON context,
                // including absent fields and property initializers from Profile.cs.
                Profile p = Load($"{{\"profile_version\":{from},\"grid_loot_type\":2}}");
                Check(p.ProfileVersion == from, $"v{from}: fixture version was not read");
                Check(PlatformDefaults.Apply(p, false), $"v{from}: migration not reported");
                Check(p.ProfileVersion == PlatformDefaults.CurrentVersion, $"v{from}: wrong final version");
                Check(p.GameWindowFullSize, $"v{from}: absent fullscreen field lost current default");
                Check(p.EnableMousewheelScaleZoom, $"v{from}: absent wheel field lost current default");
                Check(p.SaveScaleAfterClose == (desktop && from < 5), $"v{from}: v5 saved-scale gate");
                Check(p.GameWindowPosition == (desktop && from < 5 ? new Point(-5, -5) : Point.Zero), $"v{from}: v5 position gate");
                Check(p.GameWindowSize == (desktop && from < 5 ? new Point(640, 480) : new Point(600, 480)), $"v{from}: v5 headless size fallback");
                Check(p.CanvasBackgroundLowPower == (!desktop && from < 6), $"v{from}: v6 low-power gate");
                Check(p.DualScreenShelvePaperdoll == (from < 7) && p.DualScreenShelveBackpack == (from < 7)
                    && p.DualScreenShelveStatus == (from < 7) && p.DualScreenShelveJournal == (from < 7), $"v{from}: v7 shelf gate");
                Check(p.GridLootType == (mobile && from < 8 ? 1 : 2), $"v{from}: v8 old mobile grid value");
                Check(p.TouchMacroRow == (mobile && from < 9), $"v{from}: v9 mobile macro-row gate");
                Check(p.ScreenSaver == (mobile && from < 10), $"v{from}: v10 screen-saver platform default");
                Check(p.ScreenSaverChoice == "effects", $"v{from}: v11 screen-saver choice default");
                Check(p.ScreenSaverMinutes == 10, $"v{from}: screen-saver timeout default");
                string saved = Save(p);
                Profile reloaded = Load(saved);
                Check(!PlatformDefaults.Apply(reloaded, false), $"v{from}: migration repeated after reload");
                Check(Save(reloaded) == saved, $"v{from}: reload changed migrated settings");

                Profile custom = new Profile { ProfileVersion = from, GameWindowSize = new Point(901, 701),
                    GameWindowPosition = new Point(13, 27), CanvasBackgroundMode = "image",
                    CanvasBackgroundPath = "user://custom-background.png", CanvasBackgroundFps = 24,
                    CanvasBackgroundLowPower = true, GridLootType = 0, ContainersScale = 155,
                    GameWindowFullSize = false, EnableMousewheelScaleZoom = false,
                    TouchMacroRow = true, ScreenSaver = true, ScreenSaverMinutes = 23,
                    ScreenSaverChoice = "user://store/test-saver/1.0.0/loop.ogv" };
                PlatformDefaults.Apply(custom, false);
                Check(custom.GameWindowSize == new Point(901, 701) && custom.GameWindowPosition == new Point(13, 27), $"v{from}: custom window overwritten");
                Check(custom.CanvasBackgroundMode == "image" && custom.CanvasBackgroundPath == "user://custom-background.png"
                    && custom.CanvasBackgroundFps == 24 && custom.CanvasBackgroundLowPower, $"v{from}: custom background overwritten");
                Check(custom.GridLootType == 0 && custom.ContainersScale == 155, $"v{from}: custom loot/container overwritten");
                Check(!custom.GameWindowFullSize && !custom.EnableMousewheelScaleZoom, $"v{from}: saved fullscreen/wheel opt-out overwritten");
                Check(custom.TouchMacroRow && custom.ScreenSaver && custom.ScreenSaverMinutes == 23
                    && custom.ScreenSaverChoice == "user://store/test-saver/1.0.0/loop.ogv",
                    $"v{from}: custom macro/screen-saver settings overwritten");
                string customJson = Save(custom);
                Check(Save(Load(customJson)) == customJson, $"v{from}: custom settings lost in JSON round trip");
            }
            foreach (int version in new[] { PlatformDefaults.CurrentVersion, PlatformDefaults.CurrentVersion + 1 })
            {
                Profile p = new Profile { ProfileVersion = version, TouchMacroRow = false, GridLootType = 2,
                    ScreenSaver = false, ScreenSaverMinutes = 37 };
                string before = Save(p);
                Check(!PlatformDefaults.Apply(p, false) && Save(p) == before, $"v{version}: current/future profile changed");
            }
            // The on-disk key, and a v10 choice written by hand, survive the v11 step.
            Profile keyed = Load("{\"profile_version\":10,\"screen_saver\":true,\"screen_saver_choice\":\"builtin:embers\"}");
            PlatformDefaults.Apply(keyed, false);
            Check(keyed.ProfileVersion == PlatformDefaults.CurrentVersion && keyed.ScreenSaver && keyed.ScreenSaverChoice == "builtin:embers",
                "v10: screen_saver_choice key lost in v11 migration");
            Check(Save(keyed).Contains("\"screen_saver_choice\": \"builtin:embers\"") || Save(keyed).Contains("\"screen_saver_choice\":\"builtin:embers\""),
                "screen_saver_choice not written under its snake_case key");
            Check(!PlatformDefaults.Apply(null, false), "Null profile changed");
            Profile fresh = new Profile();
            Check(PlatformDefaults.Apply(fresh, true), "New profile not initialized");
            Check(fresh.TouchMacroRow == mobile && fresh.ScreenSaver == mobile && fresh.ScreenSaverMinutes == 10
                && fresh.ScreenSaverChoice == "effects",
                "New profile macro/screen-saver platform defaults");
            GD.Print($"PROFILE MIGRATIONS PASS: {platform}, v4-v10 to v{PlatformDefaults.CurrentVersion}, {_checks} assertions");
            GetTree().Quit(0);
        }
        catch (Exception e)
        {
            GD.PrintErr("PROFILE MIGRATIONS FAIL: " + e);
            GetTree().Quit(1);
        }
    }
}
