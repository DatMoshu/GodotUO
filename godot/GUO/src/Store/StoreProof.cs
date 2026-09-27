// SPDX-License-Identifier: BSD-2-Clause
using System;
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
