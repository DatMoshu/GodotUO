#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The smoke stage for the UO Store tab (ADR-0026 section 8). Nothing here reaches the internet, the
/// shared dev shard or the game profile: a local signed catalogue is started from
/// tools/editor_smoke/store_fixture.py, the editor's store is a folder under the smoke output, and
/// the deployment goes to a scratch ModernUO-shaped folder (the real deploy path, no shard process).
/// Browse (approve the key, search, details, install, remove), Publish (verify a good and a bad
/// pack, publish to a temp store, prepare a catalogue PR) and Server content (dry run, deploy,
/// status) are driven through the same methods the tab's buttons call.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _storeReport = new();
    private readonly Stopwatch _storeClock = new();
    private Task _storeTask;

    private void StoreFail(string why)
    {
        _storeReport["ok"] = false;
        _failures.Add($"Store: {why}");
        GD.Print($"[GUO editor] smoke Store FAIL: {why}");
    }

    private void StoreCheck(string name, bool ok, string detail = "")
    {
        _storeReport[name] = ok;
        if (!ok)
        {
            StoreFail($"{name} {detail}".Trim());
        }
    }

    /// <summary>One tick of the stage; true when finished.</summary>
    private bool StepStore()
    {
        if (System.Environment.GetEnvironmentVariable("GUO_STORE_SKIP") != null || Store == null)
        {
            return true;
        }

        if (_storeTask == null)
        {
            _storeReport["ok"] = true;
            _storeTask = RunStoreAsync();
            _storeClock.Restart();
        }

        if (_storeTask.IsCompleted)
        {
            if (_storeTask.IsFaulted)
            {
                StoreFail($"threw {_storeTask.Exception?.GetBaseException().GetType().Name}: {_storeTask.Exception?.GetBaseException().Message}");
            }

            _report["store"] = _storeReport;
            return true;
        }

        if (_storeClock.Elapsed.TotalSeconds > 400)
        {
            StoreFail("the stage did not finish within 400 s");
            _report["store"] = _storeReport;
            return true;
        }

        return false;
    }

    private async Task<bool> StoreUntil(Func<bool> done, double seconds)
    {
        double t = 0;
        while (!done() && t < seconds)
        {
            await Delay(0.1);
            t += 0.1;
        }

        return done();
    }

    private async Task RunStoreAsync()
    {
        string python = QueueClient.FindPython();
        string fixture = Path.Combine(EditorData.RepoRoot, "tools", "editor_smoke", "store_fixture.py");
        string dir = Path.Combine(_out, "store");
        if (python == null || !File.Exists(fixture))
        {
            StoreFail($"python or the fixture is missing (python: {python}, fixture: {fixture})");
            return;
        }

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);

        // --- the local signed test catalogue ---------------------------------------------------------------------
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = EditorData.RepoRoot,
        };
        psi.ArgumentList.Add(fixture);
        psi.ArgumentList.Add(Path.Combine(dir, "fixture"));
        using var server = Process.Start(psi);
        try
        {
            Task<string> first = Task.Run(() =>
            {
                string line;
                while ((line = server.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("FIXTURE ", StringComparison.Ordinal))
                    {
                        return line.Substring(8);
                    }
                }

                return null;
            });
            _ = server.StandardError.ReadToEndAsync();
            if (!await StoreUntil(() => first.IsCompleted, 90) || first.Result == null)
            {
                StoreFail("the test catalogue did not start");
                return;
            }

            JsonNode fx = JsonNode.Parse(first.Result);
            string url = (string)fx["url"], key = (string)fx["key"], fingerprint = (string)fx["fingerprint"], zips = (string)fx["zips"], bad = (string)fx["bad"];
            _storeReport["catalogue"] = url;
            await RunStoreChecksAsync(url, key, fingerprint, zips, bad, dir);
        }
        finally
        {
            try
            {
                if (!server.HasExited)
                {
                    server.Kill(true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    private async Task RunStoreChecksAsync(string url, string key, string fingerprint, string zips, string bad, string dir)
    {
        var bench = new StoreBench(Path.Combine(dir, "installed")) { SkipOfficial = true };
        Store.UseBench(bench);
        StoreBrowseTab browse = Store.Browse;
        StorePublishTab publish = Store.Publish;
        StoreServerTab server = Store.Server;
        if (browse == null || publish == null || server == null)
        {
            StoreFail("a section of the UO Store tab does not exist");
            return;
        }

        string where = bench.Root;

        // --- Browse: a new catalogue asks for its key, exactly as the game's Store window does ------------------
        browse.AddCatalogueUrl(url);
        StoreCheck("browse_waits_for_approval", await StoreUntil(() => !browse.IsBusy && browse.PendingApprovals == 1, 30) && browse.EntryCount == 0, browse.Status);
        StoreCheck("browse_shows_fingerprint", browse.PendingFingerprint == fingerprint, $"{browse.PendingFingerprint} vs {fingerprint}");
        browse.ApproveFirstPending();
        StoreCheck("browse_lists_packs_after_approval", await StoreUntil(() => !browse.IsBusy && browse.EntryCount >= 9, 30), browse.Status);
        _storeReport["browse_status"] = browse.Status;

        browse.SetSearch("combined");
        StoreCheck("browse_search", browse.ShownCount == 1, $"{browse.ShownCount} shown");
        browse.Select("sample-content-combined");
        bool details = await StoreUntil(() => browse.Details.Contains("Kind: content"), 20);
        StoreCheck("browse_details", details && browse.Details.Contains("Target: combined") && browse.Details.Contains("signature verified")
            && browse.Details.Contains("Depends on: nothing") && browse.Details.Contains("checked when installed"), browse.Details);

        // install and remove a sample pack (the standalone art pack), then install the deploy candidate
        browse.SetSearch("");
        browse.Select("sample-content-art");
        await Delay(0.3);
        await browse.InstallSelectedAsync();
        StoreCheck("install_pack", browse.IsInstalled("sample-content-art") && Directory.Exists(Path.Combine(where, "sample-content-art", "1.0.0")), browse.Status);
        browse.Select("sample-content-art");
        StoreCheck("installed_policy_verdict", await StoreUntil(() => browse.Details.Contains("Content policy (automatic): pass"), 30), browse.Details);
        await browse.RemoveSelectedAsync();
        StoreCheck("remove_pack", !browse.IsInstalled("sample-content-art") && !Directory.Exists(Path.Combine(where, "sample-content-art", "1.0.0")), browse.Status);

        browse.Select("sample-content-server");
        await Delay(0.3);
        await browse.InstallSelectedAsync();
        StoreCheck("install_pulls_dependencies", browse.IsInstalled("sample-content-server") && browse.IsInstalled("sample-content-art"), browse.Status);
        browse.SetSearch("");
        browse.Select("sample-content-combined");
        await Delay(0.3);
        await browse.InstallSelectedAsync();
        StoreCheck("install_deploy_candidate", browse.IsInstalled("sample-content-combined"), browse.Status);
        StoreCheck("store_is_project_local", where.Contains("store") && !where.Contains("user://") && File.Exists(Path.Combine(where, ".catalogues.json")), where);

        // --- Publish: verify a good pack and a bad one, publish to a temp store, prepare a PR --------------------
        string good = Path.Combine(zips, "sample-content-combined.zip");
        await publish.VerifyAsync(good);
        StoreCheck("verify_good_pack", publish.Verdict == "pass" && publish.ReportText.Contains("Hashes: every payload") && publish.ReportText.Contains("Schema guo/store-pack@2") && publish.CanPublish, publish.ReportText);
        await publish.VerifyAsync(bad);
        StoreCheck("verify_refuses_bad_pack", publish.Verdict == "refused" && !publish.CanPublish && publish.ReportText.Contains("tool.png"), publish.ReportText);
        await publish.VerifyAsync(good);
        string temp = Path.Combine(dir, "publish_store");
        publish.SetStoreFolder(temp);
        await publish.PublishAsync();
        StoreCheck("publish_local", File.Exists(Path.Combine(temp, "packs", "sample-content-combined", "1.0.0.zip")) && publish.Log.Contains("Published sample-content-combined"), publish.Log);
        string pr = Path.Combine(dir, "pr");
        await publish.PrepareAsync("https://example.org/sample-content-combined-1.0.0.zip", "Procedural CC0 starter art, no client files", pr);
        string listing = Path.Combine(pr, "packs", "sample-content-combined", "1.0.0.json");
        StoreCheck("prepare_catalogue_pr", File.Exists(listing) && publish.Log.Contains("gh pr create --repo DatMoshu/GodotUO-packs") && publish.Log.Contains("no signing key"), publish.Log);

        // --- Server content: a profile, a pack, slots; dry run, deploy, status -----------------------------------
        string shard = Path.Combine(dir, "shard");
        Directory.CreateDirectory(Path.Combine(shard, "Data"));
        var profile = new ServerProfile { Name = "Smoke ModernUO", Backend = "modernuo", Host = "127.0.0.1", Port = 2614, ServerDirectory = shard };
        var list = new ServerProfiles { Selected = profile.Id, Servers = new() { profile } };
        server.ProfilesOverride = list;
        server.Shown();
        StoreCheck("server_lists_profile", server.ProfileCount == 1 && server.SelectProfile("Smoke ModernUO"), $"{server.ProfileCount}");
        StoreCheck("server_selects_pack", server.SelectPack("sample-content-combined") && server.BindingCount == 1, $"{server.BindingCount} bindings");
        server.SetBinding("sample-content-combined:stone", 6001);
        await server.DeployAsync(true);
        string export = Path.Combine(shard, "Data", "GUO", "server-content.json");
        StoreCheck("deploy_dry_run", server.Log.Contains("dry run") && !File.Exists(export), server.Log);
        await server.DeployAsync(false);
        string descriptor = Path.Combine(shard, "Data", "GUO", "public", "shard-content.json");
        bool wrote = File.Exists(export) && File.Exists(descriptor);
        string identity = null;
        if (wrote)
        {
            JsonNode e = JsonNode.Parse(File.ReadAllText(export)), d = JsonNode.Parse(File.ReadAllText(descriptor));
            identity = (string)e["identity_hash"];
            wrote = (string)e["schema"] == "guo/server-content@1" && (string)d["schema"] == "guo/shard-content@1"
                && (string)d["lock"]["identity_hash"] == identity && (int)e["items"][0]["graphic"] == 6001
                && (string)d["catalogues"][0]["key"] == key;
        }

        StoreCheck("deploy_writes_descriptor_and_export", wrote, server.Log);
        StoreCheck("deploy_log_result", server.Log.Contains("Result: deployed"), server.Log);
        await server.RefreshStatusAsync();
        StoreCheck("deployed_status", identity != null && server.DeployedText.Contains("sample-content-combined") && server.DeployedText.Contains(identity[..16]), server.DeployedText);
        _storeReport["deployment_identity"] = identity;
        _storeReport["deploy_log_tail"] = string.Join(" | ", server.Log.Split('\n').Where(l => l.Contains("deployment") || l.Contains("catalogue")).Take(3));

        // The private shard, when this checkout has one: only the dry run (nothing written, no restart).
        string privateShard = Path.Combine(EditorData.RepoRoot, "build", "shard_private");
        if (File.Exists(Path.Combine(privateShard, "state.json")))
        {
            var priv = new ServerProfile { Name = "Private shard", Backend = "modernuo", Host = "127.0.0.1", Port = 2614, ServerDirectory = privateShard };
            list.Servers.Add(priv);
            server.Shown();
            server.SelectProfile("Private shard");
            await server.DeployAsync(true);
            StoreCheck("private_shard_dry_run", server.Log.Contains("dry run"), server.Log);
        }
        else
        {
            _storeReport["private_shard_dry_run"] = "skipped: no build/shard_private in this checkout";
        }

        server.ProfilesOverride = null;
        browse.SetSearch("");
    }
}
#endif
