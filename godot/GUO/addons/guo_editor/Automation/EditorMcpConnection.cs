#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text.Json.Nodes;

/// <summary>The stdio connection supplied to ACP sessions. No credentials are logged or persisted.</summary>
internal static class EditorMcpConnection
{
    private static JsonArray _servers = new();

    // Capture Godot-backed configuration on the editor thread, not after ACP's worker awaits.
    internal static void Configure(bool enabled) => _servers = enabled ? BuildServers() : new JsonArray();

    internal static JsonArray Servers() => AiFeatures.Enabled ? (JsonArray)_servers.DeepClone() : new JsonArray();

    private static JsonArray BuildServers()
    {
        var servers = new JsonArray();
        string portText = EditorData.Setting("GUO_EDITOR_MCP_PORT", "");
        string token = Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_TOKEN");
        string python = EditorData.Setting("GUO_EDITOR_MCP_PYTHON", "");
        if (python.Length == 0) python = QueueClient.FindPython();
        string script = Path.Combine(EditorData.RepoRoot, "tools", "editor_mcp", "bridge.py");
        if (!int.TryParse(portText, out int port) || port < 1024 || port > 65535 || token == null || token.Length < 32 ||
            token.Length > 256 || token.Contains('\n') || token.Contains('\r') || python == null || !File.Exists(script)) return servers;
        servers.Add(new JsonObject
        {
            ["name"] = "guo-editor", ["command"] = python, ["args"] = new JsonArray(script),
            ["env"] = new JsonArray(
                new JsonObject { ["name"] = "GUO_EDITOR_MCP_PORT", ["value"] = portText },
                new JsonObject { ["name"] = "GUO_EDITOR_MCP_TOKEN", ["value"] = token }),
        });
        return servers;
    }
}
#endif
