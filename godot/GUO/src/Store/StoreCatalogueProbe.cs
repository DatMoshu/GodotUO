// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace GUO.Store;

/// <summary>A hosted catalogue as the client meets it (STORE-01, data_formats section 12): a new catalogue asks for
/// approval of its key, then lists signed packs from every host its index names, then installs one. It uses a
/// scratch trust list and store, never the player's. --store-catalogue-probe URL, from tools/asset_store/hosted_proof.py --serve.</summary>
public static class StoreCatalogueProbe
{
    public const string Flag = "--store-catalogue-probe";

    internal static bool Run(string url)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "guo-catalogue-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var trust = new StoreTrust(Path.Combine(temporary, ".catalogues.json"));
            // Off the main thread: Godot's synchronization context would deadlock a blocking wait on the HTTP calls.
            string summary = Task.Run(async () =>
            {
                using var client = new StoreClient(url, temporary, int.MaxValue) { Trust = trust };
                string fingerprint;
                try { await client.FetchIndex(); throw new InvalidDataException("a new catalogue was listed without approval"); }
                catch (StoreTrustRequired pending)
                {
                    StorePack.Require(!pending.KeyChanged, "a new catalogue was reported as a changed key");
                    trust.Approve(pending.Pending);
                    fingerprint = pending.Fingerprint;
                }
                var entries = await client.FetchIndex();
                StorePack.Require(entries.Count > 0 && entries.All(e => e.Signed), "the approved catalogue listed no signed packs");
                var hosts = entries.SelectMany(e => e.Urls).Select(u => u.Authority).Distinct().Count();
                var pick = entries.OrderByDescending(e => StorePack.Version(e.Manifest.Version)).First();
                string folder = await client.InstallWithDependencies(pick, entries);
                StorePack.Require(Directory.Exists(folder), "the installed pack has no folder");
                return $"{client.CatalogueTitle}, key {fingerprint}: {entries.Count} signed pack(s) from {hosts} host(s); installed {pick.Manifest.Id} {pick.Manifest.Version}";
            }).GetAwaiter().GetResult();
            GD.Print("[store catalogue] PASS: " + summary);
            return true;
        }
        catch (Exception ex) { GD.PrintErr("[store catalogue] FAIL: " + ex.Message); return false; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
