// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;

namespace GUO.Store;

/// <summary>Opt-in proof scene. Uses the ordinary Main screenshot flow; never takes focus.</summary>
public sealed partial class StoreProof : Node
{
    public override async void _Ready()
    {
        try
        {
            AddChild(GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate());
            for (int i = 0; i < 100; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // The login scene forces 640x480. Enlarge after it settles so the
            // installed background outside the login gump can actually be seen.
            GetWindow().Size = new Vector2I(1200, 800);
            ProfileManager.Load("store-proof", "store-proof", "store-proof");
            using var store = StoreOptions.CreateClient();
            var installed = store.Installed().FirstOrDefault(p => p.Kind == "background");
            if (installed != null)
            {
                string video = installed.Files.Keys.FirstOrDefault(p => p.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase));
                GUO.Renderer.CanvasBackground.Override = new GUO.Renderer.CanvasBackgroundSettings(
                    video == null ? GUO.Renderer.CanvasBackgroundMode.Image : GUO.Renderer.CanvasBackgroundMode.Video,
                    $"user://store/{installed.Id}/{installed.Version}/{video ?? installed.Preview}", 12, false);
            }
            var options = new OptionsGump(Client.Game.UO.World);
            options.ChangePage(3);
            UIManager.Add(options);
            GD.Print("[store proof] Options including installed background choices constructed successfully");
            if (System.Environment.GetEnvironmentVariable("GUO_STORE_PROOF_VIEW") == "store") StoreWindow.Open();
            else options.Dispose();
        }
        catch (Exception e)
        {
            GD.PrintErr("[store proof] FAIL " + e);
            GetTree().Quit(1);
        }
    }
}
