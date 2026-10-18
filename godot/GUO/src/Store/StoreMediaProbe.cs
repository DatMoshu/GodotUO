// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Store;

/// <summary>Unimplemented consumers refuse activation. Voice is covered by StoreAudioProbe, HD by StoreHdProbe.</summary>
public partial class StoreMediaProbe : Node
{
    public override void _Ready()
    {
        string temporary = null;
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            StorePack.Require(args.Length == 1, "Pass the shared media corpus folder after --");
            string corpus = Path.GetFullPath(args[0]);
            temporary = Path.Combine(Path.GetDirectoryName(corpus), "media-refusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            foreach (var (label, type) in new[] { ("splat", "splat") })
            {
                string stage = Path.Combine(temporary, "stage-" + label);
                Directory.CreateDirectory(stage);
                var manifest = StorePack.Extract(Path.Combine(corpus, "media-" + label + "-install.zip"), stage);
                string destination = Path.Combine(temporary, manifest.Id, manifest.Version);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                Directory.Move(stage, destination);
                using var store = new StoreClient("http://127.0.0.1:18865", temporary, int.MaxValue);
                var snapshot = store.VerifyContent(manifest.Id, manifest.Version);
                var contentLock = new StoreContentLock { Pack = manifest.Id, Version = manifest.Version, IdentityHash = snapshot.IdentityHash };
                bool refused = false;
                try { using var content = StoreRuntimeContent.Load(null, "enu", contentLock, store, apply: false); }
                catch (InvalidDataException ex) when (ex.Message == "No client consumer for " + type) { refused = true; }
                StorePack.Require(refused, type + " unexpectedly activated or failed for the wrong reason");
                GD.Print("[media probe] " + type + ": installed inertly, runtime activation refused");
            }
            GD.Print("[media probe] PASS: two unimplemented media consumers refused");
            GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("[media probe] FAIL: " + ex); GetTree().Quit(1); }
        finally { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
