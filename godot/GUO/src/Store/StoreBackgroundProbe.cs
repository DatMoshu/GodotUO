// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Store;

/// <summary>Opt-in headless profile integration test; no game or UO data.</summary>
public sealed partial class StoreBackgroundProbe : Node
{
    public override void _Ready()
    {
        try
        {
            Settings.GlobalSettings.ProfilesPath = Path.Combine(ProjectSettings.GlobalizePath("res://../../build/store-background-probe"), Guid.NewGuid().ToString("N"));
            ProfileManager.Load("fixture", "fixture", "fixture");
            var profile = ProfileManager.CurrentProfile;
            profile.CanvasBackgroundMode = "video";
            profile.CanvasBackgroundPath = "user://store/test-background/1.0.0/loop.ogv";
            profile.CanvasBackgroundLowPower = true;
            using var client = StoreOptions.CreateClient(StoreAddress.Default);
            StorePack.Require(!client.BackgroundRemoved("other", "1.0.0"), "Unrelated pack changed profile");
            StorePack.Require(client.BackgroundRemoved("test-background", "1.0.0"), "Active pack was not reset");
            StorePack.Require(profile.CanvasBackgroundMode == "builtin-grey" && profile.CanvasBackgroundPath == "", "Runtime profile not reset");
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(ProfileManager.ProfilePath, "profile.json")), ProfileJsonContext.DefaultToUse.Profile);
            StorePack.Require(saved.CanvasBackgroundMode == "builtin-grey" && saved.CanvasBackgroundPath == "" && saved.CanvasBackgroundLowPower, "Saved profile reset/preferences incorrect");
            StorePack.Require(!client.BackgroundRemoved("test-background", "1.0.0"), "Repeated removal reset unrelated selection");
            profile.CanvasBackgroundMode = "builtin-wood";
            profile.CanvasBackgroundPath = "user://store/test-background/1.0.0/loop.ogv";
            StorePack.Require(!client.BackgroundRemoved("test-background", "1.0.0") && profile.CanvasBackgroundMode == "builtin-wood", "Inactive custom path overwrote built-in choice");
            GD.Print("[store background] PASS: active selection reset, saved profile, unrelated/repeated removal and low-power preference");
            GetTree().Quit(0);
        }
        catch (Exception e) { GD.PrintErr("[store background] FAIL: " + e); GetTree().Quit(1); }
    }
}
