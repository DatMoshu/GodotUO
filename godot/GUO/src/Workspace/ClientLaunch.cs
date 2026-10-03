// GUO addition, not a port. ADR-0032: how a client profile is started. Plain .NET (no Godot types), so the
// plans can be checked without starting anything ("dry run") and by tools/server_manager/tests.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GUO.Workspace;

/// <summary>What starting one client slot would run. Nothing is started by making one.</summary>
internal sealed class LaunchPlan
{
    public string Kind { get; set; }
    public string FileName { get; set; }
    public List<string> Arguments { get; set; } = new();
    public string WorkingDir { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();
    public string SlotDir { get; set; }

    /// <summary>The file the client's console is redirected to; null for an external client (GUO only starts it).</summary>
    public string Console { get; set; }

    /// <summary>The slot's process.json: PID, start time and executable of the exact process, as for a managed server.</summary>
    public string State => Path.Combine(SlotDir, "process.json");

    public ProcessStartInfo ToStartInfo()
    {
        var info = new ProcessStartInfo(FileName) { WorkingDirectory = WorkingDir, UseShellExecute = false, CreateNoWindow = Kind != ClientKinds.External };

        foreach (string a in Arguments)
        {
            info.ArgumentList.Add(a);
        }

        foreach (var (k, v) in Environment)
        {
            info.Environment[k] = v;
        }

        return info;
    }
}

internal static class ClientLaunch
{
    /// <summary>The environment variables a GUO client of ours must not inherit from the editor's own run.</summary>
    public static bool Inherited(string key) => key.StartsWith("UO_", StringComparison.Ordinal)
        && (key.Contains("PROBE", StringComparison.Ordinal) || key.StartsWith("UO_CONTENT_", StringComparison.Ordinal) || key == "UO_CUSTOM_DATA");

    /// <summary>
    /// The settings a GUO client (project or build) gets, all of them from the profile pair and the slot: the
    /// shard, the slot's cache, the base data and overlay, and the content store.
    /// </summary>
    public static Dictionary<string, string> GuoEnvironment(string host, int port, string slotDir, ClientProfile client, string contentStore, string contentLock)
    {
        var env = new Dictionary<string, string>
        {
            ["UO_SHARD_HOST"] = host,
            ["UO_SHARD_PORT"] = port.ToString(),
            ["UO_CACHE_DIR"] = Path.Combine(slotDir, "cache"),
            ["UO_CONTENT_STORE"] = contentStore,
            ["UO_WORKSPACE_DIR"] = Workspace.Root,
        };

        if (!string.IsNullOrWhiteSpace(client.BaseData))
        {
            env["UO_CLIENT_DATA"] = client.BaseData;
        }

        string overlay = ClientRegistry.OverlayFolder(client);

        if (overlay != null && File.Exists(Path.Combine(overlay, "guo_data.json")))
        {
            env["UO_CUSTOM_DATA"] = overlay;
        }

        if (!string.IsNullOrEmpty(contentLock))
        {
            env["UO_CONTENT_LOCK"] = contentLock;
        }

        return env;
    }

    /// <summary>An exported GUO build: its executable, started with the slot's cache and the settings above.</summary>
    public static LaunchPlan PlanBuild(ClientProfile client, string host, int port, string serverId, int slot, string contentStore, string contentLock)
    {
        RequireDesktop(client);

        if (client.Kind != ClientKinds.GuoBuild)
        {
            throw new InvalidOperationException("Not a guo-build client");
        }

        string slotDir = Workspace.RunSlot(serverId, client.Id, slot);
        var plan = new LaunchPlan
        {
            Kind = client.Kind, FileName = client.Program, SlotDir = slotDir,
            WorkingDir = string.IsNullOrWhiteSpace(client.WorkingDir) ? Path.GetDirectoryName(client.Program) : client.WorkingDir,
            Environment = GuoEnvironment(host, port, slotDir, client, contentStore, contentLock),
            Console = Workspace.ClientConsole(serverId, client.Id, slot),
        };
        plan.Arguments.AddRange(new[] { "--", "--cache-dir", Path.Combine(slotDir, "cache") });
        plan.Arguments.AddRange(client.Arguments);
        return plan;
    }

    /// <summary>
    /// A shard's own client or launcher. Its program, arguments (with {host} {port} {data} {slot} expanded) and
    /// working directory, and nothing else: no environment, no settings and no plugins are added.
    /// </summary>
    public static LaunchPlan PlanExternal(ClientProfile client, string host, int port, string serverId, int slot)
    {
        RequireDesktop(client);

        if (client.Kind != ClientKinds.External)
        {
            throw new InvalidOperationException("Not an external client");
        }

        string slotDir = Workspace.RunSlot(serverId, client.Id, slot);
        string Expand(string a) => a.Replace("{host}", host).Replace("{port}", port.ToString()).Replace("{data}", client.BaseData ?? "").Replace("{slot}", slotDir);
        var plan = new LaunchPlan
        {
            Kind = client.Kind, FileName = client.Program, SlotDir = slotDir,
            WorkingDir = string.IsNullOrWhiteSpace(client.WorkingDir) ? Path.GetDirectoryName(client.Program) : client.WorkingDir,
        };
        plan.Arguments.AddRange(client.Arguments.Select(Expand));
        return plan;
    }

    private static void RequireDesktop(ClientProfile client)
    {
        if (!ClientKinds.DesktopHost)
        {
            throw new PlatformNotSupportedException("A " + client.Kind + " client can only be started from a Windows, Linux or macOS desktop");
        }

        if (!File.Exists(client.Program))
        {
            throw new FileNotFoundException("The client's program does not exist: " + client.Program);
        }

        if (!string.IsNullOrWhiteSpace(client.WorkingDir) && !Directory.Exists(client.WorkingDir))
        {
            throw new DirectoryNotFoundException("The client's working folder does not exist: " + client.WorkingDir);
        }
    }

    /// <summary>
    /// Puts the client's plugins into the slot's settings.json (a GUO client reads them there). An external
    /// client is never given any. The rest of the file is kept as it is.
    /// </summary>
    public static void WritePlugins(string slotDir, ClientProfile client)
    {
        if (client.Kind == ClientKinds.External || client.Plugins.Length == 0)
        {
            return;
        }

        string file = Path.Combine(slotDir, "settings.json");
        JsonObject settings = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject() : new JsonObject();
        settings["plugins"] = new JsonArray(client.Plugins.Select(p => (JsonNode) JsonValue.Create(p)).ToArray());
        Workspace.WriteAtomic(file, System.Text.Encoding.UTF8.GetBytes(settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }
}
