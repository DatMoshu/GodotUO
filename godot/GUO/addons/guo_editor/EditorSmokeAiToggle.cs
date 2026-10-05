#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Environment = System.Environment;

public partial class EditorSmoke
{
    private async Task CheckAiToggleAsync()
    {
        var plugin = GetParent() as GuoEditorPlugin;
        var settings = EditorInterface.Singleton.GetEditorSettings();
        bool original = AiFeatures.ReadPreference();
        var staleTools = Ai.Hub.Tools;
        using var client = new AcpClient();
        bool started = client.Start(QueueClient.FindPython(),
            new[] { Path.Combine(EditorData.RepoRoot, "tools", "ai_hub", "fake_acp_agent.py") }, EditorData.RepoRoot, out string why);
        AiCheck("ai_toggle_live_agent_started", started, why);
        int pid = client.ProcessId;
        int chairsOnFirstBoot = GUO.Game.Data.ChairTable.Table.Count;
        _world.Host.Dispose();
        bool reopenedWorld = _world.EnsureBooted();
        AiCheck("world_same_assembly_reopen_without_duplicate_chairs", reopenedWorld
            && GUO.Game.Data.ChairTable.Table.Count == chairsOnFirstBoot);
        var currentOwner = Client.Game;
        var currentSettings = GUO.Configuration.Settings.GlobalSettings;
        var currentProfile = GUO.Configuration.ProfileManager.CurrentProfile;
        int chairsBefore = GUO.Game.Data.ChairTable.Table.Count;
        bool menuBefore = GUO.Renderer.PostFx.PostFxMenu.IsOpen;
        using (var contender = new WorldHost())
        {
            using var unparentedCanvas = new Node2D();
            bool refused = !contender.Boot(unparentedCanvas, 0, 0, 0, _data);
            contender.Dispose();
            AiCheck("world_contender_refused_without_shared_mutation", refused
                && ReferenceEquals(currentOwner, Client.Game)
                && ReferenceEquals(currentSettings, GUO.Configuration.Settings.GlobalSettings)
                && ReferenceEquals(currentProfile, GUO.Configuration.ProfileManager.CurrentProfile)
                && GUO.Game.Data.ChairTable.Table.Count == chairsBefore
                && GUO.Renderer.PostFx.PostFxMenu.IsOpen == menuBefore);
            // A Node is not reference counted; disposing its managed wrapper
            // alone would leave this deliberately unparented fixture orphaned.
            unparentedCanvas.Free();
        }
        using var loadEntered = new ManualResetEventSlim();
        using var releaseLoad = new ManualResetEventSlim();
        var retiringData = new EditorData
        {
            RetirementWait = TimeSpan.FromMilliseconds(10),
            AfterLoadControl = () => { loadEntered.Set(); releaseLoad.Wait(); },
        };
        int retiredNotifications = 0;
        retiringData.Loaded += () => retiredNotifications++;
        retiringData.LoadAsync();
        bool delayedLoadStarted = await Task.Run(() => loadEntered.Wait(TimeSpan.FromSeconds(5)));
        retiringData.Dispose();
        releaseLoad.Set();
        if (retiringData.LoadingTask != null) await retiringData.LoadingTask;
        await Delay(0.1);
        AiCheck("editor_data_retired_slow_load_no_publication", delayedLoadStarted
            && !retiringData.IsLoaded && retiringData.Files == null && retiredNotifications == 0);
        retiringData.Finish();
        AiCheck("editor_data_retired_queued_finish_no_publication", !retiringData.IsLoaded && retiredNotifications == 0);
        string stageControl = Path.Combine(_out, "stage_lifetime_control");
        Directory.CreateDirectory(stageControl);
        string externalControl = Path.Combine(_out, "stage_lifetime_external");
        Directory.CreateDirectory(externalControl);
        string targetControl = Path.Combine(externalControl, "target.bin");
        string mapControl = Path.Combine(stageControl, "files_override.txt");
        File.WriteAllText(targetControl, "fixture");
        File.WriteAllText(mapControl, "verdata.mul=" + targetControl);
        using (var leasedData = new EditorData())
        {
            leasedData.AcquireStageReadLeases(stageControl);
            AiCheck("editor_stage_selected_output_identified", leasedData.IsSelectedInputStage(stageControl));
            AiCheck("editor_stage_external_mapped_output_identified", leasedData.IsSelectedInputStage(externalControl));
            bool RefusesWrite(string path)
            {
                try { using var writer = File.Open(path, FileMode.Open, System.IO.FileAccess.Write, FileShare.Read); return false; }
                catch (IOException) { return true; }
            }
            AiCheck("editor_stage_mapping_and_bytes_write_refused", RefusesWrite(mapControl) && RefusesWrite(targetControl));
            leasedData.Dispose();
            AiCheck("editor_stage_leases_released_after_retirement", !RefusesWrite(mapControl) && !RefusesWrite(targetControl));
        }
        CancellationToken lifetime = AiFeatures.Lifetime;
        int port = 0;
        // Start the real plugin listener on an ephemeral free port, then prove the setting closes it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        string oldPort = Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_PORT");
        string oldToken = Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_TOKEN");
        try
        {
            settings.SetSetting(AiFeatures.SettingPath, false);
            plugin.ApplyAiPreference(false);
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PORT", port.ToString());
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_TOKEN", new string('t', 48));
            settings.SetSetting(AiFeatures.SettingPath, true);
            plugin.ApplyAiPreference(true);
            AiCheck("ai_toggle_mcp_started", plugin.AiMcpRunning);
            lifetime = AiFeatures.Lifetime;
            var commitEntered = new TaskCompletionSource<bool>();
            var finishCommit = new TaskCompletionSource<bool>();
            bool sawCancellation = false, latePublish = false;
            var activeHost = new AiToolHost(a => a());
            activeHost.Register(new AiToolHost.Tool
            {
                Name = "delayed_commit", RunAsync = async (_, token) =>
                {
                    commitEntered.SetResult(true);
                    await finishCommit.Task;
                    sawCancellation = token.IsCancellationRequested;
                    if (!sawCancellation) latePublish = true;
                    return "committed safely";
                },
            });
            Task<string> activeEdit = activeHost.RunAsync("delayed_commit", new JsonObject(), CancellationToken.None);
            await commitEntered.Task;
            Action queued = null;
            int mutations = 0;
            var waitingHost = new AiToolHost(a => queued = a);
            waitingHost.Register(new AiToolHost.Tool { Name = "edit", Run = _ => { mutations++; return "changed"; } });
            Task<string> waitingEdit = waitingHost.RunAsync("edit", new JsonObject(), CancellationToken.None);
            settings.SetSetting(AiFeatures.SettingPath, false);
            plugin.ApplyAiPreference(false);
            await Delay(0.1);
            AiCheck("ai_toggle_started_commit_waits_for_cleanup", !activeEdit.IsCompleted);
            finishCommit.SetResult(true);
            string activeResult = await activeEdit;
            AiCheck("ai_toggle_started_operation_receives_cancellation", sawCancellation);
            AiCheck("ai_toggle_canceled_commit_no_late_publish", !latePublish && activeResult.StartsWith("error:"));
            string cancelledEdit = await waitingEdit;
            queued?.Invoke();
            AiCheck("ai_toggle_blocks_queued_mutation", mutations == 0 && cancelledEdit.StartsWith("error:"));
            AiCheck("ai_toggle_saved_preference", !AiFeatures.ReadPreference());
            AiCheck("ai_toggle_hides_dock", !plugin.AiRunning && Ai == null);
            AiCheck("ai_toggle_hides_generation", !Art.AiControlsVisible);
            AiCheck("ai_toggle_cancels_lifetime", lifetime.IsCancellationRequested);
            AiCheck("ai_toggle_kills_agent", !started || !Alive(pid));
            AiCheck("ai_toggle_no_mcp", !plugin.AiMcpRunning && EditorMcpConnection.Servers().Count == 0);
            var rebind = new TcpListener(IPAddress.Loopback, port);
            try { rebind.Start(); AiCheck("ai_toggle_mcp_port_released", true); }
            finally { rebind.Stop(); }
            Search.Index.FinishNow();
            AiCheck("ai_toggle_no_search_commands", Search.Index.Provider<AiProvider>() == null
                && !Search.Index.Query("AI: new chat").SelectMany(g => g.Items).Any(i => i.Entry.Kind == "AI"));
            AiCheck("ai_toggle_manual_editor_available", GuoEditorPlugin.AssetsMain != null
                && GuoEditorPlugin.MultiEditMain != null && Search.Index.Query("backpack").Count > 0);
            using var blocked = new AcpClient();
            AiCheck("ai_toggle_blocks_process", !blocked.Start("must-not-run", Array.Empty<string>(), "", out _));
            int opened = 0;
            AiCheck("ai_toggle_blocks_scanner", new SessionScanner(opened: _ => opened++).Scan().Count == 0 && opened == 0);
            var (id, error) = await new QueueClient().PostAsync("nobody", "smoke", "must not post");
            AiCheck("ai_toggle_blocks_queue", id == 0 && error.Contains("disabled"));
            using var imageProvider = new RetroDiffusionProvider("http://127.0.0.1:1", () => throw new Exception("key must not be read"));
            var image = await imageProvider.RunAsync(new ImageRequest(), _ => { }, CancellationToken.None);
            AiCheck("ai_toggle_blocks_generation", image.Error.Contains("disabled"));
            using var chatProvider = new OllamaProvider("http://127.0.0.1:1");
            bool chatBlocked = false, toolBlocked = false;
            try { await chatProvider.ListModelsAsync(CancellationToken.None); }
            catch (InvalidOperationException) { chatBlocked = true; }
            try { await staleTools.RunAsync("editor_state", new JsonObject(), CancellationToken.None); }
            catch (InvalidOperationException) { toolBlocked = true; }
            AiCheck("ai_toggle_blocks_http", chatBlocked);
            AiCheck("ai_toggle_blocks_stale_tool", toolBlocked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PORT", oldPort);
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_TOKEN", oldToken);
            settings.SetSetting(AiFeatures.SettingPath, original);
            plugin.ApplyAiPreference(original);
            Search.Index.FinishNow();
        }
        AiCheck("ai_toggle_reenable", AiFeatures.Enabled && plugin.AiRunning && Art.AiControlsVisible
            && Search.Index.Provider<AiProvider>() != null);
    }
}
#endif
