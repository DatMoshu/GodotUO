// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Renderer;
using Control = GUO.Game.UI.Controls.Control;

namespace GUO.Store;

internal static class StoreOptions
{
    public static string Url => StoreAddress.Load(ProfileManager.CurrentProfile == null ? null : ProfileManager.ProfilePath,
        System.Environment.GetEnvironmentVariable("UO_STORE_URL") ?? StoreAddress.Default);
    public static StoreClient CreateClient(string url = null) => new(url ?? Url, Root, PlatformDefaults.CurrentVersion)
    { BackgroundRemoved = ResetRemovedBackground, Trust = Trust };

    /// <summary>The installed packs: user://store, or UO_CONTENT_STORE (a probe's own folder).</summary>
    public static string Root
    {
        get
        {
            string root = System.Environment.GetEnvironmentVariable("UO_CONTENT_STORE");
            return string.IsNullOrWhiteSpace(root) ? ProjectSettings.GlobalizePath("user://store") : root;
        }
    }

    /// <summary>The installation's approved catalogue keys (ADR-0026), beside the installed packs.</summary>
    public static StoreTrust Trust => _trust ??= new StoreTrust(System.IO.Path.Combine(Root, ".catalogues.json"));
    private static StoreTrust _trust;

    /// <summary>Installed screensaver packs, as (loop path, title) for Options.</summary>
    public static IEnumerable<(string, string)> InstalledScreensavers()
    {
        using var client = CreateClient(StoreAddress.Default);
        foreach (var m in client.Installed().Where(m => m.Kind == "screensaver").OrderBy(m => m.Title).ThenBy(m => StorePack.Version(m.Version)))
            yield return ($"user://store/{m.Id}/{m.Version}/{StorePack.ScreensaverLoop(m)}", $"Store: {m.Title} ({m.Version})");
    }

    /// <summary>The folders of installed screen-effect packs (ADR-0023), for PostFxLibrary.</summary>
    public static IEnumerable<string> InstalledPostFxFolders()
    {
        using var client = CreateClient(StoreAddress.Default);
        foreach (var m in client.Installed().Where(m => m.Kind == "postfx").OrderBy(m => m.Title))
            yield return ProjectSettings.GlobalizePath($"user://store/{m.Id}/{m.Version}");
    }

    private static bool ResetRemovedBackground(string id, string version)
    {
        var profile = ProfileManager.CurrentProfile;
        // The chosen screensaver goes back to the effects when its pack goes.
        if (profile != null && StoreBackground.BelongsTo(profile.ScreenSaverChoice, id, version))
        {
            profile.ScreenSaverChoice = "effects";
            ProfileManager.Save(profile, ProfileManager.ProfilePath);
        }
        if (profile == null || !StoreBackground.BelongsTo(profile.CanvasBackgroundPath, id, version)) return false;
        var mode = CanvasBackgroundSettings.FromProfile(profile).Mode;
        if (mode is not (CanvasBackgroundMode.Image or CanvasBackgroundMode.Video or CanvasBackgroundMode.Frames)) return false;
        profile.CanvasBackgroundMode = "builtin-grey";
        profile.CanvasBackgroundPath = "";
        ProfileManager.Save(profile, ProfileManager.ProfilePath);
        return true;
    }

    // Called from one marked block in Options. The existing Apply path remains
    // authoritative: a store choice fills the existing mode and path controls.
    public static Combobox Attach(Control section, Combobox original, List<CanvasBackgroundSettings> choices,
        List<string> titles, Action<string> setPath, Profile profile)
    {
        // Installed content remains available when the saved endpoint is invalid/offline.
        using var client = CreateClient(StoreAddress.Default);
        foreach (var m in client.Installed().Where(m => m.Kind == "background").OrderBy(m => m.Title).ThenBy(m => StorePack.Version(m.Version)))
        {
            string media = m.Files.Keys.FirstOrDefault(p => p.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase));
            bool video = media != null && !profile.CanvasBackgroundLowPower;
            media = video ? media : m.Preview;
            string file = $"user://store/{m.Id}/{m.Version}/{media}";
            choices.Add(new CanvasBackgroundSettings(video ? CanvasBackgroundMode.Video : CanvasBackgroundMode.Image, file, 12, profile.CanvasBackgroundLowPower));
            titles.Add($"Store: {m.Title} ({m.Version})");
        }
        int selected = original.SelectedIndex;
        for (int i = 0; i < choices.Count; i++)
            if (choices[i].Path.StartsWith("user://store/", StringComparison.Ordinal) && choices[i].Path == profile.CanvasBackgroundPath) selected = i;
        var replacement = new Combobox(original.X, original.Y, original.Width, titles.ToArray(), selected, font: 0xFF);
        var parent = original.Parent;
        parent.Add(replacement, original.Page);
        parent.Remove(original); original.Dispose();
        replacement.OnOptionSelected += (_, index) =>
        {
            if (choices[index].Path.StartsWith("user://store/", StringComparison.Ordinal)) setPath(choices[index].Path);
        };
        section.Add(new StoreButton());
        return replacement;
    }

    private sealed class StoreButton : NiceButton
    {
        public StoreButton() : base(0, 0, 170, 28, ButtonAction.Activate, "Store...") { IsSelectable = false; }
        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left) StoreWindow.Open();
        }
    }
}
