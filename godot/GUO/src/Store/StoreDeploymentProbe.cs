// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Assets;
using Environment = System.Environment;

namespace GUO.Store;

public partial class StoreDeploymentProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            GetWindow().Unfocusable = true;
            GetWindow().Size = Environment.GetEnvironmentVariable("UO_DEPLOYMENT_PROOF_COMPACT") == "1" ? new Vector2I(640, 480) : new Vector2I(900, 720);
            GetWindow().GuiEmbedSubwindows = true;
            string root = Environment.GetEnvironmentVariable("UO_CONTENT_STORE"), candidatePath = Environment.GetEnvironmentVariable("UO_CONTENT_LOCK");
            using var files = new UOFileManager(GUO.Host.UoDataProbe.ParseVersion("7.0.107.76"), Environment.GetEnvironmentVariable("UO_CLIENT_DATA"));
            files.Load(false, "enu", "", loadContent: false);
            string baseName = files.TileData.StaticData[3701].Name;
            var store = new StoreClient(StoreAddress.Default, root, GUO.Configuration.PlatformDefaults.CurrentVersion);
            var candidate = StoreContentLock.Read(candidatePath);
            var window = StoreDeploymentWindow.Open(this, candidate.Pack, candidate.Version, store, files);
            window.Import(candidatePath);
            await window.Select();
            string active = Path.Combine(root, ".active-content.json");
            StorePack.Require(File.Exists(active), "Deployment UI did not select validated content");
            byte[] selected = File.ReadAllBytes(active);
            StorePack.Require(files.TileData.StaticData[3701].Name == baseName && files.Content == null, "Validation changed the running assets");
            bool protectedPack = false;
            try { store.Uninstall("sample-content-art", "1.0.0"); } catch (InvalidDataException) { protectedPack = true; }
            StorePack.Require(protectedPack, "Selected dependency could be uninstalled");
            candidate.Bindings["sample-content-art:paperdoll"].Id = 124;
            string invalid = Path.Combine(root, "invalid-candidate.json");
            File.WriteAllText(invalid, JsonSerializer.Serialize(candidate));
            window.Import(invalid);
            await window.Select();
            StorePack.Require(File.ReadAllBytes(active).AsSpan().SequenceEqual(selected), "Invalid consumer bindings changed deployment");
            StorePack.Require(files.TileData.StaticData[3701].Name == baseName, "Invalid candidate mutated live metadata");
            StoreDeployment.Deactivate(active);
            StorePack.Require(!File.Exists(active) && File.Exists(active + ".previous"), "Original-assets recovery lost rollback");
            await window.Select(true);
            StorePack.Require(File.ReadAllBytes(active).AsSpan().SequenceEqual(selected), "UI rollback did not restore the validated deployment");
            Environment.SetEnvironmentVariable("UO_CONTENT_LOCK", null);
            try
            {
                using var restarted = new UOFileManager(files.Version, files.BasePath);
                restarted.Load(false, "enu");
                StorePack.Require(restarted.Content != null && restarted.TileData.StaticData[3701].Name == "Example stone", "Startup did not load the UI-selected deployment");
            }
            finally { Environment.SetEnvironmentVariable("UO_CONTENT_LOCK", candidatePath); }
            window.Import(candidatePath);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            string proof = Environment.GetEnvironmentVariable("UO_DEPLOYMENT_PROOF_IMAGE");
            if (!string.IsNullOrWhiteSpace(proof))
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                StorePack.Require(image.SavePng(proof) == Error.Ok, "Could not save deployment UI proof");
            }
            GD.Print("[deployment probe] PASS: real UI import/select/rollback and startup mount, dry-run isolation, consumer failure preservation, original-assets recovery and selected-dependency uninstall protection");
            window.QueueFree();
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("[deployment probe] FAIL: " + e); GetTree().Quit(1); }
    }
}
