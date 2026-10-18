#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Real loopback protocol, approval, main-thread dispatch and shutdown checks. No model calls.</summary>
public partial class EditorSmoke
{
    private async Task CheckEditorMcpAsync()
    {
        await CheckMcpAccessAsync();
        int editorThread = Environment.CurrentManagedThreadId;
        int mutations = 0;
        var host = new AiToolHost(Ai.Hub.Post);
        host.Register(new AiToolHost.Tool { Name = "read_probe", Run = _ => Environment.CurrentManagedThreadId == editorThread ? "main thread" : "wrong thread" });
        host.Register(new AiToolHost.Tool { Name = "write_probe", ReadOnly = false, Run = _ => { mutations++; return "changed"; } });
        string token = new('x', 48);
        var server = new EditorMcpServer(host, 0, token);
        int port = server.Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync("127.0.0.1", port, deadline.Token);
                await bad.GetStream().WriteAsync(Encoding.UTF8.GetBytes("wrong\n"), deadline.Token);
                byte[] b = new byte[1];
                bool rejected;
                try { rejected = await bad.GetStream().ReadAsync(b, deadline.Token) == 0; }
                catch (IOException) { rejected = true; } // Windows may reset a refused connection.
                AiCheck("editor_mcp_bad_auth", rejected);
            }
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, deadline.Token);
            NetworkStream stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(token);
            int id = 0;
            async Task<JsonNode> Call(string method, JsonObject p = null)
            {
                await writer.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = p }.ToJsonString());
                return JsonNode.Parse(await reader.ReadLineAsync(deadline.Token));
            }
            JsonNode early = await Call("tools/list");
            AiCheck("editor_mcp_initialize_required", (int?)early["error"]?["code"] == -32600);
            JsonNode init = await Call("initialize");
            AiCheck("editor_mcp_initialize", (string)init["result"]?["serverInfo"]?["name"] == "guo-editor");
            JsonNode list = await Call("tools/list");
            AiCheck("editor_mcp_catalog", list["result"]?["tools"] is JsonArray { Count: 2 } catalog && (bool?)catalog[1]["annotations"]?["readOnlyHint"] == false);
            JsonObject Tool(string name) => new() { ["name"] = name, ["arguments"] = new JsonObject() };
            JsonNode read = await Call("tools/call", Tool("read_probe"));
            AiCheck("editor_mcp_main_thread", (string)read["result"]?["content"]?[0]?["text"] == "main thread");
            JsonNode refused = await Call("tools/call", Tool("write_probe"));
            AiCheck("editor_mcp_no_approver_refused", (bool?)refused["result"]?["isError"] == true && mutations == 0);
            host.Approve = _ => Task.FromResult(true);
            JsonNode changed = await Call("tools/call", Tool("write_probe"));
            AiCheck("editor_mcp_approved_write", (bool?)changed["result"]?["isError"] == false && mutations == 1);
            JsonNode unknown = await Call("tools/call", Tool("unknown"));
            AiCheck("editor_mcp_unknown_tool", (int?)unknown["error"]?["code"] == -32602);
            await writer.WriteLineAsync("{broken");
            JsonNode parse = JsonNode.Parse(await reader.ReadLineAsync(deadline.Token));
            AiCheck("editor_mcp_parse_error", (int?)parse["error"]?["code"] == -32700);
            AiCheck("editor_mcp_parse_recovers", (await Call("ping"))["result"] is JsonObject);
        }
        finally { server.Dispose(); }
        // The old listener is gone and the same port is available after reload/disposal.
        using (var rebound = new EditorMcpServer(host, port, token)) AiCheck("editor_mcp_listener_released", rebound.Port == port);
        var queued = new Queue<Action>();
        var cancelledHost = new AiToolHost(a => queued.Enqueue(a));
        cancelledHost.Register(new AiToolHost.Tool { Name = "queued", Run = _ => { mutations++; return "changed"; } });
        using var cancel = new CancellationTokenSource();
        Task<string> pending = cancelledHost.RunAsync("queued", null, cancel.Token);
        cancel.Cancel();
        await pending;
        queued.Dequeue()();
        AiCheck("editor_mcp_cancelled_queue_skipped", mutations == 1);
        string state = await Ai.Hub.Tools.RunAsync("editor_state", new JsonObject(), CancellationToken.None);
        AiCheck("editor_mcp_live_state", JsonNode.Parse(state)?["assetsReady"]?.GetValue<bool>() == true);
        string search = await Ai.Hub.Tools.RunAsync("editor_search", new JsonObject { ["query"] = "backpack" }, CancellationToken.None);
        AiCheck("editor_mcp_f3_shared", JsonNode.Parse(search)?["entries"] is JsonArray { Count: > 0 });
        var sensitiveReply = new JsonObject { ["account"] = "test", ["password"] = "never-export", ["nested"] = new JsonObject { ["token"] = "never-export" } };
        string safeReply = EditorAutomationCapabilities.Safe(sensitiveReply).ToJsonString();
        AiCheck("editor_mcp_admin_reply_redacted", !safeReply.Contains("never-export") && (string)sensitiveReply["password"] == "never-export");
        string safeArgs = EditorAutomationCapabilities.ArgumentsForLog("admin_settings_edit", new JsonObject { ["id"] = "mail.password", ["value"] = "never-log" });
        AiCheck("editor_mcp_admin_settings_args_redacted", !safeArgs.Contains("never-log"));
        foreach (string section in new[] { "health", "godview", "settings", "accounts", "backups", "commands" })
        {
            string adminState = await Ai.Hub.Tools.RunAsync("admin_state", new JsonObject { ["section"] = section, ["limit"] = 1 }, CancellationToken.None);
            AiCheck("editor_mcp_admin_" + section, (string)JsonNode.Parse(adminState)?["section"] == section);
        }
        bool traversalRefused = false;
        try { EditorCapabilities.MultiPath("../outside.json"); }
        catch (ArgumentException) { traversalRefused = true; }
        AiCheck("editor_mcp_path_guard", traversalRefused);
    }

    private async Task CheckMcpAccessAsync()
    {
        string originalRoot = GUO.Workspace.Workspace.RootOverride;
        string scripted = Environment.GetEnvironmentVariable("GUO_EDITOR_SCRIPTED");
        string allowed = Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED");
        var settings = Godot.EditorInterface.Singleton.GetEditorSettings();
        try
        {
            GUO.Workspace.Workspace.RootOverride = Path.Combine(Path.GetTempPath(), "guo-mcp-access-" + Guid.NewGuid().ToString("N"));
            string scratchRoot = GUO.Workspace.Workspace.RootOverride;
            EditorMcpAccess.Forget();
            EditorMcpAccess.InitializeSettings(settings);
            int dispatched = 0;
            var host = new AiToolHost(Ai.Hub.Post);
            string[] tools = { "editor_state", "admin_view", "admin_connect", "admin_save", "admin_restart", "admin_settings_edit", "admin_account", "admin_command", "admin_godview" };
            for (int i = 0; i < tools.Length; i++)
            {
                string name = tools[i];
                host.Register(new AiToolHost.Tool { Name = name, ReadOnly = i == 0, Run = _ => { dispatched++; return "ran"; } });
                EditorMcpAccess.Group g = EditorMcpAccess.Groups[i];
                AiCheck("mcp_access_default_" + g.Id, EditorMcpAccess.Enabled(g.Id) == (i < 3));
                string result = await host.RunAsync(name, new JsonObject(), CancellationToken.None, true);
                AiCheck("mcp_access_dispatch_" + g.Id, i < 3 ? result == "ran" : result.StartsWith("refused:") && result.Contains(EditorMcpAccess.Prefix + g.Id));
            }
            AiCheck("mcp_access_defaults_no_mutation", dispatched == 3);
            EditorMcpAccess.Set("commands", true);
            EditorMcpAccess.Forget(); // Re-read the file, as a new editor/reload would.
            AiCheck("mcp_access_persisted", EditorMcpAccess.Enabled("commands"));
            var audit = EditorMcpAccess.AuditRows;
            AiCheck("mcp_access_audit", audit.Count == 1 && audit[0].Op == "editor_mcp_access" && audit[0].Args.Contains("guo/mcp_access/commands") && audit[0].Args.Contains("true"));
            AiCheck("mcp_access_explicit_allow", await host.RunAsync("admin_command", new JsonObject(), CancellationToken.None, true) == "ran");
            Environment.SetEnvironmentVariable("GUO_EDITOR_SCRIPTED", "1");
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED", "admin_command,admin_save");
            AiCheck("mcp_access_cli_cannot_widen", (await host.RunAsync("admin_save", new JsonObject(), CancellationToken.None, true, true)).Contains("guo/mcp_access/save_backup_create"));
            AiCheck("mcp_access_cli_intersection", await host.RunAsync("admin_command", new JsonObject(), CancellationToken.None, true, true) == "ran");
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED", "admin_save");
            host.Approve = _ => Task.FromResult(true);
            AiCheck("mcp_access_cli_narrows", (await host.RunAsync("admin_command", new JsonObject(), CancellationToken.None, false, true)).Contains("outside --allow-editor-tool"));
            int beforeRead = dispatched;
            AiCheck("mcp_access_cli_read_denied", (await host.RunAsync("editor_state", new JsonObject(), CancellationToken.None, true, true)).Contains("outside --allow-editor-tool") && dispatched == beforeRead);
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED", "editor_state");
            AiCheck("mcp_access_cli_read_allowed", await host.RunAsync("editor_state", new JsonObject(), CancellationToken.None, false, true) == "ran");
            EditorMcpAccess.Set("read_state", false);
            AiCheck("mcp_access_cli_read_cannot_widen", (await host.RunAsync("editor_state", new JsonObject(), CancellationToken.None, true, true)).Contains("guo/mcp_access/read_state"));
            EditorMcpAccess.Set("read_state", true);
            Environment.SetEnvironmentVariable("GUO_EDITOR_SCRIPTED", null);
            host.Approve = _ => { EditorMcpAccess.Set("commands", false); return Task.FromResult(true); };
            AiCheck("mcp_access_rechecked_after_approval", (await host.RunAsync("admin_command", new JsonObject(), CancellationToken.None, false)).Contains("guo/mcp_access/commands"));
            settings.SetSetting(EditorMcpAccess.Prefix + "commands", true);
            EditorMcpAccess.SettingsChanged(settings);
            AiCheck("mcp_access_ui_toggle_persisted", EditorMcpAccess.Enabled("commands") && EditorMcpAccess.AuditRows.Count == 5);
            int callbacks = 0, readDispatches = 0;
            var queuedRead = new AiToolHost(callback => Ai.Hub.Post(() =>
            {
                if (++callbacks == 2) EditorMcpAccess.Set("read_state", false);
                callback();
            }));
            queuedRead.Register(new AiToolHost.Tool { Name = "editor_state", ReadOnly = true, Run = _ => { readDispatches++; return "ran"; } });
            AiCheck("mcp_access_read_revoked_in_queue", (await queuedRead.RunAsync("editor_state", new JsonObject(), CancellationToken.None, false, true)).Contains("guo/mcp_access/read_state") && callbacks == 2 && readDispatches == 0);
            EditorMcpAccess.Set("read_state", true);
            AiCheck("mcp_access_godview_reads", EditorMcpAccess.Refusal("admin_godview", new JsonObject { ["action"] = "find" }, false, false) == null);
            AiCheck("mcp_access_godview_mutation", EditorMcpAccess.Refusal("admin_godview", new JsonObject { ["action"] = "press", ["text"] = "Bring here" }, false, false)?.Contains("godview_actions") == true);
            AiFeatures.Apply(false);
            AiCheck("mcp_access_ai_off", (await host.RunAsync("admin_command", new JsonObject(), CancellationToken.None, true, true)).Contains(AiFeatures.DisabledMessage));
            AiFeatures.Apply(true);
            // Only the uniquely created test folder is removed.
            Directory.Delete(scratchRoot, true);
        }
        finally
        {
            AiFeatures.Apply(true);
            GUO.Workspace.Workspace.RootOverride = originalRoot;
            Environment.SetEnvironmentVariable("GUO_EDITOR_SCRIPTED", scripted);
            Environment.SetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED", allowed);
            EditorMcpAccess.Forget();
            EditorMcpAccess.InitializeSettings(settings);
        }
    }
}
#endif
