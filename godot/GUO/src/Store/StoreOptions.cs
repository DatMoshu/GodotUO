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
    public static string Url => System.Environment.GetEnvironmentVariable("UO_STORE_URL") ?? "http://127.0.0.1:18865";
    public static StoreClient CreateClient() => new(Url, ProjectSettings.GlobalizePath("user://store"), PlatformDefaults.CurrentVersion);

    // Called from one marked block in Options. The existing Apply path remains
    // authoritative: a store choice fills the existing mode and path controls.
    public static Combobox Attach(Control section, Combobox original, List<CanvasBackgroundSettings> choices,
        List<string> titles, Action<string> setPath, Profile profile)
    {
        using var client = CreateClient();
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

