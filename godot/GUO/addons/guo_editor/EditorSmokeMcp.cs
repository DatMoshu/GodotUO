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
        bool traversalRefused = false;
        try { EditorCapabilities.MultiPath("../outside.json"); }
        catch (ArgumentException) { traversalRefused = true; }
        AiCheck("editor_mcp_path_guard", traversalRefused);
    }
}
#endif
